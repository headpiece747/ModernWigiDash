using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// <see cref="ITransferBackend"/> adapter over LibUsbDotNet: vendor control
/// transfers through the device interface and chunked bulk writes through the
/// endpoint writer. The chunked-write policy lives here — the legacy libusb
/// driver stalls on multi-megabyte single transfers (10s timeout for partial
/// data), so payloads are always written in bounded chunks sized for the
/// driver's throughput. This leg also owns its acquisition
/// (<see cref="TryOpen"/>, mirroring <see cref="WinUsbBulkDevice.Open"/>):
/// find → open → configure → claim → endpoint discovery — the partial-state
/// teardown moves with the adapter, so the transport adopts only a fully
/// configured backend.
/// </summary>
internal sealed class LibUsbTransferBackend : ITransferBackend
{
    private readonly IUsbDevice _device;
    private readonly UsbEndpointWriter _writer;
    private const string BulkDiagCategory = "USB-BULK-LIBUSB";
    private const string CtrlDiagCategory = "USB-CTRL";
    private const string BulkErrorDiagCategory = "USB-BULK-ERR";
    private const string DisposeDiagCategory = "USB-DISPOSE";
    private readonly DiagLog _bulkDiagLog = new(BulkDiagCategory, BackendDiag.BulkWriteCadence);
    // The other vocabulary categories bind once at construction (Cadence 1 —
    // each is a failure path whose lines must always fire), so a tag can't
    // drift between this backend's call sites.
    private readonly DiagLog _ctrlDiagLog = new(CtrlDiagCategory, 1);
    private readonly DiagLog _bulkErrorDiagLog = new(BulkErrorDiagCategory, 1);
    private readonly DiagLog _disposeDiagLog = new(DisposeDiagCategory, 1);

    // The leg's acquisition vocabulary: one bound log per connect step
    // (Cadence 1 — each fires when it happens), the same tags the transport
    // used to bind at the leg's top — one vocabulary across the seam.
    private static readonly DiagLog _findLog = new("USB-FIND", 1);
    private static readonly DiagLog _openLog = new("USB-OPEN", 1);
    private static readonly DiagLog _configLog = new("USB-CONFIG", 1);
    private static readonly DiagLog _claimLog = new("USB-CLAIM", 1);
    private static readonly DiagLog _endpointLog = new("USB-ENDPOINT", 1);
    private static readonly DiagLog _descLog = new("USB-DESC", 1);
    private static readonly DiagLog _legLog = new("USB-LIBUSB", 1);

    private static readonly Lazy<UsbContext> SharedContext = new(() => new UsbContext());

    private LibUsbTransferBackend(IUsbDevice device, UsbEndpointWriter writer)
    {
        _device = device;
        _writer = writer;
    }

