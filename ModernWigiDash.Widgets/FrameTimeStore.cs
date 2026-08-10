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

    private static readonly StaticTelemetryStore<FrameTimeSnapshotDto> Store = new(
        new FrameTimeSnapshotDto(),
        defaultMaxAge: DefaultMaxAge);

    /// <summary>
    /// Returns the cached snapshot when it is fresh enough, else null. The
    /// freshness decision uses the producer timestamp with an injectable clock.
    /// </summary>
    public static FrameTimeSnapshotDto? TryReadFresh(TimeSpan? maxAge = null, TimeProvider? timeProvider = null)
        => Store.TryReadFresh(maxAge, timeProvider);

    /// <summary>
    /// Stores a snapshot. A default/empty producer timestamp is resolved to the
    /// store's receive time.
    /// </summary>
    public static void Update(FrameTimeSnapshotDto snapshot) => Store.Update(snapshot, snapshot.LastUpdate);

    /// <summary>
    /// Stores a snapshot from the producer, tolerating a null DTO (treated as
    /// the unavailable state). Keeps the null-tolerant entry point the poll
    /// loop and the tests rely on.
    /// </summary>
    public static void UpdateFromDto(FrameTimeSnapshotDto? dto)
        => Store.Update(dto ?? new FrameTimeSnapshotDto(), dto?.LastUpdate ?? default);

    /// <summary>
    /// Resets the cache to the unavailable state. Intended for test isolation.
    /// </summary>
    public static void Reset() => Store.Reset();
}
