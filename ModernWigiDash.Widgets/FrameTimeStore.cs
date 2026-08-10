using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// In-process cache of the latest frame-time snapshot polled from the
/// PresentMon Service (ADR-0003). The App's polling loop calls
/// <see cref="UpdateFromDto"/>; widgets read the cached snapshot on the render
/// thread. The store owns the staleness decision — consumers ask
/// <see cref="TryReadFresh"/> and cannot skip the check.
/// </summary>
public static class FrameTimeStore
{
    /// <summary>Default staleness window for frame-time data (~1s poll cadence).</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(5);

    private static StaticTelemetryStore<FrameTimeSnapshotDto> _store = CreateStore(TimeProvider.System);

    private static StaticTelemetryStore<FrameTimeSnapshotDto> CreateStore(TimeProvider timeProvider)
        => new(new FrameTimeSnapshotDto(), defaultMaxAge: DefaultMaxAge, timeProvider: timeProvider);

    /// <summary>
    /// Internal test seam: builds a store bound to a fake clock (and optional
    /// max age) so the facade freshness tests can drive time. The production
    /// singleton binds <see cref="TimeProvider.System"/> at construction.
    /// </summary>
    internal static StaticTelemetryStore<FrameTimeSnapshotDto> CreateStoreForTest(TimeProvider timeProvider, TimeSpan? maxAge = null)
        => new(new FrameTimeSnapshotDto(), maxAge ?? DefaultMaxAge, timeProvider);

    /// <summary>Internal test seam: installs the store behind the static
    /// read/update surface (see <see cref="CreateStoreForTest"/>).</summary>
    internal static StaticTelemetryStore<FrameTimeSnapshotDto> StoreForTest
    {
        get => _store;
        set => _store = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Returns the cached snapshot when it is fresh enough, else null. The
    /// staleness window and the clock bind at construction.
    /// </summary>
    public static FrameTimeSnapshotDto? TryReadFresh() => _store.TryReadFresh();

    /// <summary>
    /// Stores a snapshot. A default/empty producer timestamp is resolved to the
    /// store's receive time.
    /// </summary>
    public static void Update(FrameTimeSnapshotDto snapshot) => _store.Update(snapshot, snapshot.LastUpdate);

    /// <summary>
    /// Stores a snapshot from the producer, tolerating a null DTO (treated as
    /// the unavailable state). Keeps the null-tolerant entry point the poll
    /// loop and the tests rely on.
    /// </summary>
    public static void UpdateFromDto(FrameTimeSnapshotDto? dto)
        => _store.Update(dto ?? new FrameTimeSnapshotDto(), dto?.LastUpdate ?? default);

    /// <summary>
    /// Resets the cache to the unavailable state. Intended for test isolation.
    /// </summary>
    public static void Reset() => _store.Reset();
}