    /// <summary>
    /// The LibUsb leg's acquisition (mirrors <see cref="WinUsbBulkDevice.Open"/>):
    /// find → open → configure → claim → endpoint discovery → the configured
    /// backend, or null when the leg cannot provide one.
    /// <paramref name="deviceProvider"/> is the device-lookup seam — null takes
    /// the production default (shared-context VID/PID find). All partial-state
    /// teardown is owned here: a failed acquisition closes the LOCAL device —
    /// no transfer can be in flight on a device the transport never adopted,
    /// so the transport's never-free-while-in-flight lock stays reserved for
    /// the adopted backend.
    /// </summary>
    public static LibUsbTransferBackend? TryOpen(Func<IUsbDevice?>? deviceProvider)
    {
        IUsbDevice? device = null;
        try
        {
            device = deviceProvider is not null ? deviceProvider() : FindLibUsbDevice();

            if (device is null)
            {
                _findLog.Write($"No WigiDash device found (VID=0x{DisplayProtocolConstants.VendorId:X4}, PID=0x{DisplayProtocolConstants.ProductId:X4})");
                return null;
            }

            _findLog.Write($"Device found: VID=0x{device.VendorId:X4} PID=0x{device.ProductId:X4}");

            try
            {
                var openSw = System.Diagnostics.Stopwatch.StartNew();
                device.Open();
                openSw.Stop();
                _openLog.Write($"device.Open() succeeded ({openSw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                _openLog.Write($"device.Open() THREW: {ex.GetType().FullName}: {ex.Message}");
                throw;
            }

            try
            {
                device.SetConfiguration(1);
                _configLog.Write("SetConfiguration(1) succeeded");
            }
            catch (Exception ex)
            {
                _configLog.Write($"SetConfiguration(1) failed: {ex.Message} (continuing)");
            }

            bool claimed = device.ClaimInterface(0);
            if (!claimed)
            {
                _claimLog.Write("Failed to claim USB interface 0");
                device.Close();
                return null;
            }

            _claimLog.Write("ClaimInterface(0) succeeded");

            WriteEndpointID endpointId = DiscoverBulkOutEndpoint(device);
            _endpointLog.Write($"Using bulk OUT endpoint: {endpointId}");

            var backend = new LibUsbTransferBackend(device, device.OpenEndpointWriter(endpointId, EndpointType.Bulk));
            _legLog.Write($"Connected: endpoint={endpointId}");
            return backend;
        }
        catch (Exception ex)
        {
            _legLog.Write($"Connect exception: {ex.GetType().FullName}: {ex.Message}");
            // Terminal teardown of the LOCAL device: an opened (and possibly
            // configured + claimed) device must be released, or it leaks until
            // process exit. The device is local to this attempt — the transport
            // never adopted it — so no transport lock is needed; the
            // never-free-while-in-flight rule applies to the adopted backend,
            // which the transport tears down under its lock.
            if (device is not null)
            {
                device.Close();
            }
            return null;
        }
    }

    /// <summary>Finds the WigiDash device via the shared LibUsbDotNet context
    /// (the production default behind the leg's device-lookup seam).</summary>
    private static IUsbDevice? FindLibUsbDevice()
    {
        var context = SharedContext.Value;
        var finder = new UsbDeviceFinder
        {
            Vid = DisplayProtocolConstants.VendorId,
            Pid = DisplayProtocolConstants.ProductId
        };
        return context.Find(finder);
    }

    /// <summary>
    /// Discovers the bulk OUT endpoint from the device descriptor.
    /// Falls back to endpoint 1 (BulkOutPipeId) if discovery fails.
    /// </summary>
    private static WriteEndpointID DiscoverBulkOutEndpoint(IUsbDevice device)
    {
        try
        {
            var info = device.Info;
            if (info.Configurations.Count > 0)
            {
                var config = info.Configurations[0];
                if (config.Interfaces.Count > 0)
                {
                    var iface = config.Interfaces[0];
                    foreach (byte addr in iface.Endpoints.Select(ep => ep.EndpointAddress))
                    {
                        // OUT endpoints have direction bit (bit 7) = 0
                        if ((addr & 0x80) == 0)
                        {
                            byte epNum = (byte)(addr & 0x0F);
                            _descLog.Write($"Found OUT endpoint: 0x{addr:X2} (ep{epNum})");
                            return (WriteEndpointID)epNum;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _descLog.Write($"Descriptor scan failed: {ex.Message}");
        }

        _descLog.Write($"Using fallback endpoint: {DisplayProtocolConstants.BulkOutPipeId}");
        return (WriteEndpointID)DisplayProtocolConstants.BulkOutPipeId;
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
            _ctrlDiagLog.Write($"ControlOut 0x{request:X2} failed: {ex.Message}");
            return false;
        }
    }

    public bool ControlIn(byte request, byte[] buffer, out int transferred, ushort wValue = 0, ushort wIndex = 0)
    {
        transferred = 0;
        try
        {
            var setup = new UsbSetupPacket(
                DisplayProtocolConstants.ControlInRequestType,
                request,
                wValue,
                wIndex,
                buffer.Length);

            transferred = _device.ControlTransfer(setup, buffer, 0, buffer.Length);
            return transferred > 0;
        }
        catch (Exception ex)
        {
            _ctrlDiagLog.Write($"ControlIn 0x{request:X2} failed: {ex.Message}");
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
                msg => _bulkErrorDiagLog.Write(msg));
            return ok;
        }
        catch (Exception ex)
        {
            _bulkErrorDiagLog.Write($"Chunked write exception: {ex.Message}");
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
        catch (Exception ex)
        {
            // USB device may already be disconnected
            _disposeDiagLog.Write($"LibUsb backend dispose failed: {ex.Message}");
        }
    }
}
