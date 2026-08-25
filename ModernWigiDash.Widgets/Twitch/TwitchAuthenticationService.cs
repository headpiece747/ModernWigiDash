namespace ModernWigiDash.Widgets.Twitch;

internal sealed class TwitchSession
{
    public static TwitchSession Shared { get; } = new();

    private readonly TwitchTokenStore _tokenStore;
    private readonly Func<string, TwitchApiClient> _clientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Action<Uri> _openBrowser;
    /// <summary>
    /// The session-mutation write gate. The user paths (restore/login/
    /// refresh-channels/logout) hold it across their whole operation; the
    /// validation tick's verdicts take it only at apply/clear, after their
    /// network calls — so a hung validation never holds it across the
    /// network while the user waits on login. The still-current snapshot
    /// re-check inside the verdicts is what makes that split safe.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateGate = new();

    private TwitchTokenSet? _tokens;
    private TwitchAccount? _account;
    private IReadOnlyList<TwitchFollowedChannel> _followedChannels = [];
    private DateTimeOffset _lastValidatedAt;
    private CancellationTokenSource? _validationCts;

    /// <summary>Production entry point used by the widgets (reflection-instantiated).</summary>
    public TwitchSession()
        : this(new TwitchTokenStore(), clientId => new TwitchApiClient(clientId), TimeProvider.System, OpenAuthorizationPage)
    {
    }

    /// <summary>Test seam: injectable token store, client factory, clock, and browser open.</summary>
    internal TwitchSession(TwitchTokenStore tokenStore, Func<string, TwitchApiClient> clientFactory, TimeProvider timeProvider, Action<Uri> openBrowser)
    {
        _tokenStore = tokenStore;
        _clientFactory = clientFactory;
        _timeProvider = timeProvider;
        _openBrowser = openBrowser;
    }

    public IReadOnlyList<TwitchFollowedChannel> FollowedChannels
    {
        get
        {
            lock (_stateGate) return _followedChannels;
        }
    }

    public bool IsAuthenticated
    {
        get
        {
            lock (_stateGate) return _tokens != null && _account != null;
        }
    }

    public async Task<bool> RestoreAsync(
        string? configuredClientId,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await EnsureAuthenticatedCoreAsync(configuredClientId, context, forceValidate: false, cancellationToken).ConfigureAwait(false))
            {
                SetFollowedChannels([]);
                return false;
            }

