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
}
