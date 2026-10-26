using System.Text.Json;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Rendering;

namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The Steam library service: the 3-second refresh loop that loads the
/// manifests, folds each into its display state (via the process tracker),
/// applies the favorite set, and publishes an immutable
/// <see cref="SteamLibrarySnapshot"/> the widget reads on the render tick.
/// It also owns the app-id to process-name map behind terminate, the
/// favorites JSON, and the persona facade (credentials, avatar, retirement).
/// The poster policy (cache, in-flight set, fetch rule) is a module the
/// service composes and re-exposes as the render face: the widget's render
/// tick takes a <see cref="SteamRenderFrame"/> (the retired-texture drain,
/// the poster lookup seam, the avatar) and the poster requests through the
/// service's names, never through the policy handle. The render tick never
/// touches disk or the network — it reads the published snapshot and the
/// poster cache only.
/// </summary>
internal sealed class SteamLibraryService : IDisposable
{
    private readonly ISteamLibrarySource _source;
    private readonly SteamProcessTracker _tracker;
    private readonly SteamPosterPolicy _posterPolicy;
    private readonly PollLoop _loop;
    private readonly Action<SteamLibrarySnapshot> _publish;
    private readonly System.Threading.Lock _gate = new();
    private HashSet<uint> _favoriteApps = [];
    private Dictionary<uint, string> _processNames = [];
    private string _favoritesJson = "[]";
    private volatile SteamLibrarySnapshot _current = SteamLibrarySnapshot.Empty;
    private volatile bool _disposed;

    private readonly SteamPersonaProvider _personaProvider;

    /// <summary>
    /// The service over injected seams: the library source, the process
    /// tracker, the (dormant-unless-configured) API client, the snapshot
    /// publish sink, and the log sink. The poster policy is composed here
    /// (the service is the composition root: the widget never builds it).
    /// </summary>
    public SteamLibraryService(
        ISteamLibrarySource source,
        SteamProcessTracker tracker,
        SteamWebApiClient api,
        Action<SteamLibrarySnapshot> publish,
        Action<string> log)
    {
        _source = source;
        _tracker = tracker;
        _posterPolicy = new SteamPosterPolicy(source, new SteamPosterCache(), api, log);
        _publish = publish;
        _personaProvider = new SteamPersonaProvider(api, persona =>
        {
            if (_disposed)
                return;

            PublishPersona(persona);
        });
        _loop = new PollLoop("STEAM", TimeSpan.FromSeconds(3), () => true, RefreshTick, () => { }, log);
    }

    /// <summary>The most recently published snapshot (the render tick's read).</summary>
    public SteamLibrarySnapshot Current => _current;

    /// <summary>
    /// The tick's publish: folds the games facet onto the LATEST snapshot
    /// under the gate (the read, the write, and the wake-up are one critical
    /// section, the sink being a cheap thread-safe wake-up), preserving the
    /// persona facet — which <see cref="PublishPersona"/> alone owns — so a
    /// persona fold landing just before cannot be clobbered by a blind tick
    /// write. The tick and the persona fold are the two owners of
    /// <c>_current</c>, each a gated read-modify-write, so a lost update on
    /// either facet is unrepresentable.
    /// </summary>
    private void PublishTick(IReadOnlyList<SteamGameItem> games, string? steamPath, bool found)
    {
        SteamLibrarySnapshot updated;
        lock (_gate)
        {
            updated = _current with { Games = games, SteamPath = steamPath, IsSteamFound = found };
            _current = updated;
            _publish(updated);
        }
    }

    /// <summary>
    /// The persona fold: overlays the persona fact onto the LATEST snapshot
    /// under the gate (the read, the write, and the wake-up are one critical
    /// section, so a refresh tick landing between them cannot be clobbered),
    /// then publishes.
    /// </summary>
    private void PublishPersona(SteamPersona? persona)
    {
        SteamLibrarySnapshot updated;
        lock (_gate)
        {
            updated = _current with { Persona = persona };
            _current = updated;
            _publish(updated);
        }
    }

    /// <summary>
    /// The render tick's face: owns the retired-texture drain (the poster
    /// cache's and the avatar's — a texture a background tick retired is
    /// freed on the render thread, before the canvas draws) and returns the
    /// frame (the poster lookup seam + the live avatar). The render thread
    /// no longer spells the paired drain ritual.
    /// </summary>
    public SteamRenderFrame BeginRenderFrame()
    {
        _posterPolicy.Cache.DisposeRetired();
        _personaProvider.DisposeRetired();
        return new SteamRenderFrame(_posterPolicy.Cache, _personaProvider.Avatar);
    }

