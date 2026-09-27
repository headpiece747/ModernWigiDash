using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

/// <summary>
/// The reconnect backoff policy (2026-09-27). A pure function of the failure
/// count, so every rule is pinned here without a timer, a clock, or a device.
///
/// The properties that matter, in the order a reader should check them: the
/// first retry is unchanged from the old flat period, the delay grows, it never
/// exceeds the ceiling however long the device has been gone, it never goes
/// backwards or non-positive, and the first attempt is not penalised for the
/// count being zero or negative (a fresh engine).
/// </summary>
[TestClass]
public class ReconnectBackoffPolicyTests
{
    [TestMethod]
    public void DelayFor_FirstFailure_IsTheFastFirstRetry()
    {
        // The common case must not regress: a cable reseat or a resume from
        // sleep still gets the old 5 s.
        Assert.AreEqual(TimeSpan.FromSeconds(5), ReconnectBackoffPolicy.DelayFor(1));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(int.MinValue)]
    public void DelayFor_NoFailuresYet_IsTheFirstDelay(int failures)
    {
        // A fresh engine (0) and a defensively negative count both mean "nothing
        // has failed yet", so neither may produce a zero or negative delay that
        // would spin the timer.
        Assert.AreEqual(ReconnectBackoffPolicy.FirstDelay, ReconnectBackoffPolicy.DelayFor(failures));
    }

    [TestMethod]
    public void DelayFor_DoublesPerFailure()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), ReconnectBackoffPolicy.DelayFor(2));
        Assert.AreEqual(TimeSpan.FromSeconds(20), ReconnectBackoffPolicy.DelayFor(3));
        Assert.AreEqual(TimeSpan.FromSeconds(40), ReconnectBackoffPolicy.DelayFor(4));
        Assert.AreEqual(TimeSpan.FromSeconds(80), ReconnectBackoffPolicy.DelayFor(5));
    }

    [TestMethod]
    public void DelayFor_SaturatesAtTheCeiling()
    {
        // A permanently absent display must settle at the ceiling, not grow
        // without bound: this is what turns a 5 s flood into half a probe a
        // minute. And the ceiling must hold for absurd counts too, so a long
        // absence cannot overflow the doubling into a nonsense interval.
        Assert.AreEqual(ReconnectBackoffPolicy.MaxDelay, ReconnectBackoffPolicy.DelayFor(6));
        Assert.AreEqual(ReconnectBackoffPolicy.MaxDelay, ReconnectBackoffPolicy.DelayFor(20));
        Assert.AreEqual(ReconnectBackoffPolicy.MaxDelay, ReconnectBackoffPolicy.DelayFor(1_000));
        Assert.AreEqual(ReconnectBackoffPolicy.MaxDelay, ReconnectBackoffPolicy.DelayFor(int.MaxValue));
    }

    [TestMethod]
    public void DelayFor_IsMonotonicAndPositive()
    {
        // The two invariants a timer depends on: the interval never decreases
        // (a decreasing period is a spin) and never reaches zero (a zero period
        // is a hot loop).
        TimeSpan previous = TimeSpan.Zero;
        for (int failures = 0; failures <= 64; failures++)
        {
            TimeSpan delay = ReconnectBackoffPolicy.DelayFor(failures);
            Assert.IsTrue(delay > TimeSpan.Zero, $"delay must be positive at {failures} failures");
            Assert.IsTrue(delay >= previous, $"delay must not shrink at {failures} failures");
            Assert.IsTrue(delay <= ReconnectBackoffPolicy.MaxDelay, $"delay must respect the ceiling at {failures} failures");
            previous = delay;
        }
    }

    [TestMethod]
    public void Ceiling_IsLongerThanTheFirstDelay()
    {
        // If the ceiling ever dropped to or below the first delay the backoff
        // would be a no-op that looks configured. Pin the relation, not the
        // numbers, so the policy can be retuned without breaking this.
        Assert.IsTrue(ReconnectBackoffPolicy.MaxDelay > ReconnectBackoffPolicy.FirstDelay);
    }
}
