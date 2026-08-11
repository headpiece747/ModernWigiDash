using System.Text.Json.Serialization;

namespace ModernWigiDash.Widgets.Twitch;

internal sealed record TwitchTokenSet(
    string ClientId,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string[] Scopes);

internal sealed record TwitchAccount(string UserId);

internal sealed record TwitchFollowedChannel(
    string Login,
    string DisplayName)
{
    public string DisplayLabel => DisplayName;
}

internal sealed record TwitchDeviceAuthorization(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    DateTimeOffset ExpiresAt,
    int PollIntervalSeconds);

internal sealed record TwitchTokenValidation(
    string ClientId,
    string UserId,
    string Login,
    int ExpiresIn,
    string[] Scopes);

internal sealed class TwitchDeviceAuthorizationResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = "";

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = "";

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = "";

    [JsonPropertyName("verification_uri_complete")]
    public string? VerificationUriComplete { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    public int Interval { get; set; } = 5;
}

internal sealed class TwitchTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string[] Scope { get; set; } = [];
}

internal sealed class TwitchTokenValidationResponse
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("scopes")]
    public string[] Scopes { get; set; } = [];

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("login")]
    public string? Login { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }
}

internal sealed class TwitchFollowedStreamsResponse
{
    public List<TwitchFollowedStreamResponse> Data { get; set; } = [];
    public TwitchPaginationResponse Pagination { get; set; } = new();
}

internal sealed class TwitchFollowedStreamResponse
{
    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("user_login")]
    public string UserLogin { get; set; } = "";

    [JsonPropertyName("user_name")]
    public string UserName { get; set; } = "";
}

internal sealed class TwitchPaginationResponse
{
    public string? Cursor { get; set; }
}

internal sealed class TwitchOAuthErrorResponse
{
    public string? Error { get; set; }
    public string? Message { get; set; }
}
