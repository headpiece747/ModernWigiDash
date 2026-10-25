using System.Text.Json;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;

namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The Steam library service: the 3-second refresh loop that loads the
/// manifests, folds each into its display state (via the process tracker),
/// applies the favorite set, and publishes an immutable
/// <see cref="SteamLibrarySnapshot"/> the widget reads on the render tick.
/// It also owns the poster warmup (local files on the tick, a bounded
/// background CDN fetch on a renderer-requested cache miss) and the
/// favorites JSON. The render tick never touches disk or the network — it
/// reads the published snapshot and the poster cache only.
/// </summary>
internal sealed class SteamLibraryService : IDisposable
{
    /// <summary>The cached poster target width (the showcase focal size, the larger of the two layouts).</summary>
    public const int PosterTargetWidth = 180;

    /// <summary>The cached poster target height (the 2:3 aspect of the width).</summary>
    public const int PosterTargetHeight = 270;

    private readonly ISteamLibrarySource _source;
    private readonly SteamProcessTracker _tracker;
    private readonly SteamPosterCache _cache;
    private readonly SteamWebApiClient _api;
    private readonly PollLoop _loop;
    private readonly Action<SteamLibrarySnapshot> _publish;
    private readonly Action<string> _log;
    private readonly System.Threading.Lock _gate = new();
    private HashSet<uint> _favoriteApps = [];
    private readonly HashSet<uint> _posterInFlight = [];
    private string _favoritesJson = "[]";
    private volatile SteamLibrarySnapshot _current = SteamLibrarySnapshot.Empty;

    /// <summary>
    /// The service over injected seams: the library source, the process
    /// tracker, the poster cache, the (dormant-unless-configured) API client,
    /// the snapshot publish sink, and the log sink.
    /// </summary>
    public SteamLibraryService(
        ISteamLibrarySource source,
        SteamProcessTracker tracker,
        SteamPosterCache cache,
        SteamWebApiClient api,
        Action<SteamLibrarySnapshot> publish,
        Action<string> log)
    {
        _source = source;
        _tracker = tracker;
        _cache = cache;
        _api = api;
        _publish = publish;
        _log = log;
        _loop = new PollLoop("STEAM", TimeSpan.FromSeconds(3), () => true, RefreshTick, () => { }, log);
    }

    /// <summary>The most recently published snapshot (the render tick's read).</summary>
    public SteamLibrarySnapshot Current => _current;

    /// <summary>The poster cache (the renderer's lookup owner).</summary>
    public SteamPosterCache PosterCache => _cache;

    /// <summary>The process tracker (the widget's launch/terminate owner).</summary>
    public SteamProcessTracker Tracker => _tracker;

    /// <summary>The current favorites JSON (a uint app-id array).</summary>
    public string FavoritesJson
    {
        get
        {
            lock (_gate)
                return _favoritesJson;
        }
    }

    /// <summary>Starts the 3-second refresh loop (idempotent).</summary>
    public void Start() => _loop.Start();

    /// <summary>
    /// Runs one refresh tick synchronously (the test seam and the force-refresh
    /// gesture): loads the manifests, folds the states, publishes the
    /// snapshot, and warms the local posters. Safe from any thread.
    /// </summary>
    public void RefreshNow() => RefreshTick();

    /// <summary>
    /// Replaces the favorites JSON (a uint app-id array) and re-derives the
    /// favorite set. A blank or malformed JSON reads as an empty set — a
    /// hostile or hand-edited value is observable, never a throw.
    /// </summary>
    public void SetFavoritesJson(string json)
    {
        lock (_gate)
        {
            string normalized = string.IsNullOrWhiteSpace(json) ? "[]" : json;
            if (string.Equals(_favoritesJson, normalized, StringComparison.Ordinal))
                return;

            _favoritesJson = normalized;
            _favoriteApps = [.. ParseFavoriteIds(normalized)];
        }
    }

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

    /// <summary>Stops the loop and disposes the poster cache.</summary>
    public void Dispose()
    {
        _loop.Dispose();
        _cache.Dispose();
    }

    private void RefreshTick()
    {
        IReadOnlyList<SteamAppManifest> manifests = _source.LoadManifests();
        List<SteamGameItem> games = manifests.Select(ToGameItem).ToList();

        var snapshot = new SteamLibrarySnapshot(games, _source.SteamPath, _source.SteamPath is not null);
        _current = snapshot;
        _publish(snapshot);
        WarmupLocalPosters(snapshot);
    }

    private SteamGameItem ToGameItem(SteamAppManifest manifest)
    {
        SteamGameState state = _tracker.Resolve(manifest);
        bool favorite;
        lock (_gate)
        {
            favorite = _favoriteApps.Contains(manifest.AppId);
        }

        return new SteamGameItem(
            manifest.AppId,
            manifest.Name,
            manifest.InstallDir,
            state,
            manifest.DownloadProgress,
            manifest.HoursOnRecord,
            manifest.LastPlayedUtc,
            favorite);
    }

    private void WarmupLocalPosters(SteamLibrarySnapshot snapshot)
    {
        string? steamPath = snapshot.SteamPath;
        if (steamPath is null)
            return;

        foreach (SteamGameItem game in snapshot.Games)
            WarmupOnePoster(game, steamPath);
    }

    private void WarmupOnePoster(SteamGameItem game, string steamPath)
    {
        if (_cache.TryGet(game.AppId, out _))
            return; // already cached.

        byte[]? local = SteamPosterSource.TryReadLocalPoster(steamPath, game.AppId);
        if (local is { Length: > 0 })
            _cache.Store(game.AppId, local, PosterTargetWidth, PosterTargetHeight);
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

    private static List<uint> ParseFavoriteIds(string json)
    {
        var ids = new List<uint>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return ids;

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.TryGetUInt32(out uint id))
                    ids.Add(id);
            }
        }
        catch (Exception)
        {
            // A malformed favorites value reads as an empty set.
            return ids;
        }

        return ids;
    }
}
