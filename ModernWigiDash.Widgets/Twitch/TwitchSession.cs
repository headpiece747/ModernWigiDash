namespace ModernWigiDash.Widgets.Twitch;

/// <summary>
/// The Twitch session (Widgets/Twitch): the composition root the widgets
/// drive — the credential core (the gated token state machine), the
/// validation monitor (the hourly loop), the followed-channels state, the
/// browser-open seam, and the client-id resolution. The public surface is
/// the four user paths (restore/login/refresh-channels/logout) plus the
/// followed-channels and authenticated reads; the modules stay internal.
/// The commit rule (apply the validated state, persist, arm the monitor)
/// and the session-wide clear (stop the monitor, clear the core, clear the
/// channels) are spelled here once, so the sequence cannot drift between
/// the user paths.
/// </summary>
internal sealed class TwitchSession
{
    public static TwitchSession Shared { get; } = new();

    private readonly TwitchCredentialCore _core;
    private readonly TwitchValidationMonitor _monitor;
    private readonly Func<string, TwitchApiClient> _clientFactory;
    private readonly Action<Uri> _openBrowser;
    private readonly Lock _stateGate = new();

    private IReadOnlyList<TwitchFollowedChannel> _followedChannels = [];

    /// <summary>Production entry point used by the widgets (reflection-instantiated).</summary>
    public TwitchSession()
        : this(new TwitchTokenStore(), clientId => new TwitchApiClient(clientId), TimeProvider.System, OpenAuthorizationPage)
    {
    }

    /// <summary>Test seam: injectable token store, client factory, clock, and browser open.</summary>
    internal TwitchSession(TwitchTokenStore tokenStore, Func<string, TwitchApiClient> clientFactory, TimeProvider timeProvider, Action<Uri> openBrowser)
    {
        _core = new TwitchCredentialCore(tokenStore, clientFactory, timeProvider);
        _monitor = new TwitchValidationMonitor(_core, clientFactory);
        _clientFactory = clientFactory;
        _openBrowser = openBrowser;
    }

    public IReadOnlyList<TwitchFollowedChannel> FollowedChannels
    {
        get
        {
            lock (_stateGate) return _followedChannels;
        }
    }

    public bool IsAuthenticated => _core.IsAuthenticated;

    /// <summary>Test seam: the validation monitor's hourly tick, driven
    /// through the session (the monitor is internal; the tick's contract —
    /// the stamp rule, the rejection clears, the gate contract — is asserted
    /// from here against the session's observable state).</summary>
    internal Task<bool> ValidateTickAsync(IModernWigiDashContext context, CancellationToken cancellationToken)
        => _monitor.ValidateTickAsync(context, cancellationToken);

