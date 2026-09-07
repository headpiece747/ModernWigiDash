namespace ModernWigiDash.Sdk;

/// <summary>
/// The core host-services seam handed to every widget at
/// <see cref="IModernWidget.InitializeAsync"/>: logging, repaint requests, and
/// inspector refresh. Widgets reach the host ONLY through this and the optional
/// capability facets (<see cref="IWidgetPropertyPersistingContext"/>,
/// <see cref="IWidgetNavigationContext"/>, <see cref="IWidgetScriptLaunchContext"/>,
/// <see cref="IWidgetCredentialContext"/>) — never through the window or process.
/// All members are safe to call from background threads (the host marshals to
/// its UI thread); they are cheap and may be called at any point after
/// initialization. The former ten-member bag was segmented into these facets so
/// a widget depends on only the capabilities it uses (the
/// <c>IWidgetActionInvoker</c> / <c>IWidgetEditorProvider</c> optional-facet
/// precedent).
/// </summary>
public interface IModernWigiDashContext
{
    /// <summary>Writes an informational line to the shared application log
    /// (display_device.log). Use for diagnostics; never log tokens or PII at
    /// this level. The host flattens, bounds, and redacts the text before
    /// writing — the line that reaches the log is one bounded line.</summary>
    void LogInfo(string message);

    /// <summary>Writes an error line (message plus optional exception) to the
    /// shared application log. The host flattens, bounds, and redacts the text
    /// before writing — a multi-line exception becomes one bounded line and
    /// token-shaped values are redacted.</summary>
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
}
