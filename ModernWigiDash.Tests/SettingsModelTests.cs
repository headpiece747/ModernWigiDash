namespace ModernWigiDash.Tests;

/// <summary>
/// The settings hub's display facts pinned without WPF: the group order,
/// the close-behavior vocabulary's lockstep with
/// <see cref="CloseBehaviorPolicy"/> (a renamed or hand-edited option fails
/// the pin), and the checked verdict's routing through the policy (an
/// absent or unknown value always lands on the default radio).
/// </summary>
[TestClass]
public class SettingsModelTests
{
    private readonly SettingsModel _model = new();

    [TestMethod]
    public void CloseBehaviors_ExposeExactlyThePolicyVocabulary_InOrder()
    {
        var values = SettingsModel.CloseBehaviors.Select(o => o.Value).ToList();
        CollectionAssert.AreEqual(
            new[] { CloseBehaviorPolicy.Quit, CloseBehaviorPolicy.HideToTray },
            values);
        foreach (var option in SettingsModel.CloseBehaviors)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(option.Label));
            Assert.IsFalse(string.IsNullOrWhiteSpace(option.Description));
        }
    }

    [TestMethod]
    public void Groups_AreAppearanceBehaviorProfile_InOrder()
    {
        var titles = SettingsModel.Groups.Select(g => g.Title).ToList();
        CollectionAssert.AreEqual(new[] { "Appearance", "Behavior", "Profile" }, titles);
        foreach (var group in SettingsModel.Groups)
            Assert.IsFalse(string.IsNullOrWhiteSpace(group.Description));
    }

    [TestMethod]
    public void CheckedCloseBehaviorFor_KnownValuesRoundTrip()
    {
        Assert.AreEqual(CloseBehaviorPolicy.Quit, _model.CheckedCloseBehaviorFor(CloseBehaviorPolicy.Quit));
        Assert.AreEqual(CloseBehaviorPolicy.HideToTray, _model.CheckedCloseBehaviorFor(CloseBehaviorPolicy.HideToTray));
    }

    [TestMethod]
    public void CheckedCloseBehaviorFor_AbsentOrUnknownLandsOnTheDefault()
    {
        Assert.AreEqual(CloseBehaviorPolicy.Default, _model.CheckedCloseBehaviorFor(null));
        Assert.AreEqual(CloseBehaviorPolicy.Default, _model.CheckedCloseBehaviorFor(""));
        Assert.AreEqual(CloseBehaviorPolicy.Default, _model.CheckedCloseBehaviorFor("QUIT"));
        Assert.AreEqual(CloseBehaviorPolicy.Default, _model.CheckedCloseBehaviorFor("bogus"));
    }

    [TestMethod]
    public void TelemetryPollingRates_ExposeSupportedIntervals_InOrder()
    {
        var periods = SettingsModel.TelemetryPollingRates.Select(o => o.Milliseconds).ToList();
        CollectionAssert.AreEqual(new[] { 250, 500, 1000, 2000 }, periods);
        foreach (var option in SettingsModel.TelemetryPollingRates)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(option.Label));
            Assert.IsFalse(string.IsNullOrWhiteSpace(option.Description));
        }
    }

    [TestMethod]
    public void ResolveTelemetryPollingRate_KnownValuesRoundTrip()
    {
        Assert.AreEqual(250, SettingsModel.ResolveTelemetryPollingRate(250));
        Assert.AreEqual(500, SettingsModel.ResolveTelemetryPollingRate(500));
        Assert.AreEqual(1000, SettingsModel.ResolveTelemetryPollingRate(1000));
        Assert.AreEqual(2000, SettingsModel.ResolveTelemetryPollingRate(2000));
    }

    [TestMethod]
    public void ResolveTelemetryPollingRate_UnknownOrInvalidLandsOnDefault500()
    {
        Assert.AreEqual(500, SettingsModel.ResolveTelemetryPollingRate(0));
        Assert.AreEqual(500, SettingsModel.ResolveTelemetryPollingRate(-1));
        Assert.AreEqual(500, SettingsModel.ResolveTelemetryPollingRate(300));
        Assert.AreEqual(500, SettingsModel.ResolveTelemetryPollingRate(9999));
    }
}
