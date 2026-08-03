using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

internal sealed class TwitchSession
{
    public static TwitchSession Shared { get; } = new();

    private readonly TwitchTokenStore _tokenStore = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();

    private TwitchTokenSet? _tokens;
    private TwitchAccount? _account;
    private IReadOnlyList<TwitchFollowedChannel> _followedChannels = [];
    private DateTimeOffset _lastValidatedAt;
    private CancellationTokenSource? _validationCts;

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
        ModernWigiDashContext context,
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
        ModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        string clientId = ResolveClientId(configuredClientId);
        if (clientId.Length == 0)
            throw new InvalidOperationException("Enter a Twitch Client ID in the widget settings or set MODERNWIGIDASH_TWITCH_CLIENT_ID.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var api = new TwitchApiClient(clientId);
            TwitchDeviceAuthorization device = await api.StartDeviceAuthorizationAsync(cancellationToken).ConfigureAwait(false);
            var host = context as IWidgetHostInteraction;

            host?.ShowDeviceAuthorization("Twitch", device.VerificationUri, device.UserCode, device.ExpiresAt);
            TryOpenBrowser(device.VerificationUri, context);

            try
            {
                TwitchTokenSet token = await api.PollDeviceTokenAsync(device, cancellationToken).ConfigureAwait(false);
                TwitchTokenValidation validation = await api.ValidateAsync(token.AccessToken, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(validation.ClientId, clientId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Twitch returned a token for a different Client ID.");

                token = token with
                {
                    ClientId = clientId,
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, validation.ExpiresIn)),
                    Scopes = validation.Scopes
                };

                ApplyValidatedState(token, validation);
                _tokenStore.Save(token);
                StartValidationMonitor(context);
                await RefreshFollowedChannelsCoreAsync(context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                host?.CloseDeviceAuthorization();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TwitchFollowedChannel>> RefreshFollowedChannelsAsync(
        string? configuredClientId,
        ModernWigiDashContext context,
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
                    await new TwitchApiClient(clientId).RevokeAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
                }
                catch (TwitchApiException)
                {
                    // Local logout still clears credentials when Twitch revocation is unavailable.
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
        ModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        TwitchTokenSet token;
        TwitchAccount account;
        lock (_stateGate)
        {
            token = _tokens ?? throw new InvalidOperationException("Twitch is not logged in.");
            account = _account ?? throw new InvalidOperationException("Twitch account information is unavailable.");
        }

        var api = new TwitchApiClient(token.ClientId);
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
        ModernWigiDashContext context,
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
            if (!forceValidate && _account != null && DateTimeOffset.UtcNow - _lastValidatedAt < TimeSpan.FromHours(1))
                return true;
        }

        var api = new TwitchApiClient(clientId);
        try
        {
            TwitchTokenValidation validation = await api.ValidateAsync(stored.AccessToken, cancellationToken).ConfigureAwait(false);
            TwitchTokenSet validatedToken = stored with
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, validation.ExpiresIn)),
                Scopes = validation.Scopes
            };
            ApplyValidatedState(validatedToken, validation);
            _tokenStore.Save(validatedToken);
            StartValidationMonitor(context);
            return true;
        }
        catch (TwitchApiException ex) when (ex.IsUnauthorized)
        {
            return await RefreshTokenCoreAsync(api, context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> RefreshTokenCoreAsync(
        TwitchApiClient api,
        ModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        TwitchTokenSet? current;
        lock (_stateGate) current = _tokens;
        if (current == null) return false;

        try
        {
            TwitchTokenSet refreshed = await api.RefreshAsync(current.RefreshToken, cancellationToken).ConfigureAwait(false);
            TwitchTokenValidation validation = await api.ValidateAsync(refreshed.AccessToken, cancellationToken).ConfigureAwait(false);
            refreshed = refreshed with
            {
                ClientId = current.ClientId,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, validation.ExpiresIn)),
                Scopes = validation.Scopes
            };
            ApplyValidatedState(refreshed, validation);
            _tokenStore.Save(refreshed);
            StartValidationMonitor(context);
            return true;
        }
        catch (TwitchApiException ex) when (ex.StatusCode is 400 or 401)
        {
            ClearState(deleteStoredToken: true);
            (context as IWidgetHostInteraction)?.RequestInspectorRefresh();
            return false;
        }
    }

    private void ApplyValidatedState(TwitchTokenSet token, TwitchTokenValidation validation)
    {
        lock (_stateGate)
        {
            _tokens = token;
            _account = new TwitchAccount(validation.UserId, validation.Login, validation.Login);
            _lastValidatedAt = DateTimeOffset.UtcNow;
        }
    }

    private void SetFollowedChannels(IReadOnlyList<TwitchFollowedChannel> channels)
    {
        lock (_stateGate) _followedChannels = channels.ToArray();
    }

    private void StartValidationMonitor(ModernWigiDashContext context)
    {
        if (_validationCts is { IsCancellationRequested: false }) return;

        _validationCts = new CancellationTokenSource();
        CancellationTokenSource monitorCts = _validationCts;
        _ = Task.Run(() => ValidationLoopAsync(context, monitorCts.Token));
    }

    private async Task ValidationLoopAsync(ModernWigiDashContext context, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!await EnsureAuthenticatedCoreAsync(null, context, forceValidate: true, cancellationToken).ConfigureAwait(false))
                    {
                        ClearState(deleteStoredToken: true);
                        (context as IWidgetHostInteraction)?.RequestInspectorRefresh();
                        return;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The hourly validation loop ends when the session is logged out or the process exits.
        }
    }

    private void ClearState(bool deleteStoredToken)
    {
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

    private static string FirstNonEmpty(params string?[] values)
    {
        string? value = values.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        return value?.Trim() ?? "";
    }

    private static void TryOpenBrowser(Uri verificationUri, ModernWigiDashContext context)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(verificationUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            context.LogError("Unable to open the Twitch authorization page", ex);
        }
    }
}
