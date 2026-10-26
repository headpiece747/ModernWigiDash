namespace ModernWigiDash.Widgets.Twitch;

/// <summary>
/// The Twitch validation monitor (Widgets/Twitch): the hourly
/// <see cref="PeriodicTimer"/> loop that keeps the session's credential
/// valid, owning the CTS's cancel-and-drop rule (a validation task in
/// flight may still hold the token, so it is cancelled and dropped, never
/// disposed while the loop may run) and the tick's gate contract: the read
/// side (the detached token snapshot) and the network calls run OUTSIDE the
/// user gate — a hung or slow validation must not hold the lock the user's
/// login/logout/refresh waits on — and the gate is taken only at the
/// verdict (apply or clear), where the still-current re-check makes a stale
/// verdict a no-op.
/// </summary>
internal sealed class TwitchValidationMonitor
{
    private readonly TwitchCredentialCore _core;
    private readonly Func<string, TwitchApiClient> _clientFactory;
    private CancellationTokenSource? _cts;

    internal TwitchValidationMonitor(
        TwitchCredentialCore core,
        Func<string, TwitchApiClient> clientFactory)
    {
        _core = core;
        _clientFactory = clientFactory;
    }

    /// <summary>
    /// Arms the hourly loop (idempotent: a live, uncancelled monitor is
    /// left running). The loop ends when a failed verdict clears the
    /// session — firing <paramref name="onCleared"/> (the session's
    /// channels-clear half; the tick's verdicts clear the core only) and
    /// stopping the monitor so the next login arms a fresh loop — or when
    /// the CTS is cancelled (logout / process exit).
    /// </summary>
    internal void Arm(IModernWigiDashContext context, Action? onCleared = null)
    {
        if (_cts is { IsCancellationRequested: false }) return;

        _cts = new CancellationTokenSource();
        CancellationTokenSource monitorCts = _cts;
        _ = Task.Run(() => RunAsync(context, monitorCts.Token, onCleared), monitorCts.Token);
    }

    /// <summary>
    /// Stops the loop: cancel and drop rather than dispose — a tick in
    /// flight may still hold the token (the deferral pattern used by the
    /// Sdk loops), and disposing a CTS a running task holds faults its
    /// cancellation registration.
    /// </summary>
    internal void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task RunAsync(IModernWigiDashContext context, CancellationToken cancellationToken, Action? onCleared)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await ValidateTickAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    // A failed verdict ends the monitor: fire the session's
                    // channels-clear half (the verdict cleared the core only),
                    // stop the CTS so the next login arms a fresh loop, and
                    // exit without one more tick.
                    onCleared?.Invoke();
                    Stop();
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
    /// The monitor's hourly tick: validate the detached token snapshot,
    /// refreshing through the rotation rule when the access token has
    /// expired, and land the verdict under the write gate's still-current
    /// re-check. False when the verdict ends the monitor (the session was
    /// cleared).
    /// </summary>
    internal async Task<bool> ValidateTickAsync(IModernWigiDashContext context, CancellationToken cancellationToken)
    {
        TwitchTokenSet? snapshot = _core.Snapshot();
        if (snapshot == null)
        {
            await _core.ClearIfStillTokenlessAsync(context, cancellationToken).ConfigureAwait(false);
            return false;
        }

        string clientId = TwitchCredentialCore.ResolveClientId(null, snapshot.ClientId);
        if (clientId.Length == 0 || !string.Equals(clientId, snapshot.ClientId, StringComparison.Ordinal))
        {
            await _core.ClearStaleTokenAsync(snapshot, context, cancellationToken).ConfigureAwait(false);
            return false;
        }

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
            if (!_core.SnapshotIsCurrent(snapshot)) return true;

            TwitchCredentialCore.RefreshVerdict verdict = await _core.RefreshVerdictAsync(api, snapshot, context, cancellationToken).ConfigureAwait(false);
            if (verdict is not TwitchCredentialCore.RotatedToken rotated)
            {
                // The rejection's one log line already landed in the verdict
                // routine; the clear takes the still-current re-check.
                await _core.ClearStaleTokenAsync(snapshot, context, cancellationToken).ConfigureAwait(false);
                return false;
            }
            await _core.ApplyIfStillCurrentAsync(snapshot, rotated.Token, rotated.Validation, context, cancellationToken).ConfigureAwait(false);
            return true;
        }

        TwitchTokenSet stamped = _core.StampFromValidation(snapshot, validation);
        await _core.ApplyIfStillCurrentAsync(snapshot, stamped, validation, context, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
