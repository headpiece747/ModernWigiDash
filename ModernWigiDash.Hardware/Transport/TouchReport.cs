
using System.Runtime.InteropServices;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Touch report data structure from the WigiDash display.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct TouchReport
{
    public byte Type { get; init; }
    public short X { get; init; }
    public short Y { get; init; }

    /// <summary>
    /// Maps the raw vendor protocol byte to the SDK touch vocabulary. This is
    /// the single normalization site for hardware touch: the App's direct-USB
    /// seam and the App's direct-USB engine both delegate here, so the App
    /// only ever sees <see cref="TouchEventType"/>. Protocol: None=0,
    /// Down=1 (contact + movement), Up=2 (release).
    /// </summary>
    public static TouchEventType ToEventType(byte rawType) => rawType switch
    {
        DisplayProtocolConstants.TouchTypeDown => TouchEventType.TouchDown,
        DisplayProtocolConstants.TouchTypeUp => TouchEventType.TouchUp,
        _ => TouchEventType.TouchMove
    };
}
