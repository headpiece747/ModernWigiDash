using ModernWigiDash.App.PresentMon;

namespace ModernWigiDash.Tests;

[TestClass]
public class TargetTrustPolicyTests
{
    private static PresentMonDynamicSample Sample(double fps = 143.2, double displayed = 142.8)
        => new(fps, 110.4, 4.0, 4.05, displayed, 2, 6.1, 4);

    [TestMethod]
    public void Decide_FirstTarget_ReturnsPoll()
    {
        var policy = new TargetTrustPolicy();

        Assert.AreEqual(TargetVerdict.Poll, policy.Decide([100]));
    }

    [TestMethod]
    public void Decide_SameTarget_ReturnsPollAndResetsStreak()
    {
        var policy = new TargetTrustPolicy();
        policy.NoteLive(100, Sample());

        Assert.AreEqual(TargetVerdict.Poll, policy.Decide([100]));

        policy.Decide([200]); // settling streak 1
        policy.Decide([200]); // settling streak 2
        Assert.AreEqual(TargetVerdict.Poll, policy.Decide([100]),
            "a returning target must clear the settling streak");
        Assert.AreEqual(0, policy.ForeignStreak);
    }

    [TestMethod]
    public void Decide_ForeignTarget_SettlesForTheWindowThenAdopts()
    {
        var policy = new TargetTrustPolicy();
        policy.NoteLive(100, Sample());

        Assert.AreEqual(TargetVerdict.TrackOnly, policy.Decide([200]), "settling poll 1");
        Assert.AreEqual(TargetVerdict.TrackOnly, policy.Decide([200]), "settling poll 2");
        Assert.AreEqual(TargetVerdict.Poll, policy.Decide([200]), "streak 3 = AdoptAfterPolls adopts");
        Assert.AreEqual(200, policy.LiveRootPid, "the adopted root becomes the live target");
        Assert.IsTrue(policy.CheckingAdopted, "the adopted target starts under the frozen-data guard");
    }

    [TestMethod]
    public void IsFrozenSample_NotCheckingAdopted_ReturnsFalse()
    {
        var policy = new TargetTrustPolicy();
        policy.NoteLive(100, Sample());

        Assert.IsFalse(policy.IsFrozenSample(Sample()), "without adoption the guard is off");
    }

    [TestMethod]
    public void IsFrozenSample_AdoptedMatchingKey_HoldsUntilItDiffers()
    {
        var policy = new TargetTrustPolicy();
        policy.NoteLive(100, Sample(fps: 143.2));
        policy.Decide([200]);
        policy.Decide([200]);
        policy.Decide([200]); // adoption — the guard key is the departed target's "143.2|142.8"

        Assert.IsTrue(policy.IsFrozenSample(Sample(fps: 143.2)),
            "the adopted target still reading the departed values is frozen data");
        Assert.IsTrue(policy.CheckingAdopted, "the guard holds while the sample matches");
        Assert.IsTrue(policy.IsFrozenSample(Sample(fps: 143.2)), "and again next poll");
    }

    [TestMethod]
    public void IsFrozenSample_AdoptedDifferentKey_ClearsGuard()
    {
        var policy = new TargetTrustPolicy();
        policy.NoteLive(100, Sample(fps: 143.2));
        policy.Decide([200]);
        policy.Decide([200]);
        policy.Decide([200]);

        Assert.IsTrue(policy.IsFrozenSample(Sample(fps: 143.2)));
        Assert.IsFalse(policy.IsFrozenSample(Sample(fps: 120.0)), "a differing sample clears the guard");
        Assert.IsFalse(policy.CheckingAdopted);
        Assert.IsFalse(policy.IsFrozenSample(Sample(fps: 120.0)), "the guard stays off");
    }

    [TestMethod]
    public void NoteLive_UpdatesLiveRootAndGuardKey()
    {
        var policy = new TargetTrustPolicy();
        policy.NoteLive(100, Sample(fps: 143.2));
        Assert.AreEqual(100, policy.LiveRootPid);

        policy.Decide([200]);
        policy.Decide([200]);
        policy.Decide([200]); // adoption — guard key = "143.2|142.8"

        policy.NoteLive(200, Sample(fps: 120.0));
        Assert.AreEqual(200, policy.LiveRootPid);
        Assert.IsFalse(policy.IsFrozenSample(Sample(fps: 120.0)),
            "after a live note, the target's own values are not the departed target's frozen data");
    }
}
