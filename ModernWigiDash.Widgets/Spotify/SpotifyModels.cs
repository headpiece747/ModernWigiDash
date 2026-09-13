using System.Text.Json.Serialization;

namespace ModernWigiDash.Widgets.Spotify;

/// <summary>
/// The Spotify credential set persisted by <see cref="SpotifyTokenStore"/>:
/// the user token from the device flow plus its expiry. Spotify's device flow
/// returns an access + refresh token pair with a single expiry; no client id is
/// stored (the client id is a public app identifier re-read from settings or
/// the environment, mirroring the Twitch session).
/// </summary>
internal sealed record SpotifyTokenSet(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string[] Scopes);

/// <summary>The in-flight device authorization: what the user must act on and when to poll.</summary>
internal sealed record SpotifyDeviceAuthorization(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    DateTimeOffset ExpiresAt,
    int PollIntervalSeconds);

/// <summary>A track currently playing on the user's account (from /me/player).</summary>
internal sealed record SpotifyNowPlaying(
    string TrackName,
    string ArtistName,
    string? AlbumName,
    string? AlbumImageUrl,
    bool IsPlaying,
    long PositionMs,
    long DurationMs);

/// <summary>A playlist the user owns or follows (from /me/playlists).</summary>
internal sealed record SpotifyPlaylist(
    string Id,
    string Name,
    string? ImageUrl);

/// <summary>The device-authorization start response from Spotify's OAuth endpoint.</summary>
internal sealed class SpotifyDeviceStartResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = "";

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = "";

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; } = 5;
}

/// <summary>The token response from Spotify's OAuth token endpoint (device poll + refresh share this shape).</summary>
internal sealed class SpotifyTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";
}

/// <summary>The error body Spotify returns for failed token requests.</summary>
internal sealed class SpotifyErrorResponse
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

/// <summary>The /me/player response: the active playback state.</summary>
internal sealed class SpotifyPlayerResponse
{
    [JsonPropertyName("is_playing")]
    public bool IsPlaying { get; set; }

    [JsonPropertyName("progress_ms")]
    public long ProgressMs { get; set; }

    [JsonPropertyName("item")]
    public SpotifyPlayerItem? Item { get; set; }
}

internal sealed class SpotifyPlayerItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("album")]
    public SpotifyAlbum? Album { get; set; }

    [JsonPropertyName("artists")]
    public List<SpotifyArtist>? Artists { get; set; }
}

internal sealed class SpotifyAlbum
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("images")]
    public List<SpotifyImage>? Images { get; set; }
}

internal sealed class SpotifyArtist
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

internal sealed class SpotifyImage
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

/// <summary>The /me/playlists paginated response.</summary>
internal sealed class SpotifyPlaylistsResponse
{
    [JsonPropertyName("items")]
    public List<SpotifyPlaylistItem>? Items { get; set; }
}

internal sealed class SpotifyPlaylistItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("images")]
    public List<SpotifyImage>? Images { get; set; }
}