    public Task<bool> RestoreAsync(
        string? configuredClientId,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
        => _core.GateAsync(async ct =>
        {
            if (!await _core.EnsureAsync(configuredClientId, context, forceValidate: false, ct).ConfigureAwait(false))
            {
                SetFollowedChannels([]);
                return false;
            }

            await RefreshFollowedChannelsCoreAsync(context, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task LoginAsync(
        string? configuredClientId,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
        => _core.GateAsync(async ct =>
        {
            string clientId = TwitchCredentialCore.ResolveClientId(configuredClientId);
            if (clientId.Length == 0)
                throw new InvalidOperationException("Enter a Twitch Client ID in the widget settings or set MODERNWIGIDASH_TWITCH_CLIENT_ID.");

            var api = _clientFactory(clientId);
            TwitchDeviceAuthorization device = await api.StartDeviceAuthorizationAsync(ct).ConfigureAwait(false);

            context.ShowDeviceAuthorization("Twitch", device.VerificationUri, device.UserCode, device.ExpiresAt);
            TryOpenBrowser(device.VerificationUri, context);

            try
            {
                TwitchTokenSet token = await api.PollDeviceTokenAsync(device, ct).ConfigureAwait(false);
                TwitchTokenValidation validation = await api.ValidateAsync(token.AccessToken, ct).ConfigureAwait(false);
                if (!string.Equals(validation.ClientId, clientId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Twitch returned a token for a different Client ID.");

                CommitValidatedToken(token, validation, clientId, context);
                await RefreshFollowedChannelsCoreAsync(context, ct).ConfigureAwait(false);
            }
            finally
            {
                context.CloseDeviceAuthorization();
            }
        }, cancellationToken);

    public Task<IReadOnlyList<TwitchFollowedChannel>> RefreshFollowedChannelsAsync(
        string? configuredClientId,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
        => _core.GateAsync(async ct =>
        {
            if (!await _core.EnsureAsync(configuredClientId, context, forceValidate: false, ct).ConfigureAwait(false))
            {
                SetFollowedChannels([]);
                return [];
            }

            return await RefreshFollowedChannelsCoreAsync(context, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken)
        => _core.GateAsync(async ct =>
        {
            (TwitchTokenSet? token, string? clientId) = _core.ReadCredential();

            if (token != null && !string.IsNullOrWhiteSpace(clientId))
            {
                try
                {
                    await _clientFactory(clientId).RevokeAsync(token.AccessToken, ct).ConfigureAwait(false);
                }
                catch (TwitchApiException)
                {
                    // Local logout still clears credentials when Twitch revocation is unavailable.
                    System.Diagnostics.Debug.WriteLine("Twitch token revocation failed; proceeding with local logout.");
                }
            }

            ClearSessionState(deleteStoredToken: true);
        }, cancellationToken);

    /// <summary>
    /// The validated-token commit, one owner: apply the state, persist, arm
    /// the validation monitor. The user paths call it; the tick's gated
    /// apply commits through the core (its monitor arm is a no-op while the
    /// monitor is live), so the commit sequence is spelled once.
    /// </summary>
    private void CommitValidatedToken(TwitchTokenSet token, TwitchTokenValidation validation, string? clientId, IModernWigiDashContext context)
    {
        _core.Commit(_core.StampFromValidation(token, validation, clientId), validation);
        _monitor.Arm(context, onCleared: () => SetFollowedChannels([]));
    }

    /// <summary>
    /// The session-wide clear, one owner: stop the monitor (a tick in
    /// flight cannot re-arm it), clear the credential core (state + store),
    /// clear the channels. The tick's verdicts clear through the core only;
    /// their channel half is the empty-followed-channels state the next
    /// successful refresh re-fills.
    /// </summary>
    private void ClearSessionState(bool deleteStoredToken)
    {
        _monitor.Stop();
        _core.Clear(deleteStoredToken);
        SetFollowedChannels([]);
    }

    /// <summary>
    /// The followed-channels refresh leg: fetch the live channels through
    /// the client, and on an unauthorized response refresh the credential
    /// (the core's rotation rule) and refetch once.
    /// </summary>
    private async Task<IReadOnlyList<TwitchFollowedChannel>> RefreshFollowedChannelsCoreAsync(
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        (TwitchTokenSet token, TwitchAccount account) = _core.ReadState();

        var api = _clientFactory(token.ClientId);
        try
        {
            var channels = await api.GetFollowedLiveChannelsAsync(token.AccessToken, account.UserId, cancellationToken).ConfigureAwait(false);
            SetFollowedChannels(channels);
            return channels;
        }
        catch (TwitchApiException ex) when (ex.IsUnauthorized)
        {
            // The refresh rejection cleared the core (state + store) through
            // its strategy; finish the session-wide clear: stop the monitor
            // (it would only tick against an empty session) and clear the
            // channels.
            if (!await _core.RefreshAsync(api, context, cancellationToken).ConfigureAwait(false))
            {
                ClearSessionState(deleteStoredToken: false);
                return [];
            }

            (token, account) = _core.ReadState();

            var channels = await api.GetFollowedLiveChannelsAsync(token.AccessToken, account.UserId, cancellationToken).ConfigureAwait(false);
            SetFollowedChannels(channels);
            return channels;
        }
    }

    private void SetFollowedChannels(IReadOnlyList<TwitchFollowedChannel> channels)
    {
        lock (_stateGate) _followedChannels = channels.ToArray();
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
