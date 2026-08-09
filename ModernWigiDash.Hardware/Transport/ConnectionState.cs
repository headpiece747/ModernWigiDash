namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The engine's single connection truth. One value instead of the old
/// lockstep trio (IsConnected / IsHardwareActive / IsSimulationMode) that
/// callers disagreed on: the presenter gate reads "Connected", the badge reads
/// "Connected", and simulation is an explicit state, not a negation.
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
