namespace ModernWigiDash.Widgets.Twitch;

/// <summary>
/// The Twitch credential core (Widgets/Twitch): the gated token state machine
/// the session composes — the stored/validated tokens, the account, the
/// last-validated stamp, the token store, and the one write gate. The user
/// paths (restore/login/refresh-channels/logout) hold the gate across their
/// whole operation; the validation tick's verdicts take it only at
/// apply/clear, after their network calls — so a hung validation never holds
/// it across the network while the user waits on login. The still-current
/// snapshot re-check inside the verdicts is what makes that split safe.
/// The single-token-owner invariant lives here: a verdict only lands on the
/// token it was computed from.
/// </summary>
internal sealed class TwitchCredentialCore
{
    /// <summary>
    /// The session-mutation write gate. The user paths hold it across their
    /// whole operation; the validation tick's verdicts take it only at
    /// apply/clear, after their network calls.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateGate = new();

    private readonly TwitchTokenStore _tokenStore;
    private readonly Func<string, TwitchApiClient> _clientFactory;
    private readonly TimeProvider _timeProvider;

    private TwitchTokenSet? _tokens;
    private TwitchAccount? _account;
    private DateTimeOffset _lastValidatedAt;

    public TwitchCredentialCore(
        TwitchTokenStore tokenStore,
        Func<string, TwitchApiClient> clientFactory,
        TimeProvider timeProvider)
    {
        _tokenStore = tokenStore;
        _clientFactory = clientFactory;
        _timeProvider = timeProvider;
    }

    public bool IsAuthenticated
    {
        get
        {
            lock (_stateGate) return _tokens != null && _account != null;
        }
    }

    /// <summary>The live credential state under the state gate (the channel refresh's read).</summary>
    public (TwitchTokenSet Token, TwitchAccount Account) ReadState()
    {
        lock (_stateGate)
        {
            return (
                _tokens ?? throw new InvalidOperationException("Twitch is not logged in."),
                _account ?? throw new InvalidOperationException("Twitch account information is unavailable."));
        }
    }

    /// <summary>
    /// The tick's read side: the detached token snapshot the verdict is
    /// computed from — the state token when present, else the store's, the
    /// same load rule the user path uses (<c>_tokens ?? _tokenStore.Load()</c>).
    /// </summary>
    public TwitchTokenSet? Snapshot()
    {
        lock (_stateGate)
        {
            if (_tokens != null) return _tokens;
        }
        return _tokenStore.Load();
    }

    /// <summary>
    /// The client-id resolution, one owner: the configured value, the
    /// environment variable, then the fallback (the stored token's id).
    /// </summary>
    public static string ResolveClientId(string? configuredClientId, string? fallbackClientId = null)
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

