namespace ModernWigiDash.Sdk;

/// <summary>
/// One store-facade shape for the telemetry stores: a singleton cache with
/// staleness tracking, a null-tolerant producer write, and the test seams
/// (fake-clock store, install). LhmSensorStore and FrameTimeStore bind one
/// instance each with their record's empty value, staleness window, and
/// timestamp extractor — the 7-member pattern is declared once instead of
/// twice, and the write surface has one shape (no test-only twin).
/// </summary>
public sealed class TelemetryStoreFacade<TRecord> where TRecord : class
{
    private readonly TRecord _emptyValue;
    private readonly Func<TRecord, DateTime> _lastUpdateOf;
    private StaticTelemetryStore<TRecord> _store;

    /// <param name="emptyValue">The disconnected/unavailable state the store
    /// resets to and a null producer write falls back to.</param>
    /// <param name="defaultMaxAge">Default staleness window for the data.</param>
    /// <param name="lastUpdateOf">Extracts the producer timestamp from a
    /// record; a default/empty producer timestamp is resolved to the store's
    /// receive time.</param>
    public TelemetryStoreFacade(TRecord emptyValue, TimeSpan defaultMaxAge, Func<TRecord, DateTime> lastUpdateOf)
    {
        _emptyValue = emptyValue;
        _lastUpdateOf = lastUpdateOf;
        DefaultMaxAge = defaultMaxAge;
        _store = Create(TimeProvider.System, defaultMaxAge);
    }

    /// <summary>Default staleness window for the store's data.</summary>
    public TimeSpan DefaultMaxAge { get; }

    private StaticTelemetryStore<TRecord> Create(TimeProvider timeProvider, TimeSpan maxAge)
        => new(_emptyValue, defaultMaxAge: maxAge, timeProvider: timeProvider);

    /// <summary>
    /// Internal test seam: builds a store bound to a fake clock (and optional
    /// max age) so the facade freshness tests can drive time. The production
    /// singleton binds <see cref="TimeProvider.System"/> at construction.
    /// </summary>
    public StaticTelemetryStore<TRecord> CreateStoreForTest(TimeProvider timeProvider, TimeSpan? maxAge = null)
        => Create(timeProvider, maxAge ?? DefaultMaxAge);

    /// <summary>Internal test seam: installs the store behind the read/update
    /// surface (see <see cref="CreateStoreForTest"/>).</summary>
    public StaticTelemetryStore<TRecord> StoreForTest
    {
        get => _store;
        set => _store = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Returns the cached snapshot regardless of freshness. Only the inspector's
    /// live sensor picker uses this — it needs the full reading list even when
    /// stale; every other consumer must go through <see cref="TryReadFresh"/>.
    /// </summary>
    public TRecord ReadSnapshot() => _store.ReadSnapshot();

    /// <summary>
    /// Returns the cached snapshot when it is fresh enough, else null. The
    /// staleness window and the clock bind at construction.
    /// </summary>
    public TRecord? TryReadFresh() => _store.TryReadFresh();

    /// <summary>
    /// Stores a snapshot from the producer, tolerating a null record (treated
    /// as the disconnected/unavailable state). The single write entry point.
    /// </summary>
    public void UpdateFromDto(TRecord? dto)
        => _store.Update(dto ?? _emptyValue, dto is null ? default : _lastUpdateOf(dto));

    /// <summary>
    /// Resets the cache to the disconnected state. Intended for test isolation.
    /// </summary>
    public void Reset() => _store.Reset();
}
