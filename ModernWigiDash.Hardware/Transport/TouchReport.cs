namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Touch report data structure from the WigiDash display.
/// </summary>
public readonly record struct TouchReport
{
    public byte Type { get; init; }
    public short X { get; init; }
    public short Y { get; init; }
    public byte ScreenState { get; init; }
    public bool SleepState { get; init; }
}
