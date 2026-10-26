using System.Runtime.InteropServices;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Rendering;

namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The poster policy: the lookup cache, the bounded in-flight fetch set, and
/// the local-file-then-CDN fetch rule behind every poster request. The render
/// tick never touches disk or the network: it reads the cache (and drains the
/// retired textures), and a cache miss starts at most
/// <see cref="MaxPosterFetchConcurrency"/> concurrent background fetches,
/// one per app at a time. A fast scroll draws placeholders until a slot
/// frees.
/// </summary>
internal sealed class SteamPosterPolicy : IDisposable
{
    /// <summary>The cached poster target width (the showcase focal size, the larger of the two layouts).</summary>
    public const int PosterTargetWidth = 180;

    /// <summary>The cached poster target height (the 2:3 aspect of the width).</summary>
    public const int PosterTargetHeight = 270;

    /// <summary>The maximum concurrent background poster fetches.</summary>
    private const int MaxPosterFetchConcurrency = 6;

    private readonly ISteamLibrarySource _source;
    private readonly SteamPosterCache _cache;
    private readonly SteamWebApiClient _api;
    private readonly Action<string> _log;
    private readonly System.Threading.Lock _gate = new();
    private readonly HashSet<uint> _posterInFlight = [];
    private readonly MemoSlot<GridRequestKey, bool> _gridRequestMemo = new(GridRequestKeyComparer.Instance);
    private volatile bool _disposed;

    /// <summary>The grid-request memo key: the games-list reference + the geometry value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GridRequestKey(IReadOnlyList<SteamGameItem> Games, SteamGridGeometry Geometry);

    /// <summary>
    /// The grid-request key's equality: the games list compares by REFERENCE
    /// (a fresh list each tick is a content change), the geometry by value (a
    /// scrolled layout is a new visible set).
    /// </summary>
    private sealed class GridRequestKeyComparer : IEqualityComparer<GridRequestKey>
    {
        public static readonly GridRequestKeyComparer Instance = new();

        public bool Equals(GridRequestKey x, GridRequestKey y)
            => ReferenceEquals(x.Games, y.Games) && x.Geometry.Equals(y.Geometry);

        public int GetHashCode(GridRequestKey key)
            => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Games), key.Geometry);
    }

    /// <summary>
    /// The policy over injected seams: the library source (the local poster
    /// file path), the lookup cache, the (dormant-unless-configured) CDN
    /// client, and the log sink.
    /// </summary>
    public SteamPosterPolicy(
        ISteamLibrarySource source,
        SteamPosterCache cache,
        SteamWebApiClient api,
        Action<string> log)
    {
        _source = source;
        _cache = cache;
        _api = api;
        _log = log;
    }

    /// <summary>The poster cache (the renderer's lookup owner).</summary>
    public SteamPosterCache Cache => _cache;

    /// <summary>
    /// Requests a background poster fetch (local file, then the CDN) for a
    /// cache miss. Fire-and-forget: the renderer draws a placeholder in the
    /// meantime and the fetched texture lands in the cache for the next tick.
    /// One fetch per app at a time (a second request while one is in flight
    /// is dropped).
    /// </summary>
    public void RequestPoster(uint appId)
    {
        bool start;
        lock (_gate)
        {
            if (_posterInFlight.Count >= MaxPosterFetchConcurrency)
                return;

            start = _posterInFlight.Add(appId);
        }

        if (!start)
            return;

        _ = FetchPosterAsync(appId).ContinueWith(_ =>
        {
            lock (_gate)
                _posterInFlight.Remove(appId);
        });
    }

    /// <summary>
    /// Requests the poster for each visible grid cell that is not yet cached
    /// (local file first, then the CDN; one in-flight per app). The 24-slot
    /// LRU then tracks what is on screen, so a scrolled-into-view cell loads
    /// on demand instead of an all-games sweep evicting the visible ones. The
    /// try-get-then-request policy lives here, not in the widget. The request
    /// is memo-gated on the visible set (the games-list reference + the
    /// geometry): a steady tick is a no-op, and a fetch is re-requested only
    /// when the visible set changes (a scroll, a new tick's list).
    /// </summary>
    public void RequestVisibleGridPosters(IReadOnlyList<SteamGameItem> games, SteamGridGeometry geometry)
    {
        var key = new GridRequestKey(games, geometry);
        if (_gridRequestMemo.TryGet(key, out _))
            return;

        for (int i = 0; i < geometry.CellCount; i++)
        {
            int index = geometry.Indexes[i];
            if (index >= 0 && index < games.Count)
                RequestPosterIfMissing(games[index]);
        }

        _gridRequestMemo.Set(key, true);
    }

    /// <summary>Requests the poster for each visible showcase card (the focal card and its flanks) when not yet cached.</summary>
    public void RequestShowcasePosters(SteamGameItem? focus, SteamGameItem? previous, SteamGameItem? next)
    {
        RequestPosterIfMissing(focus);
        RequestPosterIfMissing(previous);
        RequestPosterIfMissing(next);
    }

    /// <summary>Requests one card's poster when it is not yet cached.</summary>
    private void RequestPosterIfMissing(SteamGameItem? card)
    {
        if (card is not null && !_cache.TryGet(card.AppId, out _))
            RequestPoster(card.AppId);
    }

    /// <summary>
    /// Test seam: loads a poster synchronously from the local file (no CDN,
    /// no fire-and-forget), so a test observes the cache population
    /// deterministically.
    /// </summary>
    internal void LoadLocalPosterForTest(uint appId)
    {
        string? steamPath = _source.SteamPath;
        byte[]? bytes = steamPath is null ? null : SteamPosterSource.TryReadLocalPoster(steamPath, appId);
        if (bytes is { Length: > 0 })
            _cache.Store(appId, bytes, PosterTargetWidth, PosterTargetHeight);
    }

    /// <summary>Stops serving fetches and disposes the poster cache.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cache.Dispose();
    }

    private async Task FetchPosterAsync(uint appId)
    {
        try
        {
            string? steamPath = _source.SteamPath;
            byte[]? bytes = steamPath is null ? null : SteamPosterSource.TryReadLocalPoster(steamPath, appId);
            if (bytes is null)
                bytes = await _api.FetchPosterAsync(appId, CancellationToken.None).ConfigureAwait(false);

            if (bytes is { Length: > 0 })
                _cache.Store(appId, bytes, PosterTargetWidth, PosterTargetHeight);
        }
        catch (Exception ex)
        {
            _log($"Steam: poster fetch failed ({appId}): {ex.Message}");
        }
    }
}
