using System;
using System.Windows;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// MainWindow partial: IModernWigiDashContext host-contract implementation.
/// The dialogs and the inspector panel live in their own modules (DialogHost,
/// Inspector.InspectorController); this file only forwards.
/// </summary>
public partial class MainWindow
{
    #region IModernWigiDashContext Implementation for Telemetry & Host Services

    public void LogInfo(string message) => FileLog.Write($"[Display INFO] {message}");
    public void LogError(string message, Exception? ex = null) => FileLog.Write($"[Display ERROR] {message}{(ex != null ? $": {ex}" : "")}");
    public void RequestRender() => Dispatcher.InvokeAsync(() => SkiaCanvas?.InvalidateVisual());

    public void RequestInspectorRefresh()
    {
        if (Dispatcher.CheckAccess())
        {
            _inspector.Refresh();
            return;
        }

        _ = Dispatcher.InvokeAsync(_inspector.Refresh);
    }

    public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt)
        => _dialogHost.ShowDeviceAuthorization(serviceName, verificationUri, userCode, expiresAt);

    public void CloseDeviceAuthorization()
        => _dialogHost.CloseDeviceAuthorization();

    /// <summary>
    /// Resolves the placed instance that owns <paramref name="widget"/> (by
    /// identity) and persists the property into its PropertyValues — the
    /// companion write to <see cref="ModernWidgetBase.SetProperty"/> so widget
    /// runtime toggles survive Export→Import. A small linear scan over the
    /// profile; property changes are user-frequency, not per-frame.
    /// </summary>
    public void PersistProperty(object widget, string propertyName, object? value)
    {
        foreach (var page in _profile.Pages)
        {
            foreach (var placed in page.Widgets)
            {
                if (!ReferenceEquals(placed.ActiveInstance, widget)) continue;
                placed.PropertyValues[propertyName] = value;
                return;
            }
        }
    }

    #endregion
}