    /// <summary>Test seam: the concrete poster cache (the render face is the
    /// lookup seam; the tests drive the cache's internal retirement set).</summary>
    internal SteamPosterCache PosterCacheForTest => _posterPolicy.Cache;

    /// <summary>Requests the posters for each visible grid cell that is not yet cached (the policy's try-get-then-request rule, delegated).</summary>
    public void RequestVisibleGridPosters(IReadOnlyList<SteamGameItem> games, SteamGridGeometry geometry)
        => _posterPolicy.RequestVisibleGridPosters(games, geometry);

    /// <summary>Requests the posters for each visible showcase card when not yet cached (the policy's try-get-then-request rule, delegated).</summary>
    public void RequestShowcasePosters(SteamGameItem? focus, SteamGameItem? previous, SteamGameItem? next)
        => _posterPolicy.RequestShowcasePosters(focus, previous, next);

    /// <summary>Requests the posters for the active hero and visible shelf items in Hero Deck mode.</summary>
    public void RequestHeroPosters(SteamGameItem? hero, IReadOnlyList<SteamGameItem> shelfGames, SteamHeroGeometry geometry)
        => _posterPolicy.RequestHeroPosters(hero, shelfGames, geometry);

    /// <summary>Launches the game through the shell-exec sink (the steam://run URI).</summary>
    public void Launch(uint appId) => _tracker.Launch(appId);

    /// <summary>
    /// The tap action for one title: a running game is terminated (the close
    /// contract, run off the render thread), anything installed and idle
    /// launches through the shell-exec sink, and an unlaunchable game does
    /// nothing. The state→action rule lives here with its facts (the
    /// process-tracker fold, the off-render close contract), not in the widget.
    /// </summary>
    public void Activate(SteamGameItem game)
    {
        if (game.State == SteamGameState.Running)
            _ = Terminate(game.AppId);
        else if (game.IsLaunchable)
            Launch(game.AppId);
    }

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

            // A toggle is a user action, not a 3s tick: refresh the affected
            // game's star in the published snapshot (the read, the write, and
            // the wake-up are one critical section, so a landing tick cannot
            // clobber it).
            bool nowFavorite = _favoriteApps.Contains(appId);
            SteamLibrarySnapshot updated = _current with { Games = FlipFavorite(_current.Games, appId, nowFavorite) };
            _current = updated;
            _publish(updated);
        }
        return json;
    }

    /// <summary>The snapshot's games list with one app's favorite flag set (the toggle's immediate star refresh, no 3s tick).</summary>
    private static IReadOnlyList<SteamGameItem> FlipFavorite(IReadOnlyList<SteamGameItem> games, uint appId, bool nowFavorite)
    {
        var updated = new List<SteamGameItem>(games.Count);
        foreach (SteamGameItem game in games)
            updated.Add(game.AppId == appId ? game with { IsFavorite = nowFavorite } : game);
        return updated;
    }

    /// <summary>The canonical favorites JSON: a sorted, distinct uint array (one spelling the widget persists and the next reload parses).</summary>
    private static string SerializeFavoriteIds(HashSet<uint> ids)
        => JsonSerializer.Serialize(ids.OrderBy(id => id));

    /// <summary>
    /// Sets the optional community-API credentials (the widget's
    /// <c>SteamApiKey</c> + <c>SteamId64</c>). Both must be present for a
    /// persona fetch; a blank pair clears the persona. A change re-runs the
    /// persona leg immediately (the client's 5-minute cache bounds the HTTP).
    /// </summary>
    public void SetCredentials(string apiKey, string steamId64)
        => _personaProvider.SetCredentials(apiKey, steamId64);

    /// <summary>Test seam: publishes a persona (and optional avatar) directly, bypassing the HTTP fetch.</summary>
    internal void SetPersonaForTest(SteamPersona? persona, SKBitmap? avatar)
        => _personaProvider.SetPersonaForTest(persona, avatar);

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

    /// <summary>Stops the loop and disposes the poster policy and the persona provider.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _loop.Dispose();
        _posterPolicy.Dispose();
        _personaProvider.Dispose();
    }

    private void RefreshTick()
    {
        // One Steam-path resolution per tick (the registry ladder is cheap
        // but not free): the path feeds the snapshot, the found flag, and the
        // manifest enumeration (the source no longer re-resolves it inside).
        string? steamPath = _source.SteamPath;
        IReadOnlyList<SteamAppManifest> manifests = _source.LoadManifests(steamPath);
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

        PublishTick(games, steamPath, steamPath is not null);
        _personaProvider.Refresh();
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
