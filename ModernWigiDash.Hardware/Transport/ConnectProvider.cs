namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// One connect attempt in <see cref="DisplayHidTransport.Connect"/>'s provider
/// loop: <see cref="TryCreate"/> opens the device and returns the backend (or
/// null when this driver stack cannot provide one), and the message fields keep
/// each leg's diagnostic lines byte-identical after the loop refactor. The
/// default list holds the WinUSB provider first and the LibUsbDotNet fallback
/// second; tests inject fakes — including a fake LibUsb leg, which the
/// WinUsbDeviceFactory seam cannot reach — to drive the connect policy without
/// hardware.
/// </summary>
internal sealed record ConnectProvider(
    string Tag,
    Func<ITransferBackend?> TryCreate,
    string ConnectedVia = "WigiDash",
    string? SuccessFileLog = null,
    string InitFailureLog = "Init commands failed");
