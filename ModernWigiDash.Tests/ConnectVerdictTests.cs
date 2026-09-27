using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

/// <summary>
/// The connect-failure wording (2026-09-27). This is the single owner of the
/// user-facing text for a failed connect, so it is pinned twice over: every
/// state gets its OWN line, and no state but
/// <see cref="ConnectionState.DeviceNotFound"/> may claim the display is not
/// there. That second pin is the regression this whole change exists for — the
/// old code emitted one sentence, "No physical device found - running in
/// simulation mode", for every outcome, which is how a display that was present
/// and held by another app came to be reported as missing.
/// </summary>
[TestClass]
public class ConnectVerdictTests
{
    [TestMethod]
    public void Line_DeviceNotFound_SaysTheDisplayIsAbsent()
    {
        string line = ConnectVerdict.Line(ConnectionState.DeviceNotFound, 1);
        StringAssert.Contains(line, "No WigiDash interface found");
    }

    [TestMethod]
    public void Line_DeviceBusy_NamesTheOtherClientNotAMissingDevice()
    {
        string line = ConnectVerdict.Line(ConnectionState.DeviceBusy, 3);
        StringAssert.Contains(line, "USB open was denied");
        StringAssert.Contains(line, "another app holds the device");
        StringAssert.Contains(line, "attempt 3", "the repeat count tells a reader whether the backoff is working");
    }

    [TestMethod]
    public void Line_DeviceNotResponding_SaysItIsPresentAndMisbehaving()
    {
        string line = ConnectVerdict.Line(ConnectionState.DeviceNotResponding, 1);
        StringAssert.Contains(line, "found but did not complete initialization");
    }

    [TestMethod]
    [DataRow(ConnectionState.DeviceBusy)]
    [DataRow(ConnectionState.DeviceNotResponding)]
    public void Line_PresentButUnusable_NeverSaysNoInterfaceFound(ConnectionState state)
    {
        string line = ConnectVerdict.Line(state, 1);
        Assert.IsFalse(
            line.Contains("No WigiDash interface found", StringComparison.OrdinalIgnoreCase),
            $"a {state} verdict must not claim the interface is missing: {line}");
    }

    [TestMethod]
    public void Line_EveryState_ProducesItsOwnText()
    {
        // Two states sharing a sentence is how the truth got lost in the first
        // place, so distinctness is a tested property, not a convention.
        ConnectionState[] states =
        [
            ConnectionState.DeviceNotFound,
            ConnectionState.DeviceBusy,
            ConnectionState.DeviceNotResponding,
        ];
        string[] lines = [.. states.Select(s => ConnectVerdict.Line(s, 1))];
        Assert.AreEqual(lines.Length, lines.Distinct().Count(), "each failure state needs its own line");
    }
}
