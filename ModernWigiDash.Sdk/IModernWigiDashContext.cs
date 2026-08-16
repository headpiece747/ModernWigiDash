namespace ModernWigiDash.Sdk;

/// <summary>
/// The host-services seam handed to every widget at
/// <see cref="IModernWidget.InitializeAsync"/>: logging, repaint requests,
/// inspector refresh, device-authorization dialogs, and property persistence.
/// Widgets reach the host ONLY through this — never through the window or
/// process. All members are safe to call from background threads (the host
/// marshals to its UI thread); they are cheap and may be called at any point
/// after initialization.
/// </summary>
public interface IModernWigiDashContext
{
    /// <summary>Writes an informational line to the shared application log
    /// (display_device.log). Use for diagnostics; never log tokens or PII at
    /// this level.</summary>
    void LogInfo(string message);

    /// <summary>Writes an error line (message plus optional exception) to the
    /// shared application log. The host sanitizes the text before writing.</summary>
    void LogError(string message, Exception? ex = null);

    /// <summary>Requests a repaint of the compositor canvas — the standard way
    /// to surface a widget state change. Safe from any thread; the host
    /// marshals to the UI thread. Call on state changes, not per frame.</summary>
    void RequestRender();

    /// <summary>Requests the inspector panel rebuild its property rows — after
    /// dynamic option lists or action labels changed (e.g. a Twitch login
    /// completed). Safe from any thread.</summary>
    void RequestInspectorRefresh();

    /// <summary>Shows the device-authorization (device flow) dialog for the
    /// named service — the user code and verification URL a widget's OAuth
    /// flow needs the user to act on (Twitch login). Replaces any previously
    /// shown authorization window. Safe from any thread.</summary>
    void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt);

    /// <summary>Closes the device-authorization dialog if one is showing
    /// (login finished or abandoned). Safe from any thread.</summary>
    void CloseDeviceAuthorization();

    /// <summary>
    /// Persists a widget property change into the owning placed instance's
    /// PropertyValues, so the change survives Export→Import. The default is a
    /// no-op (test hosts and other embedders may not track placed instances);
    /// the App's context resolves the placed instance by identity.
    /// </summary>
    void PersistProperty(object widget, string propertyName, object? value)
    {
    }
}
