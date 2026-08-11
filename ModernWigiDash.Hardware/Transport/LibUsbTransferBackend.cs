using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// <see cref="ITransferBackend"/> adapter over LibUsbDotNet: vendor control
/// transfers through the device interface and chunked bulk writes through the
/// endpoint writer. The chunked-write policy lives here — the legacy libusb
/// driver stalls on multi-megabyte single transfers (10s timeout for partial
/// data), so payloads are always written in bounded chunks sized for the
/// driver's throughput.
/// </summary>
internal sealed class LibUsbTransferBackend : ITransferBackend
{
    private readonly IUsbDevice _device;
    private readonly UsbEndpointWriter _writer;
    private const string BulkDiagCategory = "USB-BULK-LIBUSB";
    private readonly DiagLog _bulkDiagLog = new(BulkDiagCategory, 30); // matches the WinUSB backend's diag cadence

    public LibUsbTransferBackend(IUsbDevice device, UsbEndpointWriter writer)
    {
        _device = device;
        _writer = writer;
    }

    public bool IsOpen => _device.IsOpen;

    public bool ControlOut(byte request, ushort wValue, byte[]? data)
    {
        try
        {
            int length = data?.Length ?? 0;
            var setup = new UsbSetupPacket(
                DisplayProtocolConstants.VendorOutRequestType,
                request,
                wValue,
                0,
                length);

            int transferred = data == null || data.Length == 0
                ? _device.ControlTransfer(setup)
                : _device.ControlTransfer(setup, data, 0, data.Length);

            return transferred >= 0;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[USB-CTRL] ControlOut 0x{request:X2} failed: {ex.Message}");
            return false;
        }
    }

    public bool ControlIn(byte request, byte[] buffer, ushort wValue = 0, ushort wIndex = 0)
    {
        try
        {
            var setup = new UsbSetupPacket(
                DisplayProtocolConstants.ControlInRequestType,
                request,
                wValue,
                wIndex,
                buffer.Length);

            int transferred = _device.ControlTransfer(setup, buffer, 0, buffer.Length);
            return transferred > 0;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[USB-CTRL] ControlIn 0x{request:X2} failed: {ex.Message}");
            return false;
        }
    }

    public bool BulkWrite(byte pipeId, byte[] data, out int transferred)
    {
        transferred = 0;

        int numChunks = (data.Length + ChunkedBulkWrite.ChunkSize - 1) / ChunkedBulkWrite.ChunkSize;
        _bulkDiagLog.Write(() => $"Chunked write: {data.Length} bytes in {numChunks} chunks");

        try
        {
            bool ok = ChunkedBulkWrite.Write(
                data,
                (offset, size) =>
                {
                    Error error = WriteChunk(data, offset, size, out int transferLength);
                    return error == Error.Success
                        ? (true, transferLength, string.Empty)
                        : (false, transferLength, error.ToString());
                },
                out transferred,
                msg => FileLog.Write($"[USB-BULK-ERR] {msg}"));
            return ok;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[USB-BULK-ERR] Chunked write exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Writes one chunk through the endpoint writer. Returns the raw
    /// <see cref="Error"/>; the caller materializes the failure detail string
    /// only on the failure branch — the success-path Error.ToString() was a
    /// per-chunk allocation on the frame path.
    /// </summary>
    private Error WriteChunk(byte[] data, int offset, int size, out int transferLength)
    {
        transferLength = 0;
        return _writer.Write(data, offset, size, ChunkedBulkWrite.ChunkTimeoutMs, out transferLength);
    }

    public void Dispose()
    {
        if (!_device.IsOpen) return;

        try
        {
            _device.ReleaseInterface(0);
            _device.Close();
        }
        catch
        {
            // USB device may already be disconnected
        }
    }
}
