namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The one owner of the connect-failure wording. The engine used to spell a
/// single line — "No physical device found - running in simulation mode" — for
/// every outcome, which is how a display that was PRESENT and merely held by
/// another client came to read as a display that was GONE (the 2026-09-27
/// report). <see cref="Line"/> spells one line per
/// <see cref="ConnectionState"/>, and the USB badge spells the same facts in
/// user-facing words; the variants are added here and in the enum together, so
/// a new outcome has exactly one place to be described.
///
/// This is a pure function of the state and the attempt count, so it is
/// testable without a device, a log file, or a clock.
/// </summary>
internal static class ConnectVerdict
{
    /// <summary>
    /// The file-log line for a failed connect attempt: what went wrong, and (for
    /// a repeat) how many attempts have now failed — the count is what tells a
    /// reader whether the backoff is working or the device just came back.
    /// </summary>
    public static string Line(ConnectionState state, int consecutiveFailures) => state switch
    {
        ConnectionState.DeviceNotFound =>
            "No WigiDash interface found - running in simulation mode",
        ConnectionState.DeviceBusy =>
            $"WigiDash found but the USB open was denied (another app holds the device) - attempt {consecutiveFailures}",
        ConnectionState.DeviceNotResponding =>
            $"WigiDash found but did not complete initialization - attempt {consecutiveFailures}",
        // Disconnected means the attempt was abandoned by a concurrent dispose,
        // never a device verdict; Connecting never reaches here. The line keeps
        // the switch total so a new variant cannot fall through to nothing.
        _ => $"Connect did not reach a device state ({state})",
    };
}
