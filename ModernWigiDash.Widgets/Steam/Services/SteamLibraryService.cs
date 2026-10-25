using System.Text.Json;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;

namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The Steam library service: the 3-second refresh loop that loads the
/// manifests, folds each into its display state (via the process tracker),
/// applies the favorite set, and publishes an immutable
/// <see cref="SteamLibrarySnapshot"/> the widget reads on the render tick.
/// It also owns the posters (a local file on the renderer's request, a
/// bounded background CDN fetch on the local miss, at most
/// <see cref="MaxPosterFetchConcurrency"/> in flight), the app-id to
/// process-name map behind terminate, and the favorites JSON. The render
/// tick never touches disk or the network — it reads the published snapshot
/// and the poster cache only.
/// </summary>
internal sealed class SteamLibraryService : IDisposable
{
    /// <summary>The cached poster target width (the showcase focal size, the larger of the two layouts).</summary>
    public const int PosterTargetWidth = 180;

    /// <summary>The cached poster target height (the 2:3 aspect of the width).</summary>
    public const int PosterTargetHeight = 270;

    /// <summary>The maximum concurrent background poster fetches: a fast scroll
    /// can request many visible cells at once, and an unbounded burst would
    /// hammer the disk/CDN. The renderer redraws a placeholder until a slot
    /// frees.</summary>
    private const int MaxPosterFetchConcurrency = 6;

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
    private Dictionary<uint, string> _processNames = [];
    private string _favoritesJson = "[]";
    private volatile SteamLibrarySnapshot _current = SteamLibrarySnapshot.Empty;
    private volatile bool _disposed;

    private string _apiKey = "";
    private string _steamId64 = "";
    private string _avatarUrlForCurrent = "";
    private volatile SteamPersona? _persona;
    private volatile SKBitmap? _personaAvatar;
    private readonly RetiredBitmapSet _avatarRetire = new();

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
    /// Toggles one app's favorite state and returns the canonical favorites
    /// JSON (the sorted, distinct id array). The service owns the set: the
    /// widget persists the returned value through its one <c>SetProperty</c>
    /// path and never parses the JSON itself.
    /// </summary>
    public string ToggleFavorite(uint appId)
    {
        string json;
        lock (_gate)
        {
            if (!_favoriteApps.Remove(appId))
                _favoriteApps.Add(appId);

            _favoritesJson = SerializeFavoriteIds(_favoriteApps);
            json = _favoritesJson;
        }
        return json;
    }

    /// <summary>The canonical favorites JSON: a sorted, distinct uint array (one spelling the widget persists and the next reload parses).</summary>
    private static string SerializeFavoriteIds(HashSet<uint> ids)
        => JsonSerializer.Serialize(ids.OrderBy(id => id));

    /// <summary>
    /// The owner's avatar texture (the showcase banner), or null (the banner
    /// draws its placeholder disc). Read on the render thread.
    /// </summary>
    public SKBitmap? PersonaAvatar => _personaAvatar;

    /// <summary>
    /// Drains the retired avatar texture on the render thread (the UAF rule:
    /// a replaced avatar is freed here, before the canvas draws).
    /// </summary>
    public void DisposeRetiredAvatar() => _avatarRetire.DisposeRetired();

