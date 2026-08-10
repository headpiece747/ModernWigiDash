namespace ModernWigiDash.Sdk;

/// <summary>
/// The shared shape of a process-wide producer store: owns a
/// <see cref="TelemetryStore{TRecord}"/> instance bound to the domain's empty
/// value and staleness window, and exposes the read / freshness / update /
/// reset surface. Domain store facades (e.g. <c>LhmSensorStore</c>,
/// <c>FrameTimeStore</c>) wrap one instance per record shape and keep only the
/// DTO-to-record mapping, so the staleness policy and its test surface are
/// declared exactly once instead of restated per domain.
/// </summary>
public sealed class StaticTelemetryStore<TRecord> where TRecord : class
{
    private readonly TelemetryStore<TRecord> _store;

    /// <param name="emptyValue">The record a freshly reset store exposes
    /// (e.g. the disconnected/unavailable sentinel of the domain).</param>
    /// <param name="defaultMaxAge">The staleness window used by
    /// <see cref="TryReadFresh"/>.</param>
    /// <param name="timeProvider">The clock used by <see cref="TryReadFresh"/>
    /// and <see cref="Update"/>. Defaults to <see cref="TimeProvider.System"/>.</param>
    public StaticTelemetryStore(TRecord emptyValue, TimeSpan defaultMaxAge, TimeProvider? timeProvider = null)
    {
        _store = new TelemetryStore<TRecord>(emptyValue, defaultMaxAge, timeProvider);
    }

    /// <summary>Returns the cached snapshot (never null — the store is
    /// initialized to the domain's empty value and Update only assigns
    /// non-null records).</summary>
    public TRecord ReadSnapshot() => _store.Current!;

    /// <summary>
    /// Returns the cached snapshot when it is fresh enough, else null. The
    /// staleness window and the clock bind at construction.
    /// </summary>
    public TRecord? TryReadFresh() => _store.TryReadFresh();

    /// <summary>
    /// Stores a record. A default/empty producer timestamp is resolved to the
    /// store's receive time.
    /// </summary>
    public void Update(TRecord record, DateTime producerTimestamp) => _store.Update(record, producerTimestamp);

    /// <summary>Resets the cache to the domain's empty value. Intended for test isolation.</summary>
    public void Reset() => _store.Reset();
}
