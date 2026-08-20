namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The engine's single connection truth — the presenter gate and the USB
/// badge read the same state, and simulation is an explicit state, not a
/// negation.
/// </summary>
public enum ConnectionState
{
    /// <summary>Never connected, or disconnected by dispose — the engine is idle.</summary>
    Disconnected,

    /// <summary>A connection attempt is in progress.</summary>
    Connecting,

    /// <summary>A physical device is connected and frames/touch are live.</summary>
    Connected,

    /// <summary>No physical device — the app runs in simulation mode; the
    /// reconnect timer keeps probing.</summary>
    Simulated
}