    /// <summary>
    /// Sets the optional community-API credentials (the widget's
    /// <c>SteamApiKey</c> + <c>SteamId64</c>). Both must be present for a
    /// persona fetch; a blank pair clears the persona. A change re-runs the
    /// persona leg immediately (the client's 5-minute cache bounds the HTTP).
    /// </summary>
    public void SetCredentials(string apiKey, string steamId64)
    {
        _apiKey = apiKey ?? "";
        _steamId64 = steamId64 ?? "";
        RefreshPersona();
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

    /// <summary>Test seam: publishes a persona (and optional avatar) directly, bypassing the HTTP fetch.</summary>
    internal void SetPersonaForTest(SteamPersona? persona, SKBitmap? avatar)
    {
        _persona = persona;
        if (avatar is not null)
        {
            SetAvatar(avatar);
            _avatarUrlForCurrent = "test";
        }
        else
        {
            RetireAvatar();
            _avatarUrlForCurrent = "";
        }

        PublishWithPersona();
    }

    /// <summary>
    /// Re-runs the persona leg: a blank credential pair clears the persona,
    /// else a fire-and-forget fetch (persona, then the avatar only when its
    /// URL changed) lands and publishes an updated snapshot. The client's
    /// 5-minute cache keeps the persona HTTP bounded.
    /// </summary>
    private void RefreshPersona()
    {
        if (_disposed)
            return;

        string apiKey = _apiKey;
        string steamId64 = _steamId64;
        if (string.IsNullOrWhiteSpace(apiKey) || !SteamWebApiClient.IsValidSteamId64(steamId64))
        {
            if (_persona is not null || _personaAvatar is not null)
            {
                _persona = null;
                RetireAvatar();
                _avatarUrlForCurrent = "";
                PublishWithPersona();
            }
            return;
        }

        _ = FetchPersonaAndAvatarAsync(apiKey, steamId64);
    }

    private async Task FetchPersonaAndAvatarAsync(string apiKey, string steamId64)
    {
        SteamPersona? persona = await _api.FetchPersonaAsync(apiKey, steamId64, CancellationToken.None).ConfigureAwait(false);
        if (_disposed)
            return;

        _persona = persona;

        string avatarUrl = persona?.AvatarUrl ?? "";
        if (avatarUrl.Length > 0 && !string.Equals(avatarUrl, _avatarUrlForCurrent, StringComparison.Ordinal))
        {
            byte[]? avatarBytes = await _api.FetchAvatarAsync(avatarUrl, CancellationToken.None).ConfigureAwait(false);
            if (_disposed)
                return;

            SKBitmap? decoded = avatarBytes is { Length: > 0 } ? SteamPosterCache.DecodeScaled(avatarBytes, 64, 64) : null;
            if (decoded is not null)
            {
                SetAvatar(decoded);
                _avatarUrlForCurrent = avatarUrl;
            }
        }
        else if (avatarUrl.Length == 0)
        {
            RetireAvatar();
            _avatarUrlForCurrent = "";
        }

        PublishWithPersona();
    }

    private void SetAvatar(SKBitmap bitmap)
    {
        SKBitmap? previous = _personaAvatar;
        _personaAvatar = bitmap;
        _avatarRetire.Retire(previous);
    }

    private void RetireAvatar()
    {
        SKBitmap? previous = _personaAvatar;
        _personaAvatar = null;
        _avatarRetire.Retire(previous);
    }

    private void PublishWithPersona()
    {
        if (_disposed)
            return;

        SteamLibrarySnapshot current = _current;
        var updated = current with { Persona = _persona };
        _current = updated;
        _publish(updated);
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
    /// Terminates a running game by app id: the process tree gets the polite
    /// close, the 3-second budget, then the whole-tree kill (the PRD's
    /// contract). The close blocks, so it runs on the thread pool, never on
    /// the render/touch thread; the returned task completes when the close
    /// has finished (callers may discard it). A blank process name (an app
    /// with no resolvable executable) is a completed no-op.
    /// </summary>
    public Task Terminate(uint appId)
    {
        string? processName;
        lock (_gate)
            _processNames.TryGetValue(appId, out processName);

        if (string.IsNullOrEmpty(processName) || _disposed)
            return Task.CompletedTask;

        return Task.Run(() => _tracker.TryClose(processName));
    }

    /// <summary>Stops the loop and disposes the poster cache.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _loop.Dispose();
        _cache.Dispose();
        SKBitmap? avatar = _personaAvatar;
        _personaAvatar = null;
        avatar?.Dispose();
        _avatarRetire.DisposeAll();
    }

    private void RefreshTick()
    {
        IReadOnlyList<SteamAppManifest> manifests = _source.LoadManifests();
        var games = new List<SteamGameItem>(manifests.Count);
        var processNames = new Dictionary<uint, string>(manifests.Count);
        foreach (SteamAppManifest manifest in manifests)
        {
            games.Add(ToGameItem(manifest));
            if (manifest.ProcessName.Length > 0)
                processNames[manifest.AppId] = manifest.ProcessName;
        }

        lock (_gate)
            _processNames = processNames;

        // One Steam-path resolution per tick (the registry ladder is cheap but
        // not free, and the path feeds both the snapshot and the found flag).
        string? steamPath = _source.SteamPath;
        var snapshot = new SteamLibrarySnapshot(games, steamPath, steamPath is not null, _persona);
        _current = snapshot;
        _publish(snapshot);
        RefreshPersona();
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
