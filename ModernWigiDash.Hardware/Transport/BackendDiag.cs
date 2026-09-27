namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Shared diagnostic cadence for the transport backends' bulk-write
/// diagnostics — one constant instead of mirrored per-backend numbers (the
/// DiagLog module exists precisely to replace hand-mirrored counters).
/// </summary>
internal static class BackendDiag
{
    /// <summary>Log every Nth bulk-write diagnostic line (both backends).</summary>
    public const int BulkWriteCadence = 30;

    /// <summary>
    /// How many connect attempts pass between full SetupAPI traces. The
    /// enumeration writes eight lines per attempt (GetClassDevs,
    /// EnumDeviceInterfaces, two detail calls, the device path, CreateFile,
    /// WinUsb_Initialize, the pipe timeouts) and used to write them EVERY
    /// attempt — with a 5 s reconnect that is ~100 lines a minute of the same
    /// failure, which is how a real incident buried its own evidence (the
    /// 2026-09-27 log: hundreds of identical blocks around the one line that
    /// mattered). The FIRST attempt always traces in full — that is the one
    /// being diagnosed — and after that the per-step trace repeats only every
    /// Nth attempt, while the attempt's VERDICT is logged every time by
    /// <see cref="ConnectVerdict"/>. One line per attempt, full call sequence
    /// still available when a reconnect is being chased.
    /// </summary>
    public const int ConnectTraceCadence = 5;
}
