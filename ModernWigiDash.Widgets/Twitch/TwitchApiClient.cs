using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ModernWigiDash.Widgets.Twitch;

internal class TwitchApiClient(string clientId, HttpClient? httpClient = null)
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Uri DeviceAuthorizationEndpoint = new("https://id.twitch.tv/oauth2/device");
    private static readonly Uri TokenEndpoint = new("https://id.twitch.tv/oauth2/token");
    private static readonly Uri ValidationEndpoint = new("https://id.twitch.tv/oauth2/validate");
    private static readonly Uri RevokeEndpoint = new("https://id.twitch.tv/oauth2/revoke");
    private static readonly Uri FollowedStreamsEndpoint = new("https://api.twitch.tv/helix/streams/followed");

    private readonly string _clientId = clientId.Trim();
    private readonly HttpClient _httpClient = httpClient ?? SharedHttpClient;

    /// <summary>Test seam: injectable clock for token expiry timestamps.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    public virtual async Task<TwitchDeviceAuthorization> StartDeviceAuthorizationAsync(CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("scopes", "user:read:follows")
        ]);

        using var response = await _httpClient.PostAsync(DeviceAuthorizationEndpoint, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var payload = await response.Content.ReadFromJsonAsync<TwitchDeviceAuthorizationResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new TwitchApiException(500, "Twitch returned an empty device authorization response.");

        string verificationUri = payload.VerificationUriComplete ?? payload.VerificationUri;
        if (!Uri.TryCreate(verificationUri, UriKind.Absolute, out var parsedUri))
            throw new TwitchApiException(500, "Twitch returned an invalid device verification URL.");

        return new TwitchDeviceAuthorization(
            payload.DeviceCode,
            payload.UserCode,
            parsedUri,
            Clock.GetUtcNow().AddSeconds(Math.Max(1, payload.ExpiresIn)),
            Math.Max(1, payload.Interval));
    }

    public virtual async Task<TwitchTokenSet> PollDeviceTokenAsync(
        TwitchDeviceAuthorization deviceAuthorization,
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
                new KeyValuePair<string, string>("scopes", "user:read:follows"),
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
            ]);

            using var response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<TwitchTokenResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new TwitchApiException(500, "Twitch returned an empty access token response.");

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
                    throw new TwitchApiException((int)response.StatusCode, "Twitch authorization was denied.");
                case "expired_token":
                    throw new TwitchApiException((int)response.StatusCode, "The Twitch device code expired.");
                default:
                    throw new TwitchApiException((int)response.StatusCode, string.IsNullOrWhiteSpace(error) ? "Twitch authorization failed." : error);
            }
        }

        throw new TwitchApiException(408, "The Twitch device authorization expired before it was completed.");
    }

    public virtual async Task<TwitchTokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
            new KeyValuePair<string, string>("grant_type", "refresh_token")
        ]);

        using var response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var payload = await response.Content.ReadFromJsonAsync<TwitchTokenResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new TwitchApiException(500, "Twitch returned an empty refresh response.");

        if (string.IsNullOrWhiteSpace(payload.RefreshToken))
            payload.RefreshToken = refreshToken;

        return CreateTokenSet(payload, Clock.GetUtcNow().AddSeconds(Math.Max(1, payload.ExpiresIn)));
    }

    public virtual async Task<TwitchTokenValidation> ValidateAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ValidationEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var payload = await response.Content.ReadFromJsonAsync<TwitchTokenValidationResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new TwitchApiException(500, "Twitch returned an empty token validation response.");

        if (string.IsNullOrWhiteSpace(payload.UserId) || string.IsNullOrWhiteSpace(payload.Login))
            throw new TwitchApiException(401, "The Twitch token is not associated with a user.");

        return new TwitchTokenValidation(
            payload.ClientId,
            payload.UserId,
            payload.Login,
            payload.ExpiresIn,
            payload.Scopes ?? []);
    }

    public virtual async Task<IReadOnlyList<TwitchFollowedChannel>> GetFollowedLiveChannelsAsync(
        string accessToken,
        string userId,
        CancellationToken cancellationToken)
    {
        List<TwitchFollowedChannel> channels = [];
        string? cursor = null;

        do
        {
            string url = $"{FollowedStreamsEndpoint}?user_id={Uri.EscapeDataString(userId)}&first=100";
            if (!string.IsNullOrWhiteSpace(cursor))
                url += $"&after={Uri.EscapeDataString(cursor)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Client-Id", _clientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            var payload = await response.Content.ReadFromJsonAsync<TwitchFollowedStreamsResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new TwitchApiException(500, "Twitch returned an empty followed-channel response.");

            channels.AddRange(payload.Data
                .Where(channel => !string.IsNullOrWhiteSpace(channel.UserLogin))
                .Select(channel => new TwitchFollowedChannel(
                    channel.UserLogin,
                    channel.UserName)));

            cursor = payload.Pagination?.Cursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return channels
            .OrderBy(channel => channel.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(channel => channel.Login, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public virtual async Task RevokeAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("token", accessToken)
        ]);
        using var response = await _httpClient.PostAsync(RevokeEndpoint, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private TwitchTokenSet CreateTokenSet(TwitchTokenResponse payload, DateTimeOffset fallbackExpiry)
        => new(
            _clientId,
            payload.AccessToken,
            payload.RefreshToken,
            payload.ExpiresIn > 0 ? Clock.GetUtcNow().AddSeconds(payload.ExpiresIn) : fallbackExpiry,
            payload.Scope ?? []);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        throw await CreateApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TwitchApiException> CreateApiExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string message = $"Twitch request failed with HTTP {(int)response.StatusCode}.";
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var error = JsonSerializer.Deserialize<TwitchOAuthErrorResponse>(body, JsonOptions);
                if (!string.IsNullOrWhiteSpace(error?.Message)) message = error.Message;
                else if (!string.IsNullOrWhiteSpace(error?.Error)) message = error.Error;
            }
            catch (JsonException)
            {
                // Keep the generic status message when Twitch returns non-JSON content.
                System.Diagnostics.Debug.WriteLine("Twitch API error body is not valid JSON; keeping the generic status message.");
            }
        }

        return new TwitchApiException((int)response.StatusCode, message);
    }

    private static async Task<string> ReadOAuthErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return "";

        try
        {
            var error = JsonSerializer.Deserialize<TwitchOAuthErrorResponse>(body, JsonOptions);
            return error?.Error ?? error?.Message ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}

public sealed class TwitchApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public bool IsUnauthorized => StatusCode == 401;
}
