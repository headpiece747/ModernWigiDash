using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The hotkey action-fire ritual behind the <see cref="HotkeyButtonWidget"/>:
/// the one owner of the re-entrancy gate, the per-run 30 s cancellation
/// (teardown cancels the in-flight run), and the dispatch (page-flip, the
/// empty-command skip, the AHK script, and the executor seam). The touch-up
/// path and the global-hotkey trigger share this one entry, so "what happens
/// when a hotkey fires" is one module. It reads its inputs through providers
/// (the action, the raw action type for the needs-command rule, the host
/// context, and the executor seam), so the gate/CTS/dispatch rule is drivable
/// without a widget.
/// </summary>
internal sealed class HotkeyActionRunner(
    Func<HotkeyAction> actionProvider,
    Func<string> actionTypeProvider,
    Func<IModernWigiDashContext?> contextProvider,
    Func<Func<IReadOnlyList<HotkeyAction>, CancellationToken, Task>?> executorProvider)
{
    /// <summary>The per-run action timeout: a wedged launch or a stuck chord is abandoned, not left running.</summary>
    private const int ActionTimeoutSeconds = 30;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;

    /// <summary>
    /// The action-fire entry (fire-and-forget safe): a zero-timeout gate
    /// acquire (a saturated gate is a no-op, never a wait, so a concurrent
    /// fire is dropped, not queued), a fresh 30 s CTS, then the dispatch. The
    /// prior-CTS cancel branch is defensive (a saturated gate means a
    /// concurrent run cannot observe a live CTS); the reachable cancel path
    /// is <see cref="DisposeAsync"/>.
    /// </summary>
    public async Task RunAsync()
    {
        if (!await _gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) return;
        if (_cts is { } prior)
        {
            await prior.CancelAsync().ConfigureAwait(false);
            prior.Dispose();
        }
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(ActionTimeoutSeconds));
        var context = contextProvider();
        try
        {
            var action = actionProvider();

            if (action.Kind == HotkeyActionKind.PageNavigate
                && int.TryParse(action.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta))
            {
                (context as IWidgetNavigationContext)?.NavigatePage(delta);
                context?.RequestRender();
                return;
            }

            if (string.IsNullOrWhiteSpace(action.Value) && HotkeyActionCatalog.NeedsCommand(actionTypeProvider()))
            {
                context?.LogError("Hotkey action skipped: Action Path/Command is empty.");
                return;
            }

            if (action.Kind == HotkeyActionKind.AhkScript)
            {
                (context as IWidgetScriptLaunchContext)?.LaunchAutoHotkeyScript(action.Value);
                context?.RequestRender();
                return;
            }

            var executor = executorProvider() ?? HotkeyActionExecutor.ExecuteAsync;
            await executor([action], _cts.Token).ConfigureAwait(false);
            context?.RequestRender();
        }
        catch (OperationCanceledException)
        {
            context?.LogInfo("Hotkey action cancelled (30s timeout or shutdown)");
        }
        catch (Exception ex)
        {
            context?.LogError($"Hotkey action failed: {ex.Message}", ex);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Teardown disposed the gate first: no acquirer remains, so the release is moot.
            }
        }
    }

    /// <summary>Cancels any in-flight action and releases the gate (the widget's teardown).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_cts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }
        _gate.Dispose();
    }
}
