namespace ModernWigiDash.Hardware.Service;

/// <summary>
/// The process-wide vendor service accessor (ADR-0022, the ADR-0011 image):
/// reflection-instantiated widgets cannot receive injected dependencies, so
/// they read the shared <see cref="WigiDashServiceClient"/> through this static.
/// The App's startup wiring sets <see cref="Instance"/> once at boot; widgets
/// read it per render tick (a null instance degrades to the house placeholder).
/// </summary>
public static class VendorService
{
    private static volatile WigiDashServiceClient? _instance;

    /// <summary>The shared client, or null when the App has not wired one yet.</summary>
    public static WigiDashServiceClient? Instance => _instance;

    /// <summary>Sets the shared client. Called once by the App's startup wiring.</summary>
    public static void SetInstance(WigiDashServiceClient? client) => _instance = client;
}
