namespace ModernWigiDash.Sdk;

public interface IWidgetContext
{
    // Global telemetry or configuration data passed from host to widget
    string GetSetting(string key, string defaultValue = "");
    void SetSetting(string key, string value);
    void LogInfo(string message);
    void LogError(string message, Exception? ex = null);
    
    // Request a repaint on the Skia canvas
    void RequestRender();
    
    // Shared sensor access bridge (HWiNFO / AIDA64 / System telemetry values)
    bool TryGetSensorValue(string sensorId, out float value);
    string GetSensorFormattedString(string sensorId);
}
