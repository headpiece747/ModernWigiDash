using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// In-process cache of the latest frame-time snapshot polled from the
/// PresentMon Service (ADR-0003). The App's polling loop calls
/// <see cref="UpdateFromDto"/>; widgets read the cached snapshot on the render
/// thread. The store owns the staleness decision — consumers ask
/// <see cref="TryReadFresh"/> and cannot skip the check.
/// One instance of the shared <see cref="TelemetryStoreFacade{TRecord}"/>.
/// </summary>
public static class FrameTimeStore
{
    private static readonly TelemetryStoreFacade<FrameTimeSnapshotDto> Facade = new(
        new FrameTimeSnapshotDto(),
        defaultMaxAge: TimeSpan.FromSeconds(5),
        lastUpdateOf: dto => dto.LastUpdate);

    /// <summary>Default staleness window for frame-time data (~1s poll cadence).</summary>
    public static TimeSpan DefaultMaxAge => Facade.DefaultMaxAge;

    /// <summary>
    /// Internal test seam: builds a store bound to a fake clock (and optional
    /// max age) so the facade freshness tests can drive time. The production
    /// singleton binds <see cref="TimeProvider.System"/> at construction.
    /// </summary>
    internal static StaticTelemetryStore<FrameTimeSnapshotDto> CreateStoreForTest(TimeProvider timeProvider, TimeSpan? maxAge = null)
        => Facade.CreateStoreForTest(timeProvider, maxAge);

    /// <summary>Internal test seam: installs the store behind the static
    /// read/update surface (see <see cref="CreateStoreForTest"/>).</summary>
    internal static StaticTelemetryStore<FrameTimeSnapshotDto> StoreForTest
    {
        get => Facade.StoreForTest;
        set => Facade.StoreForTest = value;
    }

    /// <summary>
    /// Returns the cached snapshot when it is fresh enough, else null. The
    /// staleness window and the clock bind at construction.
    /// </summary>
    public static FrameTimeSnapshotDto? TryReadFresh() => Facade.TryReadFresh();

    /// <summary>
    /// Stores a snapshot from the producer, tolerating a null DTO (treated as
    /// the unavailable state). The single write entry point.
    /// </summary>
    public static void UpdateFromDto(FrameTimeSnapshotDto? dto) => Facade.UpdateFromDto(dto);

    /// <summary>
    /// Resets the cache to the unavailable state. Intended for test isolation.
    /// </summary>
    public static void Reset() => Facade.Reset();
}
