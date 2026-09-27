using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.App;

/// <summary>
/// The USB badge's display mapping: engine state → (label, resource brush).
/// Connecting shares the danger brush with Disconnected (nothing is green
/// while the engine is still trying).
///
/// The three failure states get DIFFERENT labels because they call for
/// different things from the user, and one "Simulated" for all of them is what
/// made a busy-but-present display look like a missing one (the 2026-09-27
/// report). "In use" tells the user to close the other app; "Not responding"
/// tells them the device is there and wedged; "No display" tells them to check
/// the cable. None of those is actionable if the badge only says "Simulated".
/// AccentRed is kept for all three — on this theme it reads as amber, the
/// warning color — so the badge's color language is unchanged; only the words
/// became specific.
/// </summary>
internal static class UsbBadgeModel
{
    public static (string Label, string BrushKey) From(ConnectionState state) => state switch
    {
        ConnectionState.Connected => ("Connected", "AccentGreen"),
        ConnectionState.DeviceNotFound => ("No display", "AccentRed"),
        ConnectionState.DeviceBusy => ("In use", "AccentRed"),
        ConnectionState.DeviceNotResponding => ("Not responding", "AccentRed"),
        ConnectionState.Connecting => ("Connecting", "DangerBorder"),
        _ => ("Disconnected", "DangerBorder"),
    };
}
