namespace ModernWigiDash.Sdk;

/// <summary>
/// A diagnostic log cadence: fires on the first call and then every Nth call
/// (or only every Nth). One rule, one test — replaces the hand-rolled modulo
/// counters that scattered "log at most once per N events" across the frame
/// pipeline (transport touch diagnostics, bulk-write diagnostics, delivery
/// send/failure logs).
/// </summary>
public sealed class LogCadence
{
    private readonly int _interval;
    private readonly bool _logFirst;
    private int _count;

    /// <param name="interval">Cadence: fire on every Nth call.</param>
    /// <param name="logFirst">Also fire on the very first call — for failure
    /// logs, where the first occurrence must not be silent.</param>
    public LogCadence(int interval, bool logFirst = false)
    {
        _interval = Math.Max(1, interval);
        _logFirst = logFirst;
    }

    /// <summary>True when this call is on cadence: every Nth call, plus the
    /// first call when the first-log flag is set. Thread-safe.</summary>
    public bool Due()
    {
        int count = Interlocked.Increment(ref _count);
        return (_logFirst && count == 1) || count % _interval == 0;
    }
}
