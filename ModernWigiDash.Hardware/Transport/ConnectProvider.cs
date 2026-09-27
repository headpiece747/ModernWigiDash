namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// One connect attempt in <see cref="DisplayHidTransport.Connect"/>'s provider
/// loop: <see cref="TryCreate"/> opens the device and returns a
/// <see cref="ProviderAttempt"/> — the backend, or the reason there is none. The
/// record carries only the leg's identity — its
/// diagnostic <see cref="Tag"/> (the file-log vocabulary the loop and the leg
/// bound into their <c>DiagLog</c> instances), the display name for the logger
/// line, and the leg's success line. The loop spells its own failure lines from
/// the tag, so no message text can drift between the provider record and the leg
/// that emits beside it. The default list holds the WinUSB provider first and
/// the LibUsbDotNet fallback second; tests inject fakes — a fake WinUSB leg
/// and/or a fake LibUsb leg — through the list to drive the connect policy
/// without hardware.
///
/// The attempt type is the whole reason a failure can be named: a
/// <c>Func&lt;ITransferBackend?&gt;</c> could only say "no backend", so a busy
/// device and an absent one reached the engine as the same fact.
/// </summary>
internal sealed record ConnectProvider(
    string Tag,
    Func<ProviderAttempt> TryCreate,
    string DisplayName = "",
    string? SuccessLine = null);
