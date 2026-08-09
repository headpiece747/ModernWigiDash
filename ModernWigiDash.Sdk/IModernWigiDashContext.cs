namespace ModernWigiDash.Sdk;

public interface IModernWigiDashContext
{
    void LogInfo(string message);
    void LogError(string message, Exception? ex = null);

    // Request a repaint on the Skia canvas
    void RequestRender();

    // Host interaction capabilities (inspector refresh and device authorization UI)
    void RequestInspectorRefresh();
    void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt);
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
