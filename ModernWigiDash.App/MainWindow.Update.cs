using System.Windows.Threading;
using ModernWigiDash.App.Update;

namespace ModernWigiDash.App;

/// <summary>
/// The update button's WPF wiring and the restart-prompt flow. The state
/// machine (the check/download/failure transitions, the one spelling of
/// every tooltip) is the app-side <see cref="UpdateFlow"/>; the window
/// applies the flow's <see cref="UpdateUiState"/> render units to the
/// x:Name'd elements and owns the restart-prompt dialog and the close (the
/// launch decision routes through <c>UpdateFlow.OnClick</c>; the launch
/// protocol — staged-cmd read, {{RELAUNCH}} substitution, ShellExecute
/// detach — stays with <see cref="UpdateService"/>).
///
/// The startup check runs from SourceInitialized so window construction in
/// tests stays network-free.
/// </summary>
public partial class MainWindow
{
    private readonly UpdateService _updateService = new();
    private readonly UpdateFlow _updateFlow = new();

    private async void OnUpdateCheckAtStartup(object? _, EventArgs e)
    {
        // SourceInitialized: the window is visible; run the check off-thread.
        // The real network path throws (DNS failure, connection refused, the
        // 10s timeout -> TaskCanceledException) and, because the await below
        // resumes on the captured UI (dispatcher) context, an unhandled
        // exception would surface through DispatcherUnhandledException and
        // terminate the process. Log and stay silent (button hidden); the
        // same guard covers the shutdown edge (posting to a shutting-down
        // dispatcher can throw a canceled-operation exception).

        // Startup recovery runs once, before the check: heal an interrupted
        // swap (.old restore) and clear stale stage/download dirs from failed
        // downloads — otherwise a crash between rename-aside and copy-complete
        // leaves the install with no exe and no automatic restore. One call,
        // owned by the service.
        try
        {
            _updateService.RecoverAtStartup(AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UPDATE] startup recovery failed: {ex.Message}");
        }

        try
        {
            var info = await _updateService.CheckForUpdateAsync();
            // The flow owns the transition + tooltip spelling; a null result
            // (up-to-date/offline/failed) is silent — no render.
            var render = _updateFlow.CheckResult(info);
            if (render is null) return;
            _ = Dispatcher.InvokeAsync(() => ApplyUpdateState(render));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UPDATE] check failed: {ex.Message}");
        }
    }

    internal void ApplyUpdateState(UpdateUiState render)
    {
        UpdateButton.ToolTip = render.Tooltip;
        UpdateBadgeModel badge = UpdateBadgeModel.From(render.State);
        UpdateIconPath.Data = GriddyIconGeometry.FromName(badge.IconName);
        UpdateButton.Visibility = badge.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateIconPath.Fill = new SolidColorBrush(Color.FromRgb(badge.Red, badge.Green, badge.Blue));
    }

    private void UpdateButton_Click(object _, RoutedEventArgs e)
    {
        switch (_updateFlow.OnClick())
        {
            case UpdateClickAction.Download:
                _ = DownloadUpdateAsync(_updateFlow.PendingUpdate!);
                break;
            case UpdateClickAction.Restart:
                ShowRestartPrompt();
                break;
        }
    }

    private async Task DownloadUpdateAsync(UpdateInfo info)
    {
        ApplyUpdateState(_updateFlow.BeginDownload(info));
        var progress = new Progress<double>(p =>
            UpdateButton.ToolTip = UpdateFlow.DownloadingTooltip(info, p));
        bool ok = await _updateService.DownloadAndStageAsync(info, progress);
        ApplyUpdateState(_updateFlow.DownloadComplete(info, ok));
    }

    private void ShowRestartPrompt()
    {
        var info = _updateFlow.PendingUpdate;
        if (info is null) return;
        bool restart = _dialogHost.Confirm("Update ready — restart to apply",
            $"v{info.Version} is downloaded and staged. It will be installed in place when the app closes. Your profile and theme are preserved.");
        if (!restart) return;

        // Spawn the updater hidden, then close normally (standby teardown).
        // The launch protocol — staged-cmd read, {{RELAUNCH}} substitution,
        // live-cmd write outside the stage, ShellExecute detach — is owned by
        // the service, and a failure routes through the flow's failure
        // transition (the window stays open, the button hides instead of the
        // app dying on the UI thread).
        if (!_updateService.LaunchUpdater(info, AppContext.BaseDirectory))
        {
            ApplyUpdateState(_updateFlow.Fail());
            return;
        }

        Close();
    }
}
