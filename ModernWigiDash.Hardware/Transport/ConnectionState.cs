namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The engine's single connection truth — the presenter gate, the USB badge,
/// and the transport's own connect verdict all speak this vocabulary, and it
/// is the engine's state machine rather than a bag of booleans.
///
/// The three failure variants are the fix for the 2026-09-27 incident: a bare
/// <c>bool</c> from the transport made "nothing is plugged in" and "the display
/// is plugged in but another client owns it" collapse into one line
/// (<c>No physical device found</c>), so a device that was PRESENT and merely
/// inaccessible read as a device that was GONE. Access denied is
/// ERROR_ACCESS_DENIED on the WinUSB open (another client holds the handle) and
/// <c>libusb Error.Access</c> on the LibUsbDotNet leg — the same fact, two
/// spellings, one variant here.
///
/// The variants are mutually exclusive by construction: a state is one name,
/// never a combination of flags, so a new failure mode is a new variant the
/// compiler forces every reader to handle.
/// </summary>
public enum ConnectionState
{
    /// <summary>Never connected, or disconnected by dispose — the engine is idle.</summary>
    Disconnected,

    /// <summary>A connection attempt is in progress.</summary>
    Connecting,

    /// <summary>A physical device is connected and frames/touch are live.</summary>
    Connected,

    /// <summary>SetupAPI enumeration found no matching display interface: nothing
    /// is attached, so the app renders without driving hardware.</summary>
    DeviceNotFound,

    /// <summary>The display IS attached, but the open was denied — another client
    /// (the vendor Manager/service, or a second dashboard app) owns the USB
    /// handle. Frames cannot flow until that client lets go; unplugging will not
    /// help.</summary>
    DeviceBusy,

    /// <summary>The display is attached and the handle opened, but the device did
    /// not complete initialization (WinUsb_Initialize or the init sequence
    /// failed) — a wedged or mid-reset device, not a missing one.</summary>
    DeviceNotResponding,
}
