using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ModernWigiDash.App.Update;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

internal enum UpdateState { Hidden, Available, Downloading, Ready }

/// <summary>
/// The update button's UI states (approved mockup: Griddy icons left of Snap
/// to Grid, hover tooltips per state) and the restart-prompt flow. The startup
/// check runs from SourceInitialized so window construction in tests stays
/// network-free; the swap spawns apply-update.cmd and closes the window.
/// </summary>
public partial class MainWindow
{
    // UpdateButton / UpdateIconPath: the x:Name'd elements are the window's
    // generated internal fields — tests reach them directly.

    private readonly UpdateService _updateService = new();
    private UpdateState _updateState = UpdateState.Hidden;
    private UpdateInfo? _pendingUpdate;

    private async void OnUpdateCheckAtStartup(object? sender, EventArgs e)
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
            if (info is null) return; // up-to-date/offline/failed — silent
            _pendingUpdate = info;
            _ = Dispatcher.InvokeAsync(() => ApplyUpdateState(UpdateState.Available, $"Update v{info.Version} available"));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UPDATE] check failed: {ex.Message}");
        }
    }

    internal void ApplyUpdateState(UpdateState state, string tooltip)
    {
        _updateState = state;
        UpdateButton.ToolTip = tooltip;
        UpdateBadgeModel badge = UpdateBadgeModel.From(state);
        UpdateIconPath.Data = GriddyIconGeometry.FromName(badge.IconName);
        UpdateButton.Visibility = badge.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateIconPath.Fill = new SolidColorBrush(Color.FromRgb(badge.Red, badge.Green, badge.Blue));
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_updateState)
        {
            case UpdateState.Available when _pendingUpdate is not null:
                _ = DownloadUpdateAsync(_pendingUpdate);
                break;
            case UpdateState.Ready:
                ShowRestartPrompt();
                break;
        }
    }

    private async Task DownloadUpdateAsync(UpdateInfo info)
    {
        ApplyUpdateState(UpdateState.Downloading, $"Downloading v{info.Version}… 0%");
        var progress = new Progress<double>(p =>
            UpdateButton.ToolTip = $"Downloading v{info.Version}… {p * 100:F0}%");
        bool ok = await _updateService.DownloadAndStageAsync(info, progress);
        if (!ok)
        {
            ApplyUpdateState(UpdateState.Hidden, ""); // silent fail
            return;
        }
        _pendingUpdate = info;
        ApplyUpdateState(UpdateState.Ready, "Restart to apply");
    }

    private void ShowRestartPrompt()
    {
        if (_pendingUpdate is null) return;
        bool restart = _dialogHost.Confirm("Update ready — restart to apply",
            $"v{_pendingUpdate.Version} is downloaded and staged. It will be installed in place when the app closes. Your profile and theme are preserved.");
        if (!restart) return;

        // Spawn the updater hidden, then close normally (standby teardown).
        // The launch protocol — staged-cmd read, {{RELAUNCH}} substitution,
        // live-cmd write outside the stage, ShellExecute detach — is owned by
        // the service, and a failure keeps the window open (the button hides
        // again instead of the app dying on the UI thread).
        if (!_updateService.LaunchUpdater(_pendingUpdate, AppContext.BaseDirectory))
        {
            ApplyUpdateState(UpdateState.Hidden, "");
            return;
        }

        Close();
    }
}
