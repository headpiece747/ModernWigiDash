using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.App;

/// <summary>
/// The USB badge's display mapping: engine state → (label, resource brush).
/// Connecting shares the danger brush with Disconnected (nothing is green
/// while the engine is still trying); Simulated keeps AccentRed, which reads
/// as "amber" on this theme.
/// </summary>
public static class UsbBadgeModel
{
    public static (string Label, string BrushKey) From(ConnectionState state) => state switch
    {
        ConnectionState.Connected => ("Connected", "AccentGreen"),
        ConnectionState.Simulated => ("Simulated", "AccentRed"),
        ConnectionState.Connecting => ("Connecting", "DangerBorder"),
        _ => ("Disconnected", "DangerBorder"),
    };
}