    /// <summary>
    /// The ensure-authenticated leg of the user paths: the stored token
    /// (state, else store) validates or refreshes into the live state.
    /// </summary>
    internal async Task<bool> EnsureAsync(
        string? configuredClientId,
        IModernWigiDashContext context,
        bool forceValidate,
        CancellationToken cancellationToken)
    {
        TwitchTokenSet? stored = Snapshot();
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
            Commit(StampFromValidation(stored, validation), validation);
            return true;
        }
        catch (TwitchApiException ex) when (ex.IsUnauthorized)
        {
            return await RefreshAsync(api, context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The refresh leg: spend the current token's refresh token and commit
    /// the rotated credential, or clear the session when Twitch rejects it.
    /// </summary>
    internal async Task<bool> RefreshAsync(
        TwitchApiClient api,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        TwitchTokenSet? current;
        lock (_stateGate) current = _tokens;
        if (current == null) return false;

        RefreshVerdict verdict = await RefreshVerdictAsync(api, current, context, cancellationToken).ConfigureAwait(false);
        if (verdict is not RotatedToken rotated)
        {
            // The rejection's one log line already landed in the verdict
            // routine; the user path runs under the user gate, so the clear
            // is immediate — no session operation can land between the
            // verdict and the clear.
            Clear(deleteStoredToken: true);
            context.RequestInspectorRefresh();
            return false;
        }
        Commit(rotated.Token, rotated.Validation);
        return true;
    }

    /// <summary>
    /// The validated-token commit, one owner: apply the state, persist. The
    /// session spells the arm-the-monitor half of the commit sequence once
    /// (the core does not own the monitor).
    /// </summary>
    internal void Commit(TwitchTokenSet token, TwitchTokenValidation validation)
    {
        lock (_stateGate)
        {
            _tokens = token;
            _account = new TwitchAccount(validation.UserId);
            _lastValidatedAt = _timeProvider.GetUtcNow();
        }
        _tokenStore.Save(token);
    }

    /// <summary>
    /// The credential-state clear, one owner: the state and the store. The
    /// session spells the monitor-stop and channels-clear halves of the
    /// session-wide clear once.
    /// </summary>
    internal void Clear(bool deleteStoredToken)
    {
        lock (_stateGate)
        {
            _tokens = null;
            _account = null;
            _lastValidatedAt = default;
        }
        if (deleteStoredToken) _tokenStore.Delete();
    }

    /// <summary>
    /// The still-current re-check the verdicts take under the write gate:
    /// false when a different token took the state since the snapshot (a
    /// login, or a newer apply — an apply always re-stamps the expiry, so a
    /// stale snapshot can never value-equal the live token), or when the
    /// store the snapshot came from has been emptied (a logout clears state
    /// AND store — a stale apply must not resurrect a logged-out token).
    /// </summary>
    internal bool SnapshotIsCurrent(TwitchTokenSet snapshot)
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
    internal async Task ApplyIfStillCurrentAsync(
        TwitchTokenSet snapshot,
        TwitchTokenSet token,
        TwitchTokenValidation validation,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        await GateAsync(async ct =>
        {
            if (!SnapshotIsCurrent(snapshot)) return;
            Commit(token, validation);
        }, cancellationToken).ConfigureAwait(false);
        context.RequestInspectorRefresh();
    }

    /// <summary>
    /// The clear verdict under the write gate: a stale snapshot (a session
    /// operation already cleared the state) makes the clear a no-op.
    /// </summary>
    internal async Task ClearStaleTokenAsync(
        TwitchTokenSet snapshot,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        await GateAsync(async ct =>
        {
            if (!SnapshotIsCurrent(snapshot)) return;
            Clear(deleteStoredToken: true);
        }, cancellationToken).ConfigureAwait(false);
        context.RequestInspectorRefresh();
    }

    /// <summary>
    /// The no-token clear verdict under the write gate: a login that landed
    /// after the tick's read (state no longer null) makes the clear a no-op.
    /// </summary>
    internal async Task ClearIfStillTokenlessAsync(IModernWigiDashContext context, CancellationToken cancellationToken)
    {
        await GateAsync(async ct =>
        {
            lock (_stateGate)
            {
                if (_tokens != null) return;
            }
            Clear(deleteStoredToken: true);
        }, cancellationToken).ConfigureAwait(false);
        context.RequestInspectorRefresh();
    }

    /// <summary>
    /// The write-gate hold for a session operation: the user paths take it
    /// across their whole operation (the channel refresh's network call
    /// included); the tick's verdicts take it only inside their re-check.
    /// </summary>
    internal async Task GateAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The write-gate hold for an operation with a result (the user paths' verdicts).</summary>
    internal async Task<T> GateAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The logout's read: the live token and its client id (null when logged out).</summary>
    public (TwitchTokenSet? Token, string? ClientId) ReadCredential()
    {
        lock (_stateGate)
        {
            return (_tokens, _tokens?.ClientId);
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
    internal TwitchTokenSet StampFromValidation(TwitchTokenSet token, TwitchTokenValidation validation, string? clientId = null) =>
        token with
        {
            ClientId = clientId ?? token.ClientId,
            ExpiresAt = _timeProvider.GetUtcNow().AddSeconds(Math.Max(1, validation.ExpiresIn)),
            Scopes = validation.Scopes
        };

    /// <summary>
    /// The one refresh verdict: what spending the refresh token produced —
    /// the rotated token, validated and stamped, or the 400/401 rejection
    /// that voids the session. The rotation rule (which codes count) and the
    /// rejection's one log line live in the verdict's producer
    /// (<see cref="RefreshVerdictAsync"/>), so a changed rejection policy
    /// lands in one place; the commit/clear strategy stays with the caller.
    /// </summary>
    internal abstract record RefreshVerdict
    {
        public static RefreshVerdict Rotated(TwitchTokenSet token, TwitchTokenValidation validation)
            => new RotatedToken(token, validation);

        public static RefreshVerdict Rejected()
            => new RejectedRefresh();
    }

    /// <summary>The rotated token: the refresh token was spent, the rotated
    /// access token validated, and the validation stamped onto it.</summary>
    internal sealed record RotatedToken(TwitchTokenSet Token, TwitchTokenValidation Validation) : RefreshVerdict;

    /// <summary>The rejection: Twitch refused the refresh token (400/401) —
    /// the session is void and the caller clears through its strategy.</summary>
    internal sealed record RejectedRefresh() : RefreshVerdict;

    /// <summary>
    /// Produces the one refresh verdict: spend the refresh token (Twitch
    /// rotates refresh tokens; spending a stale one would void a newer
    /// login's), validate the rotated access token, and stamp the validation
    /// onto it. A 400/401 rejection on EITHER leg returns the named
    /// <see cref="RejectedRefresh"/> arm with its one log line (a rotated
    /// access token that fails validation is a dead credential set: the
    /// refresh token is already spent) — the rotation rule's single owner.
    /// Other API/network failures propagate (the caller's existing policy
    /// for transient errors).
    /// </summary>
    internal async Task<RefreshVerdict> RefreshVerdictAsync(
        TwitchApiClient api,
        TwitchTokenSet source,
        IModernWigiDashContext context,
        CancellationToken cancellationToken)
    {
        TwitchTokenSet refreshed;
        TwitchTokenValidation validation;
        try
        {
            refreshed = await api.RefreshAsync(source.RefreshToken, cancellationToken).ConfigureAwait(false);
            validation = await api.ValidateAsync(refreshed.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (TwitchApiException ex) when (ex.StatusCode is 400 or 401)
        {
            context.LogError($"Twitch token refresh rejected (HTTP {ex.StatusCode}): {ex.Message}");
            return RefreshVerdict.Rejected();
        }
        return RefreshVerdict.Rotated(StampFromValidation(refreshed, validation, source.ClientId), validation);
    }
}
