namespace ModernWigiDash.Tests;

/// <summary>The log-on-change dedup rule behind the telemetry error surfaces.</summary>
[TestClass]
public class LogOnChangeTests
{
    [TestMethod]
    public void Changed_FirstNonNullMessage_Fires()
    {
        var dedup = new LogOnChange();

        Assert.IsTrue(dedup.Changed("boom"));
    }

    [TestMethod]
    public void Changed_FirstNullMessage_IsSilent()
    {
        var dedup = new LogOnChange();

        Assert.IsFalse(dedup.Changed(null), "Null means 'nothing to report' — silence until a real message arrives");
    }

    [TestMethod]
    public void Changed_Repeats_AreSilent_UntilTheMessageChanges()
    {
        var dedup = new LogOnChange();

        Assert.IsTrue(dedup.Changed("boom"));
        Assert.IsFalse(dedup.Changed("boom"), "Identical repeats must be suppressed");
        Assert.IsTrue(dedup.Changed("boom again"), "A changed message must fire");
        Assert.IsFalse(dedup.Changed("boom again"));
    }

    [TestMethod]
    public void Changed_HealthyGap_ReLogsSameMessage()
    {
        var dedup = new LogOnChange();

        Assert.IsTrue(dedup.Changed("boom"));
        Assert.IsFalse(dedup.Changed("boom"));
        Assert.IsTrue(dedup.Changed(null), "A healthy cycle (null) is a change and resets the state");
        Assert.IsFalse(dedup.Changed(null));
        Assert.IsTrue(dedup.Changed("boom"), "The same message after a healthy gap must fire again");
    }
}
