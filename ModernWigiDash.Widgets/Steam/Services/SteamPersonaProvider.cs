namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The one owner of the Steam persona/avatar sub-state machine: the
/// community-API credentials, the persona fetch (the client's 5-minute cache
/// bounds the HTTP), the avatar texture with its <see cref="RetiredBitmapSet"/>
/// retirement discipline (the UAF rule: a replaced avatar is retired and
/// disposed on the render thread's drain, never on the background thread
/// that replaced it), and the persona-change publish. The service keeps the
/// thin facade (credentials in, persona out) and the snapshot composition:
/// the provider publishes persona changes, and the service folds them into
/// the snapshot it owns.
/// </summary>
internal sealed class SteamPersonaProvider : IDisposable
{
    private readonly SteamWebApiClient _api;
    private readonly Action<SteamPersona?> _publish;
    private string _apiKey = "";
    private string _steamId64 = "";
    private string _avatarUrlForCurrent = "";
    private volatile SteamPersona? _persona;
    private volatile SKBitmap? _avatar;
    private readonly RetiredBitmapSet _retired = new();
    private bool _disposed;

    /// <summary>The persona provider over the community-API client and the persona-change publish sink.</summary>
    public SteamPersonaProvider(SteamWebApiClient api, Action<SteamPersona?> publish)
    {
        _api = api;
        _publish = publish;
    }

    /// <summary>The current persona (null when the credentials are blank or the fetch failed).</summary>
    public SteamPersona? Persona => _persona;

    /// <summary>
    /// The owner's avatar texture (the showcase banner), or null (the banner
    /// draws its placeholder disc). Read on the render thread.
    /// </summary>
    public SKBitmap? Avatar => _avatar;

    /// <summary>The retirement set (test seam: observes the pending-drain state).</summary>
    internal RetiredBitmapSet RetirementSet => _retired;

    /// <summary>
    /// Drains the retired avatar texture on the render thread (the UAF rule:
    /// a replaced avatar is freed here, before the canvas draws).
    /// </summary>
    public void DisposeRetired() => _retired.DisposeRetired();

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
        Refresh();
    }

    /// <summary>
    /// Re-runs the persona leg (the service's tick calls it every 3 seconds):
    /// a blank credential pair clears the persona, else a fire-and-forget
    /// fetch (persona, then the avatar only when its URL changed) lands and
    /// publishes. The client's 5-minute cache keeps the persona HTTP bounded.
    /// </summary>
    public void Refresh()
    {
        if (_disposed)
            return;

        string apiKey = _apiKey;
        string steamId64 = _steamId64;
        if (string.IsNullOrWhiteSpace(apiKey) || !SteamWebApiClient.IsValidSteamId64(steamId64))
        {
            if (_persona is not null || _avatar is not null)
            {
                _persona = null;
                RetireAvatar();
                _avatarUrlForCurrent = "";
                _publish(null);
            }
            return;
        }

        _ = FetchPersonaAndAvatarAsync(apiKey, steamId64);
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

        _publish(persona);
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

        _publish(persona);
    }

    private void SetAvatar(SKBitmap bitmap)
    {
        SKBitmap? previous = _avatar;
        _avatar = bitmap;
        _retired.Retire(previous);
    }

    private void RetireAvatar()
    {
        SKBitmap? previous = _avatar;
        _avatar = null;
        _retired.Retire(previous);
    }

    /// <summary>Teardown: disposes the live avatar texture and every pending retirement.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        SKBitmap? avatar = _avatar;
        _avatar = null;
        avatar?.Dispose();
        _retired.DisposeAll();
    }
}
