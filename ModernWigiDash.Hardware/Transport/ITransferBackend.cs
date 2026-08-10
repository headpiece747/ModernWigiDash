namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The transfer seam behind <see cref="DisplayHidTransport"/>: everything the
/// transport needs from a USB backend — vendor control in/out and bulk writes.
/// Two real adapters sit at this seam (WinUSB and LibUsbDotNet) plus test
/// fakes, so the transport's connect / init / frame / touch policy is drivable
/// without hardware and the backend choice is made once in
/// <see cref="DisplayHidTransport.Connect"/> instead of re-decided per call.
/// The WinUSB attempt is constructed by the WinUSB provider's
/// <c>TryCreate</c> (default: a real <see cref="WinUsbBulkDevice"/>), so the
/// connect policy — open, PING, init, fallback — is drivable with a fake
/// device injected through the provider list.
/// </summary>
internal interface ITransferBackend : IDisposable
{
    bool IsOpen { get; }

    bool ControlOut(byte request, ushort wValue, byte[]? data);

    bool ControlIn(byte request, byte[] buffer, ushort wValue = 0, ushort wIndex = 0);

    bool BulkWrite(byte pipeId, byte[] data, out int transferred);
}