            await RefreshFollowedChannelsCoreAsync(context, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LoginAsync(
        string? configuredClientId,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        string clientId = ResolveClientId(configuredClientId);
        if (clientId.Length == 0)
            throw new InvalidOperationException("Enter a Twitch Client ID in the widget settings or set MODERNWIGIDASH_TWITCH_CLIENT_ID.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var api = _clientFactory(clientId);
            TwitchDeviceAuthorization device = await api.StartDeviceAuthorizationAsync(cancellationToken).ConfigureAwait(false);

            context.ShowDeviceAuthorization("Twitch", device.VerificationUri, device.UserCode, device.ExpiresAt);
            TryOpenBrowser(device.VerificationUri, context);

            try
            {
                TwitchTokenSet token = await api.PollDeviceTokenAsync(device, cancellationToken).ConfigureAwait(false);
                TwitchTokenValidation validation = await api.ValidateAsync(token.AccessToken, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(validation.ClientId, clientId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Twitch returned a token for a different Client ID.");

                token = StampFromValidation(token, validation, clientId);
                CommitValidatedToken(token, validation, context);
                await RefreshFollowedChannelsCoreAsync(context, cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<TwitchFollowedChannel>> RefreshFollowedChannelsAsync(
        string? configuredClientId,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await EnsureAuthenticatedCoreAsync(configuredClientId, context, forceValidate: false, cancellationToken).ConfigureAwait(false))
            {
                SetFollowedChannels([]);
                return [];
            }

            return await RefreshFollowedChannelsCoreAsync(context, cancellationToken).ConfigureAwait(false);
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
            TwitchTokenSet? tokens;
            string? clientId;
            lock (_stateGate)
            {
                tokens = _tokens;
                clientId = _tokens?.ClientId;
            }

            if (tokens != null && !string.IsNullOrWhiteSpace(clientId))
            {
                try
                {
                    await _clientFactory(clientId).RevokeAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
                }
                catch (TwitchApiException)
                {
                    // Local logout still clears credentials when Twitch revocation is unavailable.
                    System.Diagnostics.Debug.WriteLine("Twitch token revocation failed; proceeding with local logout.");
                }
            }

            ClearState(deleteStoredToken: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<TwitchFollowedChannel>> RefreshFollowedChannelsCoreAsync(
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        TwitchTokenSet token;
        TwitchAccount account;
        lock (_stateGate)
        {
            token = _tokens ?? throw new InvalidOperationException("Twitch is not logged in.");
            account = _account ?? throw new InvalidOperationException("Twitch account information is unavailable.");
        }

        var api = _clientFactory(token.ClientId);
        try
        {
            var channels = await api.GetFollowedLiveChannelsAsync(token.AccessToken, account.UserId, cancellationToken).ConfigureAwait(false);
            SetFollowedChannels(channels);
            return channels;
        }
        catch (TwitchApiException ex) when (ex.IsUnauthorized)
        {
            if (!await RefreshTokenCoreAsync(api, context, cancellationToken).ConfigureAwait(false))
            {
                SetFollowedChannels([]);
                return [];
            }

            lock (_stateGate)
            {
                token = _tokens ?? throw new InvalidOperationException("Twitch login expired.");
                account = _account ?? throw new InvalidOperationException("Twitch account information is unavailable.");
            }

            var channels = await api.GetFollowedLiveChannelsAsync(token.AccessToken, account.UserId, cancellationToken).ConfigureAwait(false);
            SetFollowedChannels(channels);
            return channels;
        }
    }

    private async Task<bool> EnsureAuthenticatedCoreAsync(
        string? configuredClientId,
        IModernWigiDashContext context,
        bool forceValidate,
        CancellationToken cancellationToken)
    {
        TwitchTokenSet? stored = _tokens ?? _tokenStore.Load();
        if (stored == null) return false;

        string clientId = ResolveClientId(configuredClientId, stored.ClientId);
        if (clientId.Length == 0 || !string.Equals(clientId, stored.ClientId, StringComparison.Ordinal))
            return false;

        lock (_stateGate)
        {
            _tokens ??= stored;
            if (!forceValidate && _account != null && _timeProvider.GetUtcNow() - _lastValidatedAt < TimeSpan.FromHours(1))
                return true;
        }

        var api = _clientFactory(clientId);
        try
        {
            TwitchTokenValidation validation = await api.ValidateAsync(stored.AccessToken, cancellationToken).ConfigureAwait(false);
            TwitchTokenSet validatedToken = StampFromValidation(stored, validation);
            CommitValidatedToken(validatedToken, validation, context);
            return true;
        }
        catch (TwitchApiException ex) when (ex.IsUnauthorized)
        {
            return await RefreshTokenCoreAsync(api, context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> RefreshTokenCoreAsync(
        TwitchApiClient api,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        TwitchTokenSet? current;
        lock (_stateGate) current = _tokens;
        if (current == null) return false;

        try
        {
            TwitchTokenSet refreshed = await api.RefreshAsync(current.RefreshToken, cancellationToken).ConfigureAwait(false);
            TwitchTokenValidation validation = await api.ValidateAsync(refreshed.AccessToken, cancellationToken).ConfigureAwait(false);
            refreshed = StampFromValidation(refreshed, validation, current.ClientId);
            CommitValidatedToken(refreshed, validation, context);
            return true;
        }
        catch (TwitchApiException ex) when (ex.StatusCode is 400 or 401)
        {
            System.Diagnostics.Debug.WriteLine($"Twitch token refresh rejected (HTTP {ex.StatusCode}): {ex.Message}");
            ClearState(deleteStoredToken: true);
            context.RequestInspectorRefresh();
            return false;
        }
    }

    private void ApplyValidatedState(TwitchTokenSet token, TwitchTokenValidation validation)
    {
        lock (_stateGate)
        {
            _tokens = token;
            _account = new TwitchAccount(validation.UserId);
            _lastValidatedAt = _timeProvider.GetUtcNow();
        }
    }

    /// <summary>
    /// The validated-token stamp fact, one owner: what a validation response
    /// turns a token into. The server's <c>ExpiresIn</c> becomes the expiry,
    /// clamped to at least one second so a zero or negative server value
    /// cannot stamp an already-expired token; the scopes come from the
    /// response; <paramref name="clientId"/> (when given) re-asserts the
    /// owner the token was minted for.
    /// </summary>
    private TwitchTokenSet StampFromValidation(TwitchTokenSet token, TwitchTokenValidation validation, string? clientId = null) =>
        token with
        {
            ClientId = clientId ?? token.ClientId,
            ExpiresAt = _timeProvider.GetUtcNow().AddSeconds(Math.Max(1, validation.ExpiresIn)),
            Scopes = validation.Scopes
        };

    /// <summary>
    /// The validated-token commit, one owner: apply the state, persist, arm
    /// the validation monitor. The user paths call it directly; the tick's
    /// gated apply calls it under its still-current re-check, so the commit
    /// sequence is spelled once.
    /// </summary>
    private void CommitValidatedToken(TwitchTokenSet token, TwitchTokenValidation validation, IModernWigiDashContext context)
    {
        ApplyValidatedState(token, validation);
        _tokenStore.Save(token);
        StartValidationMonitor(context);
    }

    private void SetFollowedChannels(IReadOnlyList<TwitchFollowedChannel> channels)
    {
        lock (_stateGate) _followedChannels = channels.ToArray();
    }

    private void StartValidationMonitor(IModernWigiDashContext context)
    {
        if (_validationCts is { IsCancellationRequested: false }) return;

        _validationCts = new CancellationTokenSource();
        CancellationTokenSource monitorCts = _validationCts;
        _ = Task.Run(() => ValidationLoopAsync(context, monitorCts.Token), monitorCts.Token);
    }

    private async Task ValidationLoopAsync(IModernWigiDashContext context, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await ValidateTickAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    // A failed verdict ends the monitor — ClearState has already
                    // cancelled its CTS; the loop exits without one more tick.
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The hourly validation loop ends when the session is logged out or the process exits.
            System.Diagnostics.Debug.WriteLine("Twitch validation loop canceled; ending the hourly refresh cycle.");
        }
        catch (Exception ex)
        {
            // A transient network/API failure must not kill the hourly monitor
            // silently: log it and let the next tick retry.
            System.Diagnostics.Debug.WriteLine($"Twitch validation loop failed; will retry next hour: {ex.Message}");
        }
    }

    /// <summary>
    /// The validation monitor's hourly tick, split by the gate's contract: the
    /// read side (the detached token snapshot) and the network calls run
    /// OUTSIDE the user gate — a hung or slow validation must not hold the
    /// lock the user's login/logout/refresh waits on — and the gate is taken
    /// only at the verdict (apply or clear), where the still-current re-check
    /// makes a stale verdict a no-op. The single-token-owner invariant is
    /// untouched: a verdict only lands on the token it was computed from.
    /// False when the verdict ends the monitor (the session was cleared).
    /// </summary>
    internal async Task<bool> ValidateTickAsync(IModernWigiDashContext context, CancellationToken cancellationToken)
    {
        TwitchTokenSet? snapshot = ReadStoredToken();
        if (snapshot == null)
        {
            await ClearIfStillTokenlessAsync(context, cancellationToken).ConfigureAwait(false);
            return false;
        }

        string clientId = ResolveClientId(null, snapshot.ClientId);
        if (clientId.Length == 0 || !string.Equals(clientId, snapshot.ClientId, StringComparison.Ordinal))
        {
            await ClearStaleTokenAsync(snapshot, context, cancellationToken).ConfigureAwait(false);
            return false;
        }

        lock (_stateGate) _tokens ??= snapshot;

        var api = _clientFactory(clientId);
        TwitchTokenValidation validation;
        try
        {
            validation = await api.ValidateAsync(snapshot.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (TwitchApiException ex) when (ex.IsUnauthorized)
        {
            // Refresh verdict — the snapshot must still own the state before a
            // refresh token is spent (Twitch rotates refresh tokens; spending
            // a stale one would void a newer login's).
            if (!TokenSnapshotIsCurrent(snapshot)) return true;

            TwitchTokenSet refreshed;
            try
            {
                refreshed = await api.RefreshAsync(snapshot.RefreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (TwitchApiException refreshEx) when (refreshEx.StatusCode is 400 or 401)
            {
                System.Diagnostics.Debug.WriteLine($"Twitch token refresh rejected (HTTP {refreshEx.StatusCode}): {refreshEx.Message}");
                await ClearStaleTokenAsync(snapshot, context, cancellationToken).ConfigureAwait(false);
                return false;
            }

            validation = await api.ValidateAsync(refreshed.AccessToken, cancellationToken).ConfigureAwait(false);
            refreshed = StampFromValidation(refreshed, validation, snapshot.ClientId);
            await ApplyIfStillCurrentAsync(snapshot, refreshed, validation, context, cancellationToken).ConfigureAwait(false);
            return true;
        }

        TwitchTokenSet stamped = StampFromValidation(snapshot, validation);
        await ApplyIfStillCurrentAsync(snapshot, stamped, validation, context, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The tick's read side: the detached token snapshot the verdict is
    /// computed from — the state token when present, else the store's, the
    /// same load rule the user path uses (<c>_tokens ?? _tokenStore.Load()</c>).
    /// </summary>
    private TwitchTokenSet? ReadStoredToken()
    {
        lock (_stateGate)
        {
            if (_tokens != null) return _tokens;
        }
        return _tokenStore.Load();
    }

    /// <summary>
    /// The still-current re-check the verdicts take under the write gate:
    /// false when a different token took the state since the snapshot (a
    /// login, or a newer apply — an apply always re-stamps the expiry, so a
    /// stale snapshot can never value-equal the live token), or when the
    /// store the snapshot came from has been emptied (a logout clears state
    /// AND store — a stale apply must not resurrect a logged-out token).
    /// </summary>
    private bool TokenSnapshotIsCurrent(TwitchTokenSet snapshot)
    {
        lock (_stateGate)
        {
            if (_tokens is { } current) return Equals(current, snapshot);
            return Equals(_tokenStore.Load(), snapshot);
        }
    }

    /// <summary>
    /// The apply verdict under the write gate: the snapshot re-check first,
    /// so a session operation that landed during the network calls turns the
    /// apply into a no-op instead of overwriting the newer state.
    /// </summary>
    private async Task ApplyIfStillCurrentAsync(
        TwitchTokenSet snapshot,
        TwitchTokenSet token,
        TwitchTokenValidation validation,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TokenSnapshotIsCurrent(snapshot)) return;
            CommitValidatedToken(token, validation, context);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The clear verdict under the write gate: a stale snapshot (a session
    /// operation already cleared the state) makes the clear a no-op.
    /// </summary>
    private async Task ClearStaleTokenAsync(
        TwitchTokenSet snapshot,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TokenSnapshotIsCurrent(snapshot)) return;
            ClearState(deleteStoredToken: true);
            context.RequestInspectorRefresh();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The no-token clear verdict under the write gate: a login that landed
    /// after the tick's read (state no longer null) makes the clear a no-op.
    /// </summary>
    private async Task ClearIfStillTokenlessAsync(IModernWigiDashContext context, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (_tokens != null) return;
            }
            ClearState(deleteStoredToken: true);
            context.RequestInspectorRefresh();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ClearState(bool deleteStoredToken)
    {
        // Cancel and drop rather than dispose: a validation task in flight may
        // still hold the token (the deferral pattern used by the Sdk loops).
        _validationCts?.Cancel();
        _validationCts = null;
        lock (_stateGate)
        {
            _tokens = null;
            _account = null;
            _followedChannels = [];
            _lastValidatedAt = default;
        }
        if (deleteStoredToken) _tokenStore.Delete();
    }

    private static string ResolveClientId(string? configuredClientId, string? fallbackClientId = null)
        => FirstNonEmpty(
            configuredClientId,
            Environment.GetEnvironmentVariable("MODERNWIGIDASH_TWITCH_CLIENT_ID"),
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
        // Defense-in-depth: only shell-open trusted https URLs on twitch.tv so a
        // tampered response cannot invoke file:/custom protocol handlers.
        if (!TrustedUriPolicy.IsTwitchAuthorizationUri(verificationUri))
        {
            context.LogError($"Refusing to open non-Twitch authorization URL: {verificationUri}");
            return;
        }

        try
        {
            _openBrowser(verificationUri);
        }
        catch (Exception ex)
        {
            context.LogError("Unable to open the Twitch authorization page", ex);
        }
    }

    /// <summary>The production browser open; the test ctor binds a recorder instead.</summary>
    private static void OpenAuthorizationPage(Uri uri)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
}
