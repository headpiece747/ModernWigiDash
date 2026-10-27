using ModernWigiDash.Widgets.Steam.Models;

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
    private object? _lastToken;
    private uint[] _lastRequestedAppIds = [];
    private volatile bool _disposed;

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
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Requests posters for each visible AppId that is not yet cached.
    /// The request is gated on the token (reference equality on the games list or context)
    /// and the sequence of visible AppIds, so steady-state frames re-request nothing and allocate nothing.
    /// </summary>
    public void RequestPosters(object? contextToken, ReadOnlySpan<uint> visibleAppIds)
    {
        if (ReferenceEquals(_lastToken, contextToken)
            && MemoryExtensions.SequenceEqual(_lastRequestedAppIds.AsSpan(), visibleAppIds))
        {
            return;
        }

        _lastToken = contextToken;
        if (_lastRequestedAppIds.Length != visibleAppIds.Length)
            _lastRequestedAppIds = new uint[visibleAppIds.Length];

        visibleAppIds.CopyTo(_lastRequestedAppIds);

        for (int i = 0; i < visibleAppIds.Length; i++)
        {
            uint appId = visibleAppIds[i];
            if (appId != 0 && !_cache.TryGet(appId, out _))
                RequestPoster(appId);
        }
    }

    /// <summary>Requests the poster for each visible showcase card (the focal card and its flanks) when not yet cached.</summary>
    public void RequestShowcasePosters(SteamGameItem? focus, SteamGameItem? previous, SteamGameItem? next)
    {
        Span<uint> ids = stackalloc uint[3];
        int count = 0;
        if (focus is not null) ids[count++] = focus.AppId;
        if (previous is not null) ids[count++] = previous.AppId;
        if (next is not null) ids[count++] = next.AppId;
        RequestPosters(null, ids[..count]);
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
