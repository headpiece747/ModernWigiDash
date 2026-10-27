using System;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App;

/// <summary>
/// MainWindow partial: IModernWigiDashContext and capability facets implementation,
/// forwarding directly to the decoupled <see cref="ModernWigiDashHostContext"/>.
/// </summary>
public partial class MainWindow
{
    #region IModernWigiDashContext Implementation for Telemetry & Host Services

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread: synchronously when the
    /// caller is already there, otherwise posted to the dispatcher (fire-and-forget).
    /// </summary>
    private void OnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }
        _ = Dispatcher.InvokeAsync(action);
    }

    /// <summary>
    /// Widget log sink: forwards to host context.
    /// </summary>
    /// <param name="message">The message text.</param>
    public void LogInfo(string message) => _hostContext.LogInfo(message);

    /// <summary>
    /// Widget log sink: forwards to host context.
    /// </summary>
    /// <param name="message">The message text.</param>
    /// <param name="ex">Optional exception, appended to the line when present.</param>
    public void LogError(string message, Exception? ex = null) => _hostContext.LogError(message, ex);

    /// <summary>
    /// Requests a canvas repaint: forwards to host context.
    /// </summary>
    public void RequestRender() => _hostContext.RequestRender();

    /// <summary>
    /// Refreshes the inspector panel: forwards to host context.
    /// </summary>
    public void RequestInspectorRefresh() => _hostContext.RequestInspectorRefresh();

    /// <summary>
    /// Shows device authorization dialog: forwards to host context.
    /// </summary>
    public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt)
        => _hostContext.ShowDeviceAuthorization(serviceName, verificationUri, userCode, expiresAt);

    /// <summary>
    /// Closes device authorization dialog: forwards to host context.
    /// </summary>
    public void CloseDeviceAuthorization() => _hostContext.CloseDeviceAuthorization();

    /// <summary>
    /// Persists a widget property: forwards to host context.
    /// </summary>
    public void PersistProperty(object widget, string propertyName, object? value)
        => _hostContext.PersistProperty(widget, propertyName, value);

    /// <summary>
    /// Navigates active page by delta: forwards to host context.
    /// </summary>
    public void NavigatePage(int delta) => _hostContext.NavigatePage(delta);

    /// <summary>
    /// Launches an AutoHotkey script: forwards to host context.
    /// </summary>
    public void LaunchAutoHotkeyScript(string scriptPath) => _hostContext.LaunchAutoHotkeyScript(scriptPath);

    /// <summary>
    /// Stores the machine-local CalDAV password: forwards to host context.
    /// </summary>
    public void SaveCalendarCredential(string feedId, string password) => _hostContext.SaveCalendarCredential(feedId, password);

    /// <summary>
    /// Tries reading hardware sensor snapshot: forwards to host context.
    /// </summary>
    public SensorSnapshotDto? TryReadSensors() => _hostContext.TryReadSensors();

    /// <summary>
    /// Tries reading PresentMon frame time snapshot: forwards to host context.
    /// </summary>
    public FrameTimeSnapshotDto? TryReadFrameTime() => _hostContext.TryReadFrameTime();

    #endregion
}
