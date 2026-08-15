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
}
