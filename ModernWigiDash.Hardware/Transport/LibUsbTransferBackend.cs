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
    private const int BulkChunkSize = 262144;

    private readonly IUsbDevice _device;
    private readonly UsbEndpointWriter _writer;
    private int _bulkDiagCount;

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

        int totalBytes = data.Length;
        int numChunks = (totalBytes + BulkChunkSize - 1) / BulkChunkSize;
        if (_bulkDiagCount++ % 60 == 0)
            FileLog.Write($"[USB-BULK-LIBUSB] Chunked write: {totalBytes} bytes in {numChunks} chunks");

        int totalTransferred = 0;

        try
        {
            for (int i = 0; i < numChunks; i++)
            {
                // Advance by the actually-transferred length, not the nominal
                // chunk stride, so a short write doesn't skip a gap.
                int offset = totalTransferred;
                int remaining = totalBytes - offset;
                int size = Math.Min(BulkChunkSize, remaining);

                Error error = _writer.Write(data, offset, size, 10000, out int transferLength);
                if (error != Error.Success || transferLength <= 0)
                {
                    FileLog.Write($"[USB-BULK-ERR] Chunk {i}/{numChunks} failed: error={error} transferred={transferLength}");
                    return false;
                }

                totalTransferred += transferLength;
            }

            transferred = totalTransferred;
            return totalTransferred == totalBytes;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[USB-BULK-ERR] Chunked write exception: {ex.Message}");
            return false;
        }
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
