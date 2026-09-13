namespace ModernWigiDash.Widgets.Spotify;

/// <summary>
/// The Spotify session: one process-wide owner of the device-flow login, token
/// refresh, and logout. Mirrors the Twitch session's shape (a write gate for
/// user operations, a state gate for the credential snapshot, an injectable
/// client factory + clock + browser open for tests) but is simpler: no followed
/// channel list to maintain, so there is no hourly validation monitor — the
/// access token is refreshed lazily on a 401 from any Web API read, which is
/// all the Spotify widget needs. A test host binds an isolated token store and
/// a fake client so a stored token never performs a real network call.
/// </summary>
internal sealed class SpotifySession
{
    public static SpotifySession Shared { get; } = new();

    private readonly SpotifyTokenStore _tokenStore;
    private readonly Func<string, SpotifyApiClient> _clientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Action<Uri> _openBrowser;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateGate = new();

    private SpotifyTokenSet? _tokens;
    private string? _accountName;

    /// <summary>Production entry point used by the widgets (reflection-instantiated).</summary>
    public SpotifySession()
        : this(new SpotifyTokenStore(), clientId => new SpotifyApiClient(clientId), TimeProvider.System, OpenAuthorizationPage)
    {
    }

    /// <summary>Test seam: injectable token store, client factory, clock, and browser open.</summary>
    internal SpotifySession(SpotifyTokenStore tokenStore, Func<string, SpotifyApiClient> clientFactory, TimeProvider timeProvider, Action<Uri> openBrowser)
    {
        _tokenStore = tokenStore;
        _clientFactory = clientFactory;
        _timeProvider = timeProvider;
        _openBrowser = openBrowser;
    }

    /// <summary>The authenticated account's display name (null when not logged in).</summary>
    public string? AccountName
    {
        get
        {
            lock (_stateGate) return _accountName;
        }
    }

    public bool IsAuthenticated
    {
        get
        {
            lock (_stateGate) return _tokens != null;
        }
    }

    /// <summary>
    /// Restores a stored token if present. Returns true when a live session is
    /// available after the call. Does NOT hit the network beyond what a stored
    /// token requires (none at restore time: Spotify tokens are validated lazily
    /// on first use).
    /// </summary>
    public async Task<bool> RestoreAsync(string? configuredClientId, IModernWigiDashContext context, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SpotifyTokenSet? stored = _tokens ?? _tokenStore.Load();
            if (stored == null) return false;

            // The client id is re-read from settings/environment; the stored token
            // does not carry it (it is a public app identifier, not a secret). A
            // missing client id does not invalidate the stored token — it only
            // blocks a future refresh, which EnsureAccessTokenAsync reports.
            lock (_stateGate)
            {
                _tokens ??= stored;
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LoginAsync(string? configuredClientId, IModernWigiDashContext context, CancellationToken cancellationToken)
    {
        string clientId = ResolveClientId(configuredClientId);
        if (clientId.Length == 0)
            throw new InvalidOperationException("Enter a Spotify Client ID in the widget settings or set MODERNWIGIDASH_SPOTIFY_CLIENT_ID.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var api = _clientFactory(clientId);
            SpotifyDeviceAuthorization device = await api.StartDeviceAuthorizationAsync(cancellationToken).ConfigureAwait(false);

            context.ShowDeviceAuthorization("Spotify", device.VerificationUri, device.UserCode, device.ExpiresAt);
            TryOpenBrowser(device.VerificationUri, context);

            try
            {
                SpotifyTokenSet token = await api.PollDeviceTokenAsync(device, cancellationToken).ConfigureAwait(false);
                CommitToken(token, context);
            }
            finally
            {
                context.CloseDeviceAuthorization();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClearState(deleteStoredToken: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ensures a live access token, refreshing once on 401. Returns the access
    /// token to use, or null when the session is not recoverable (the caller
    /// renders its placeholder). This is the single lazy-refresh entry point
    /// every Web API read routes through.
    /// </summary>
    public async Task<string?> EnsureAccessTokenAsync(string? configuredClientId, IModernWigiDashContext context, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SpotifyTokenSet? current = _tokens ?? _tokenStore.Load();
            if (current == null) return null;

            string clientId = ResolveClientId(configuredClientId);
            if (clientId.Length == 0) return null;

            lock (_stateGate) _tokens ??= current;

            // If the stored token is still within its expiry window, use it as-is.
            if (_timeProvider.GetUtcNow() < current.ExpiresAt.AddSeconds(-30))
                return current.AccessToken;

            // Expired (or about to be): refresh once.
            var api = _clientFactory(clientId);
            try
            {
                SpotifyTokenSet refreshed = await api.RefreshAsync(current, cancellationToken).ConfigureAwait(false);
                CommitToken(refreshed, context);
                return refreshed.AccessToken;
            }
            catch (SpotifyApiException ex) when (ex.IsUnauthorized)
            {
                // Refresh refused: the session is void.
                ClearState(deleteStoredToken: true);
                context.RequestInspectorRefresh();
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CommitToken(SpotifyTokenSet token, IModernWigiDashContext context)
    {
        lock (_stateGate)
        {
            _tokens = token;
            _accountName = null; // Resolved lazily from /me/player or /me/playlists.
        }
        _tokenStore.Save(token);
        context.RequestInspectorRefresh();
        context.RequestRender();
    }

    private void ClearState(bool deleteStoredToken)
    {
        lock (_stateGate)
        {
            _tokens = null;
            _accountName = null;
        }
        if (deleteStoredToken) _tokenStore.Delete();
    }

    private static string ResolveClientId(string? configuredClientId, string? fallbackClientId = null)
        => FirstNonEmpty(
            configuredClientId,
            Environment.GetEnvironmentVariable("MODERNWIGIDASH_SPOTIFY_CLIENT_ID"),
            fallbackClientId);

    private static string FirstNonEmpty(params ReadOnlySpan<string?> values)
    {
        string? value = null;
        foreach (string? candidate in values)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                value = candidate;
                break;
            }
        }
        return value?.Trim() ?? "";
    }

    private void TryOpenBrowser(Uri verificationUri, IModernWigiDashContext context)
    {
        // Defense-in-depth: only shell-open trusted https URLs on spotify.com so a
        // tampered response cannot invoke file:/custom protocol handlers.
        if (!TrustedUriPolicy.IsSpotifyAuthorizationUri(verificationUri))
        {
            context.LogError($"Refusing to open non-Spotify authorization URL: {verificationUri}");
            return;
        }

        try
        {
            _openBrowser(verificationUri);
        }
        catch (Exception ex)
        {
            context.LogError("Unable to open the Spotify authorization page", ex);
        }
    }

    /// <summary>The production browser open; the test ctor binds a recorder instead.</summary>
    private static void OpenAuthorizationPage(Uri uri)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
}
