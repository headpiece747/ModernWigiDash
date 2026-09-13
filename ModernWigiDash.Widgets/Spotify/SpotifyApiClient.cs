using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ModernWigiDash.Widgets.Spotify;

/// <summary>
/// The Spotify Web API + OAuth device-flow client. Mirrors the Twitch API
/// client: one shared process-wide <see cref="HttpClient"/> (the house rule —
/// no per-call clients), an injectable transport for tests, and a typed
/// <see cref="SpotifyApiException"/> carrying the HTTP status. The device flow
/// polls at the server-returned interval (with the slow_down growth); the Web
/// API reads route through the user's access token with automatic refresh on 401.
/// </summary>
internal class SpotifyApiClient(string clientId, HttpClient? httpClient = null)
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Uri DeviceStartEndpoint = new("https://accounts.spotify.com/api/oauth/device");
    private static readonly Uri TokenEndpoint = new("https://accounts.spotify.com/api/token");
    private static readonly Uri MePlayerEndpoint = new("https://api.spotify.com/v1/me/player");
    private static readonly Uri MePlaylistsEndpoint = new("https://api.spotify.com/v1/me/playlists");

    /// <summary>The scopes requested in the device flow: playback state read + playlist read.</summary>
    public const string Scopes = "user-read-playback-state user-read-currently-playing playlist-read-private playlist-read-collaborative";

    private readonly string _clientId = clientId.Trim();
    private readonly HttpClient _httpClient = httpClient ?? SharedHttpClient;

    /// <summary>Test seam: injectable clock for token expiry timestamps.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    public virtual async Task<SpotifyDeviceAuthorization> StartDeviceAuthorizationAsync(CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("scope", Scopes)
        ]);

        using var response = await _httpClient.PostAsync(DeviceStartEndpoint, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var payload = await response.Content.ReadFromJsonAsync<SpotifyDeviceStartResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new SpotifyApiException(500, "Spotify returned an empty device authorization response.");

        if (!Uri.TryCreate(payload.VerificationUri, UriKind.Absolute, out var parsedUri))
            throw new SpotifyApiException(500, "Spotify returned an invalid device verification URL.");

        return new SpotifyDeviceAuthorization(
            payload.DeviceCode,
            payload.UserCode,
            parsedUri,
            Clock.GetUtcNow().AddSeconds(Math.Max(1, payload.ExpiresIn)),
            Math.Max(1, payload.Interval));
    }

    public virtual async Task<SpotifyTokenSet> PollDeviceTokenAsync(
        SpotifyDeviceAuthorization deviceAuthorization,
        CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(deviceAuthorization.PollIntervalSeconds);

        while (Clock.GetUtcNow() < deviceAuthorization.ExpiresAt)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("device_code", deviceAuthorization.DeviceCode),
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
            ]);

            using var response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<SpotifyTokenResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new SpotifyApiException(500, "Spotify returned an empty access token response.");
                return CreateTokenSet(payload, deviceAuthorization.ExpiresAt);
            }

            string error = await ReadOAuthErrorAsync(response, cancellationToken).ConfigureAwait(false);
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "access_denied":
                    throw new SpotifyApiException((int)response.StatusCode, "Spotify authorization was denied.");
                case "expired_token":
                    throw new SpotifyApiException((int)response.StatusCode, "The Spotify device code expired.");
                default:
                    throw new SpotifyApiException((int)response.StatusCode, string.IsNullOrWhiteSpace(error) ? "Spotify authorization failed." : error);
            }
        }

        throw new SpotifyApiException(408, "The Spotify device authorization expired before it was completed.");
    }

    public virtual async Task<SpotifyTokenSet> RefreshAsync(SpotifyTokenSet current, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("refresh_token", current.RefreshToken),
            new KeyValuePair<string, string>("grant_type", "refresh_token")
        ]);

        using var response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var payload = await response.Content.ReadFromJsonAsync<SpotifyTokenResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new SpotifyApiException(500, "Spotify returned an empty refresh response.");

        // Spotify may omit the refresh token on refresh (it stays valid); keep the prior one.
        if (string.IsNullOrWhiteSpace(payload.RefreshToken))
            payload.RefreshToken = current.RefreshToken;

        return CreateTokenSet(payload, Clock.GetUtcNow().AddSeconds(Math.Max(1, payload.ExpiresIn)));
    }

    public virtual async Task<SpotifyNowPlaying?> GetNowPlayingAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MePlayerEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null; // No active playback session.
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var payload = await response.Content.ReadFromJsonAsync<SpotifyPlayerResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (payload?.Item is not { } item || string.IsNullOrWhiteSpace(item.Name))
            return null;

        string artist = item.Artists is { Count: > 0 } artists
            ? string.Join(", ", artists.Select(a => a.Name))
            : "";
        string? image = item.Album?.Images is { Count: > 0 } images ? images[^1].Url : null;

        return new SpotifyNowPlaying(
            item.Name,
            artist,
            item.Album?.Name,
            image,
            payload.IsPlaying,
            payload.ProgressMs,
            item.DurationMs);
    }

    public virtual async Task<IReadOnlyList<SpotifyPlaylist>> GetPlaylistsAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{MePlaylistsEndpoint}?limit=20");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var payload = await response.Content.ReadFromJsonAsync<SpotifyPlaylistsResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (payload?.Items is not { } items)
            return [];

        return items
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .Select(p => new SpotifyPlaylist(
                p.Id,
                p.Name,
                p.Images is { Count: > 0 } images ? images[0].Url : null))
            .ToArray();
    }

    private SpotifyTokenSet CreateTokenSet(SpotifyTokenResponse payload, DateTimeOffset fallbackExpiry)
        => new(
            payload.AccessToken,
            payload.RefreshToken,
            payload.ExpiresIn > 0 ? Clock.GetUtcNow().AddSeconds(payload.ExpiresIn) : fallbackExpiry,
            SplitScopes(payload.Scope));

    private static string[] SplitScopes(string scope)
        => string.IsNullOrWhiteSpace(scope) ? [] : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        throw await CreateApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SpotifyApiException> CreateApiExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string message = $"Spotify request failed with HTTP {(int)response.StatusCode}.";
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var error = JsonSerializer.Deserialize<SpotifyErrorResponse>(body, JsonOptions);
                if (!string.IsNullOrWhiteSpace(error?.ErrorDescription)) message = error.ErrorDescription;
                else if (!string.IsNullOrWhiteSpace(error?.Error)) message = error.Error;
            }
            catch (JsonException)
            {
                // Keep the generic status message when Spotify returns non-JSON content.
                System.Diagnostics.Debug.WriteLine("Spotify API error body is not valid JSON; keeping the generic status message.");
            }
        }

        return new SpotifyApiException((int)response.StatusCode, message);
    }

    private static async Task<string> ReadOAuthErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return "";

        try
        {
            var error = JsonSerializer.Deserialize<SpotifyErrorResponse>(body, JsonOptions);
            return error?.Error ?? error?.ErrorDescription ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}

/// <summary>
/// A failed Spotify API call: carries the HTTP status code the failure came
/// back with (401 is the session's re-login signal).
/// </summary>
public sealed class SpotifyApiException(int statusCode, string message) : Exception(message)
{
    /// <summary>The HTTP status code the failure came back with.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>Whether the failure was an unauthorized response (401, the session's re-login signal).</summary>
    public bool IsUnauthorized => StatusCode == 401;
}
