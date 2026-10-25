using System.Text.Json;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// A Steam community persona (the optional Web API surface): the avatar
/// URL, the persona name, and the online state. Fetched only when BOTH the
/// widget's SteamApiKey and SteamId64 properties are set; with either empty
/// the client is dormant (zero HTTP).
/// </summary>
internal sealed record SteamPersona(string AvatarUrl, string PersonaName, int PersonState)
{
    /// <summary>The Steam persona-state vocabulary (the community API's
    /// personastate ordinals).</summary>
    public static readonly string[] StateNames = ["Offline", "Online", "InGame", "Busy", "Away", "Snoozing"];

    /// <summary>The state's display label (unknown ordinals read as the
    /// ordinal itself, so a new Steam state is never a silent blank).</summary>
    public string StateLabel
        => PersonState >= 0 && PersonState < StateNames.Length ? StateNames[PersonState] : PersonState.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The Steam cluster's one HTTP owner: the optional community-API persona
/// fetch (5-minute cache, keyed by the api-key + steam-id pair) and the CDN
/// poster fallback GET. The shared long-lived client budgets from
/// <see cref="NetworkBudget.Wait"/> (the one-wait shape: the client's
/// Timeout covers the response). Every method degrades to null — a failed
/// fetch is a missing fact, never a throw.
/// </summary>
internal sealed class SteamWebApiClient
{
    private static readonly HttpClient Shared = new() { Timeout = NetworkBudget.Wait };

    private readonly TimeProvider _clock;
    private readonly HttpClient _client;
    private readonly System.Threading.Lock _gate = new();
    private (string ApiKey, string SteamId, DateTimeOffset UtcNow, SteamPersona Persona)? _personaCache;

    /// <summary>The production client over the shared HTTP owner.</summary>
    public SteamWebApiClient() : this(TimeProvider.System, null)
    {
    }

    /// <summary>Test seam: an injectable clock and HTTP client (the persona
    /// cache TTL is clock-relative, and the fetch runs against a stub
    /// handler instead of the network).</summary>
    internal SteamWebApiClient(TimeProvider clock, HttpClient? client)
    {
        _clock = clock;
        _client = client ?? Shared;
    }

    /// <summary>
    /// The persona for the api-key + steam-id pair, served from the
    /// 5-minute cache when fresh. Null when either property is empty (the
    /// dormant gate), when the fetch fails, or when the response carries no
    /// player row — the renderer falls back to the local facts (ACF playtime
    /// and last-played) in every one of those cases.
    /// </summary>
    public async Task<SteamPersona?> FetchPersonaAsync(string apiKey, string steamId64, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || !IsValidSteamId64(steamId64))
            return null;

        (string apiKey, string steamId, DateTimeOffset utcNow, SteamPersona persona)? cached;
        lock (_gate)
        {
            cached = _personaCache;
        }

        if (cached is { } fresh
            && string.Equals(fresh.apiKey, apiKey, StringComparison.Ordinal)
            && string.Equals(fresh.steamId, steamId64, StringComparison.Ordinal)
            && _clock.GetUtcNow() - fresh.utcNow < TimeSpan.FromMinutes(5))
        {
            return fresh.persona;
        }

        SteamPersona? fetched = await FetchPersonaCoreAsync(apiKey, steamId64, cancellationToken).ConfigureAwait(false);
        if (fetched is null)
            return null;

        lock (_gate)
        {
            _personaCache = (apiKey, steamId64, _clock.GetUtcNow(), fetched);
        }

        return fetched;
    }

    /// <summary>
    /// The CDN poster bytes for an app id (the local-file-miss fallback leg),
    /// bounded to a 4 MB read (a library_600x900 poster is tens of KB; the
    /// cap refuses a hostile server's multi-megabyte answer). Null on any
    /// failure.
    /// </summary>
    public async Task<byte[]?> FetchPosterAsync(uint appId, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(SteamPosterSource.CdnUrl(appId), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaxPosterBytes)
                return null;

            return bytes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the value is a 17-digit SteamID64 (the community API's id
    /// shape; a hand-typed id with letters or the wrong length is refused
    /// before any HTTP).
    /// </summary>
    internal static bool IsValidSteamId64(string steamId64)
        => steamId64.Length == 17 && steamId64.All(char.IsAsciiDigit);

    /// <summary>The one-wait budget's owner (test seam: the Timeout is read
    /// from the shared client, not re-spelled).</summary>
    internal static HttpClient SharedClient => Shared;

    private const int MaxPosterBytes = 4 * 1024 * 1024;

    private async Task<SteamPersona?> FetchPersonaCoreAsync(string apiKey, string steamId64, CancellationToken cancellationToken)
    {
        try
        {
            string url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(apiKey)}&steamids={steamId64}";
            using HttpResponseMessage response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("response", out JsonElement responseElement)
                || !responseElement.TryGetProperty("players", out JsonElement players)
                || players.ValueKind != JsonValueKind.Array
                || players.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement player = players[0];
            string avatar = player.TryGetProperty("avatar_full", out JsonElement avatarElement) ? avatarElement.GetString() ?? "" : "";
            string name = player.TryGetProperty("personaname", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";
            int state = player.TryGetProperty("personastate", out JsonElement stateElement) && stateElement.ValueKind == JsonValueKind.Number ? stateElement.GetInt32() : -1;

            return new SteamPersona(avatar, name, state);
        }
        catch (Exception)
        {
            // A failed community-API fetch is a missing persona: the renderer
            // falls back to the local ACF facts.
            return null;
        }
    }
}
