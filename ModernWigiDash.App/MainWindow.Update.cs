using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ModernWigiDash.App.Update;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

public enum UpdateState { Hidden, Available, Downloading, Ready }

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
        // 10s timeout -> TaskCanceledException) and, with ConfigureAwait(false)
        // inside the service, the continuation lands on a threadpool thread —
        // an unhandled exception here would reach AppDomain.UnhandledException
        // and terminate the process. Log and stay silent (button hidden); the
        // same guard covers the shutdown edge (posting to a shutting-down
        // dispatcher can throw a canceled-operation exception).
        try
        {
            var info = await _updateService.CheckForUpdateAsync();
            if (info is null) return; // up-to-date/offline/failed — silent
            _pendingUpdate = info;
            Dispatcher.InvokeAsync(() => ApplyUpdateState(UpdateState.Available, $"Update v{info.Version} available", info.Version));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UPDATE] check failed: {ex.Message}");
        }
    }

    internal void ApplyUpdateState(UpdateState state, string tooltip, string? version)
    {
        _updateState = state;
        UpdateButton.ToolTip = tooltip;
        string icon = state switch
        {
            UpdateState.Available => "arrow-circle-down",
            UpdateState.Downloading => "swap-horizontal",
            UpdateState.Ready => "refresh",
            _ => ""
        };
        UpdateIconPath.Data = GriddyIconGeometry.FromName(icon);
        UpdateButton.Visibility = state == UpdateState.Hidden ? Visibility.Collapsed : Visibility.Visible;
        UpdateIconPath.Fill = state switch
        {
            UpdateState.Ready => new SolidColorBrush(Color.FromRgb(16, 185, 129)), // green
            UpdateState.Available => new SolidColorBrush(Color.FromRgb(245, 158, 11)), // amber
            _ => new SolidColorBrush(Color.FromRgb(250, 250, 250))
        };
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
        ApplyUpdateState(UpdateState.Downloading, $"Downloading v{info.Version}… 0%", info.Version);
        var progress = new Progress<double>(p =>
            UpdateButton.ToolTip = $"Downloading v{info.Version}… {p * 100:F0}%");
        bool ok = await _updateService.DownloadAndStageAsync(info, progress);
        if (!ok)
        {
            ApplyUpdateState(UpdateState.Hidden, "", null); // silent fail
            return;
        }
        _pendingUpdate = info;
        ApplyUpdateState(UpdateState.Ready, "Restart to apply", info.Version);
    }

    private void ShowRestartPrompt()
    {
        if (_pendingUpdate is null) return;
        bool restart = _dialogHost.Confirm("Update ready — restart to apply",
            $"v{_pendingUpdate.Version} is downloaded and staged. It will be installed in place when the app closes. Your profile and theme are preserved.");
        if (!restart) return;

        // Spawn the updater hidden, then close normally (standby teardown).
        // The staged cmd can vanish between staging and this click (cleanup
        // tools, AV, external wipe) — the read/substitute/start must not throw
        // on the UI thread (DispatcherUnhandledException does not mark
        // non-benign exceptions handled). Log, hide the button again (the
        // download-failure path's shape), and keep the window open.
        try
        {
            string installDir = AppContext.BaseDirectory;
            string stageDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ModernWigiDash", "updates", "staged", _pendingUpdate.Version);
            string cmd = _updateService.StagedCmdPath(_pendingUpdate);
            string relaunch = $"start \"\" \"{installDir}\\ModernWigiDash.App.exe\"";
            string args = $"\"{cmd}\" \"{installDir}\" \"{stageDir}\" ModernWigiDash.App.exe";

            // /S /C with doubled inner quotes: the canonical form cmd.exe handles
            // correctly (a plain /c "..." strips quotes and mangles the script
            // path — "filename, directory name, or volume label syntax is incorrect").
            string cmdExe = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(cmdExe, $"/S /C \"\"{args}\"") { UseShellExecute = false };
            // Replace the {{RELAUNCH}} marker inside the staged cmd with the relaunch line.
            string body = System.IO.File.ReadAllText(cmd);
            string substituted = body.Replace("{{RELAUNCH}}", relaunch);
            if (substituted.Length == body.Length)
                FileLog.Write("[UPDATE] relaunch marker missing in staged cmd; the updater will not relaunch the app");
            System.IO.File.WriteAllText(cmd, substituted);
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UPDATE] launch failed: {ex.Message}");
            ApplyUpdateState(UpdateState.Hidden, "", null);
            return;
        }

        Close();
    }
}
