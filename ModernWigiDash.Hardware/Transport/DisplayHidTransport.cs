// <copyright file="DisplayHidTransport.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// LibUsbDotNet 3.0.224 transport for the WigiDash display device.
/// Uses standard control and bulk USB transfer patterns:
///   ControlOut(0x21, request, wValue, 0, data) for control OUT
///   iface.OutPipe.Write(buffer) for bulk OUT
///   ControlIn(0xA1, request, wValue, 0, buffer) for control IN
/// </summary>
public sealed partial class DisplayHidTransport(ILogger<DisplayHidTransport>? logger = null) : IDisplayTransport
{
    private readonly ILogger<DisplayHidTransport> _logger = logger ?? NullLogger<DisplayHidTransport>.Instance;

    private IUsbDevice? _usbDevice;
    private UsbEndpointWriter? _bulkWriter;
    private WinUsbBulkDevice? _winUsbBulk; // Direct WinUSB for bulk writes (fallback)
    private static readonly Lazy<UsbContext> SharedContext = new(() => new UsbContext());

    private volatile bool _isConnected;
    private int _isDisposed;
    private long _framesSent;
    private long _framesFailed;
    private readonly Lock _usbLock = new();

    // 3-page double-buffering (Base0=0x20, Base1=0x21, Base2=0x22)
    private const int NumPages = 3;
    private const byte Base0 = 0x20;
    private int _currentPage;

    public bool IsConnected => _isConnected;
    public string DevicePath
    {
        get
        {
            if (!_isConnected) return "Disconnected";
            if (_winUsbBulk != null && _winUsbBulk.IsOpen)
            {
                return !string.IsNullOrEmpty(_winUsbBulk.DevicePath)
                    ? _winUsbBulk.DevicePath
                    : $"WinUSB {DisplayProtocolConstants.VendorId:X4}:{DisplayProtocolConstants.ProductId:X4}";
            }
            return $"LibUsbDotNet {DisplayProtocolConstants.VendorId:X4}:{DisplayProtocolConstants.ProductId:X4}";
        }
    }
    public long FramesSent => Volatile.Read(ref _framesSent);
    public long FramesFailed => Volatile.Read(ref _framesFailed);
    public int CurrentPage => _currentPage;

    public bool Connect()
    {
        if (_isConnected)
            return true;

        _logger.LogInformation("Connecting to WigiDash...");

        // STRATEGY: Try WinUSB first, fall back to LibUsbDotNet.
        // WinUSB and LibUsbDotNet cannot share the same USB interface, so we pick one.

        // --- Try WinUSB first ---
        try
        {
            _winUsbBulk = new WinUsbBulkDevice();
            if (_winUsbBulk.Open(DisplayProtocolConstants.WinUsbInterfaceGuid))
            {
                LogToFile("[USB-WINUSB] Direct WinUSB connection opened");
                _isConnected = true;

                // Verify with PING
                byte[] pingBuf = new byte[4];
                bool pingOk = _winUsbBulk.ControlIn(0x00, pingBuf);
                LogToFile($"[USB-WINUSB] PING: ok={pingOk}");

                if (pingOk)
                {
                    LogToFile("[USB-WINUSB] Using WinUSB for all transfers (control + bulk)");
                    _logger.LogInformation("Connected to WigiDash via WinUSB");
                    bool initOk = SendInitCommands();
                    if (!initOk)
                    {
                        LogToFile("[USB-WINUSB] Init commands failed — treating connection as failed");
                        Cleanup();
                    }
                    return initOk;
                }

                // PING failed, close WinUSB and try LibUsbDotNet
                LogToFile("[USB-WINUSB] PING failed, falling back to LibUsbDotNet");
                _winUsbBulk.Dispose();
                _winUsbBulk = null;
                _isConnected = false;
            }
            else
            {
                LogToFile("[USB-WINUSB] Failed to open WinUSB, falling back to LibUsbDotNet");
                _winUsbBulk.Dispose();
                _winUsbBulk = null;
            }
        }
        catch (Exception ex)
        {
            LogToFile($"[USB-WINUSB] Exception: {ex.Message}, falling back to LibUsbDotNet");
            _winUsbBulk?.Dispose();
            _winUsbBulk = null;
            _isConnected = false;
        }

        // --- Fallback to LibUsbDotNet ---
        _logger.LogInformation("Connecting to WigiDash via LibUsbDotNet 3.0 (fallback)...");

        try
        {
            var context = SharedContext.Value;
            var finder = new UsbDeviceFinder
            {
                Vid = DisplayProtocolConstants.VendorId,
                Pid = DisplayProtocolConstants.ProductId
            };

            IUsbDevice? device = context.Find(finder);

            if (device is null)
            {
                _logger.LogWarning("No WigiDash device found (VID=0x{VID:X4}, PID=0x{PID:X4})",
                    DisplayProtocolConstants.VendorId, DisplayProtocolConstants.ProductId);
                _isConnected = false;
                return false;
            }

            LogToFile($"[USB-FIND] Device found: VID=0x{device.VendorId:X4} PID=0x{device.ProductId:X4}");

            try
            {
                var openSw = System.Diagnostics.Stopwatch.StartNew();
                device.Open();
                openSw.Stop();
                LogToFile($"[USB-OPEN] device.Open() succeeded ({openSw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                LogToFile($"[USB-OPEN] device.Open() THREW: {ex.GetType().FullName}: {ex.Message}");
                throw;
            }

            try
            {
                device.SetConfiguration(1);
                LogToFile("[USB-CONFIG] SetConfiguration(1) succeeded");
            }
            catch (Exception ex)
            {
                LogToFile($"[USB-CONFIG] SetConfiguration(1) failed: {ex.Message} (continuing)");
            }

            bool claimed = device.ClaimInterface(0);
            if (!claimed)
            {
                _logger.LogError("Failed to claim USB interface 0");
                device.Close();
                _isConnected = false;
                return false;
            }

            LogToFile("[USB-CLAIM] ClaimInterface(0) succeeded");

            WriteEndpointID endpointId = DiscoverBulkOutEndpoint(device);
            LogToFile($"[USB-ENDPOINT] Using bulk OUT endpoint: {endpointId}");

            _bulkWriter = device.OpenEndpointWriter(endpointId, EndpointType.Bulk);
            _usbDevice = device;
            _isConnected = true;

            LogToFile($"[USB-LIBUSB] Connected: endpoint={endpointId}");

            bool initOk = SendInitCommands();
            if (!initOk)
            {
                LogToFile("[USB-LIBUSB] Init commands failed — treating connection as failed");
                Cleanup();
                return false;
            }

            _logger.LogInformation("Connected to WigiDash via LibUsbDotNet 3.0");
            return true;
        }
        catch (Exception ex)
        {
            LogToFile($"[USB-LIBUSB] Connect exception: {ex.GetType().FullName}: {ex.Message}");
            _logger.LogError(ex, "Failed to connect to WigiDash");
            Cleanup();
            return false;
        }
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
                            LogToFile($"[USB-DESC] Found OUT endpoint: 0x{addr:X2} (ep{epNum})");
                            return (WriteEndpointID)epNum;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogToFile($"[USB-DESC] Descriptor scan failed: {ex.Message}");
        }

        // Fallback to known endpoint from protocol constants
        LogToFile($"[USB-DESC] Using fallback endpoint: {DisplayProtocolConstants.BulkOutPipeId}");
        return (WriteEndpointID)DisplayProtocolConstants.BulkOutPipeId;
    }

    public bool SendInitCommands()
    {
        _logger.LogInformation("Sending device initialization commands...");

        // PING command (CMD_PING = 0x00, Control IN)
        byte[] pingBuf = new byte[4];
        bool pingOk = ControlIn(0x00, 0, 0, pingBuf);
        LogToFile($"[USB-INIT] PING: ok={pingOk}");

        // Set brightness to 100%
        ControlOut(DisplayProtocolConstants.CmdSetBrightness, 0, [100]);

        // Initialize all 3 pages (3-page double-buffering)
        // Each page gets: ClearPage → AddWidget(full-screen) → blank framebuffer
        bool initOk = true;
        for (int page = 0; page < NumPages; page++)
        {
            // ClearPage: CMD_SCREENCFG_CLEAR (0x90) wValue=page
            bool clearOk = ControlOut(DisplayProtocolConstants.CmdClearPage, (ushort)page, null);

            // AddWidget: CMD_SCREENCFG_WIDGET_ADD (0x91) wValue = (page << 8) | widgetId
            // Registers a full-screen widget (1016x592) at (0,0)
            byte[] widgetConfig = BuildWidgetConfig(
                x: 0, y: 0,
                width: DisplayProtocolConstants.FramebufferWidth,
                height: DisplayProtocolConstants.FramebufferHeight);
            bool widgetOk = ControlOut(DisplayProtocolConstants.CmdAddWidget, (ushort)((page << 8) | 0), widgetConfig);
            LogToFile($"[USB-INIT] Page {page}: ClearPage + AddWidget(0,0) sent ({widgetConfig.Length} bytes), ok={clearOk && widgetOk}");
            initOk &= clearOk && widgetOk;
        }

        // Write blank framebuffer to page 0 only (first visible page)
        WriteBlankFramebuffer(page: 0, widgetId: 0);

        // GoToScreen(Base0): CMD_SEND_UI_CMD (0x70) wValue=0x20
        bool gotoOk = ControlOut(DisplayProtocolConstants.CmdGoToScreen, Base0, null);
        LogToFile($"[USB-INIT] GoToScreen(Base0) sent — all 3 pages initialized, ok={gotoOk}");

        _currentPage = 0;
        initOk &= gotoOk;
        _logger.LogInformation("Device initialization complete (3 pages), ok={InitOk}", initOk);
        return initOk;
    }

    /// <summary>
    /// Builds the 20-byte WidgetConfig struct for the display protocol.
    /// StructLayout(Pack=4): short X(2), short Y(2), short Width(2), short Height(2),
    ///   ushort BaseClr(2), pad(2), uint DrawAddr(4), byte DrawLock(1), byte InvalidateFlag(1),
    ///   byte UpdateFromCache(1), pad(1) = 20 bytes total.
    /// </summary>
    internal static byte[] BuildWidgetConfig(short x, short y, short width, short height)
    {
        byte[] config = new byte[20];
        BitConverter.GetBytes(x).CopyTo(config, 0);
        BitConverter.GetBytes(y).CopyTo(config, 2);
        BitConverter.GetBytes(width).CopyTo(config, 4);
        BitConverter.GetBytes(height).CopyTo(config, 6);
        // BaseClr at offset 8 = 0 (ushort)
        // Padding at offset 10 (2 bytes)
        // DrawAddr at offset 12 = 0 (uint)
        // DrawLock at offset 16 = 0 (byte)
        // InvalidateFlag at offset 17 = 0 (byte)
        // UpdateFromCache at offset 18 = 0 (byte)
        // Padding at offset 19 (1 byte)
        return config;
    }

    private void WriteBlankFramebuffer(byte page, byte widgetId)
    {
        if (_usbDevice == null) return;

        try
        {
            byte[] blankFrame = new byte[DisplayProtocolConstants.FrameBufferSize];
            LogToFile($"[HW-INIT] Writing blank framebuffer ({blankFrame.Length} bytes) to page={page} widget={widgetId}");

            // Control transfer header: offset=0, length=FrameBufferSize
            byte[] header = new byte[DisplayProtocolConstants.FrameHeaderDataSize];
            BitConverter.GetBytes((uint)0).CopyTo(header, 0);
            BitConverter.GetBytes((uint)blankFrame.Length).CopyTo(header, 4);

            ushort wValue = (ushort)((page << 8) | widgetId);
            bool headerOk = ControlOut(DisplayProtocolConstants.CmdFrameHeader, wValue, header);
            LogToFile($"[HW-INIT] FrameHeader control write: ok={headerOk}");

            if (headerOk)
            {
                bool bulkOk = WriteBulkData(blankFrame);
                LogToFile($"[HW-INIT] Blank framebuffer bulk write: ok={bulkOk}");
            }
        }
        catch (Exception ex)
        {
            LogToFile($"[HW-INIT] Blank framebuffer write exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Vendor OUT control transfer.
    /// Routes through WinUSB (primary) or LibUsbDotNet (fallback).
    /// bmRequestType = 0x21 (Vendor | Host-to-Device | Interface)
    /// </summary>
    private bool ControlOut(byte request, ushort wValue, byte[]? data)
    {
        // Try WinUSB first
        if (_winUsbBulk != null && _winUsbBulk.IsOpen)
        {
            return _winUsbBulk.ControlOut(request, wValue, data);
        }

        if (_usbDevice == null) return false;

        try
        {
            int length = data?.Length ?? 0;
            var setup = new UsbSetupPacket(
                DisplayProtocolConstants.VendorOutRequestType,
                request,
                wValue,
                0,
                length);

            int transferred;
            if (data == null || data.Length == 0)
            {
                transferred = _usbDevice.ControlTransfer(setup);
            }
            else
            {
                transferred = _usbDevice.ControlTransfer(setup, data, 0, data.Length);
            }

            return transferred >= 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ControlOut 0x{Request:X2} failed", request);
            return false;
        }
    }

    /// <summary>
    /// Vendor IN control transfer.
    /// Routes through WinUSB (primary) or LibUsbDotNet (fallback).
    /// bmRequestType = 0xA1 (Vendor | Device-to-Host | Interface)
    /// </summary>
    private bool ControlIn(byte request, ushort wValue, ushort wIndex, byte[] buffer)
    {
        // Try WinUSB first
        if (_winUsbBulk != null && _winUsbBulk.IsOpen)
        {
            return _winUsbBulk.ControlIn(request, buffer, wValue, wIndex);
        }

        if (_usbDevice == null) return false;

        try
        {
            var setup = new UsbSetupPacket(
                DisplayProtocolConstants.ControlInRequestType,
                request,
                wValue,
                wIndex,
                buffer.Length);

            int transferred = _usbDevice.ControlTransfer(setup, buffer, 0, buffer.Length);
            return transferred > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ControlIn 0x{Request:X2} failed", request);
            return false;
        }
    }

    /// <summary>
    /// Writes bulk data using direct WinUSB (primary) or LibUsbDotNet (fallback).
    /// Direct WinUSB WinUsb_WritePipe is used because LibUsbDotNet's bulk write
    /// has issues with large transfers on the WinUSB backend.
    /// </summary>
    private bool WriteBulkData(byte[] data)
    {
        // Try direct WinUSB first
        if (_winUsbBulk != null && _winUsbBulk.IsOpen)
        {
            try
            {
                bool ok = _winUsbBulk.BulkWrite(DisplayProtocolConstants.BulkOutPipeId, data, out int transferred);
                if (ok && transferred == data.Length)
                {
                    return true;
                }

                LogToFile($"[USB-WINUSB-BULK] WinUSB write returned ok={ok} transferred={transferred}/{data.Length}, error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex)
            {
                LogToFile($"[USB-WINUSB-BULK] WinUSB write exception: {ex.Message}");
            }
        }

        // Fallback to LibUsbDotNet — try single unchunked write first,
        // fall back to chunked if the device rejects large single transfers.
        if (_bulkWriter == null) return false;

        int totalBytes = data.Length;
        const int chunkSize = 4096;

        try
        {
            // Try single unchunked write first
            Error error = _bulkWriter.Write(data, 0, totalBytes, 10000, out int singleTransferred);
            if (error == Error.Success && singleTransferred == totalBytes)
            {
                return true;
            }

            LogToFile($"[USB-BULK-LIBUSB] Single write returned error={error} transferred={singleTransferred}/{totalBytes}, falling back to chunked");
        }
        catch (Exception ex)
        {
            LogToFile($"[USB-BULK-LIBUSB] Single write exception: {ex.Message}, falling back to chunked");
        }

        // Chunked fallback
        int numChunks = (totalBytes + chunkSize - 1) / chunkSize;
        LogToFile($"[USB-BULK-LIBUSB] Chunked write: {totalBytes} bytes in {numChunks} chunks");

        int totalTransferred = 0;

        try
        {
            for (int i = 0; i < numChunks; i++)
            {
                int offset = i * chunkSize;
                int remaining = totalBytes - offset;
                int size = Math.Min(chunkSize, remaining);

                Error error = _bulkWriter.Write(data, offset, size, 10000, out int transferLength);
                if (error != Error.Success)
                {
                    LogToFile($"[USB-BULK-ERR] Chunk {i}/{numChunks} failed: error={error} transferred={transferLength}");
                    return false;
                }

                totalTransferred += transferLength;
            }

            return totalTransferred == totalBytes;
        }
        catch (Exception ex)
        {
            LogToFile($"[USB-BULK-ERR] Chunked write exception: {ex.Message}");
            return false;
        }
    }

    private int _touchDiagCount;

    public TouchReport? ReadTouch()
    {
        if (!_isConnected || _usbDevice == null)
        {
            if (_touchDiagCount++ % 20 == 0)
                LogToFile($"[TOUCH-DIAG] Not connected: isConnected={_isConnected}");
            return null;
        }

        try
        {
            byte[] touchBuf = new byte[DisplayProtocolConstants.TouchReportSize];

            bool ok = ControlIn(DisplayProtocolConstants.CmdGetTouch, 0, 0, touchBuf);

            if (!ok)
            {
                if (_touchDiagCount++ % 20 == 0)
                    LogToFile($"[TOUCH-DIAG] ControlIn FAILED");
                return null;
            }

            byte type = touchBuf[0];
            short x = BitConverter.ToInt16(touchBuf, 2);
            short y = BitConverter.ToInt16(touchBuf, 4);

            if (_touchDiagCount++ % 200 == 0)
                LogToFile($"[TOUCH-DIAG] Raw: type={type} x={x} y={y}");

            if (type == DisplayProtocolConstants.TouchTypeNone)
                return null;

            if (x < 0 || x >= DisplayProtocolConstants.FramebufferWidth ||
                y < 0 || y >= DisplayProtocolConstants.FramebufferHeight)
                return null;

            byte screenState = touchBuf[6];
            byte sleepState = touchBuf[7];

            return new TouchReport
            {
                Type = type,
                X = x,
                Y = y,
                ScreenState = screenState,
                SleepState = sleepState != 0
            };
        }
        catch (Exception ex)
        {
            LogToFile($"[TOUCH-DIAG] Exception: {ex.Message}");
            return null;
        }
    }

    public void Disconnect()
    {
        Cleanup();
    }

    private void Cleanup()
    {
        _isConnected = false;

        // Dispose WinUSB bulk device
        _winUsbBulk?.Dispose();
        _winUsbBulk = null;

        _bulkWriter = null;

        if (_usbDevice != null)
            {
                try
                {
                    if (_usbDevice.IsOpen)
                    {
                        _usbDevice.ReleaseInterface(0);
                        _usbDevice.Close();
                    }
                }
                catch (IOException)
                {
                    // USB device may already be disconnected
                    System.Diagnostics.Debug.WriteLine("USB device release failed; device may already be disconnected");
                }
                _usbDevice = null;
            }

            // Context is a shared singleton - don't dispose
        }

    public bool SendFrame(ReadOnlySpan<byte> frameBuffer)
    {
        if (!_isConnected)
        {
            _logger.LogWarning("SendFrame SKIPPED: not connected");
            return false;
        }

        if (frameBuffer.Length < DisplayProtocolConstants.FrameBufferSize)
        {
            _logger.LogWarning("Frame buffer too small: {Len} < {Req}",
                frameBuffer.Length, DisplayProtocolConstants.FrameBufferSize);
            return false;
        }

        try
        {
            byte[] frameArray = frameBuffer.ToArray();

            lock (_usbLock)
            {
                // WriteToWidget(currentPage, widgetId=0, offset=0, data)
                // CMD_SDRAM_WIDGET_WRITE (0x61), wValue = (page << 8) | widgetId
                // Writes directly to the currently displayed page.
                int page = _currentPage;

                byte[] header = new byte[DisplayProtocolConstants.FrameHeaderDataSize];
                BitConverter.GetBytes((uint)0).CopyTo(header, 0);
                BitConverter.GetBytes((uint)frameArray.Length).CopyTo(header, 4);

                ushort wValue = (ushort)((page << 8) | 0);
                if (!ControlOut(DisplayProtocolConstants.CmdFrameHeader, wValue, header))
                {
                    Interlocked.Increment(ref _framesFailed);
                    return false;
                }

                if (!WriteBulkData(frameArray))
                {
                    ControlOut(DisplayProtocolConstants.CmdFrameAbort, 0, null);
                    Interlocked.Increment(ref _framesFailed);
                    return false;
                }
            }

            Interlocked.Increment(ref _framesSent);
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _framesFailed);
            _logger.LogError(ex, "SendFrame FAILED (total failed: {Failed})", FramesFailed);
            return false;
        }
    }

    public bool SetBrightness(byte brightnessPercent)
    {
        if (!_isConnected)
            return false;

        byte clamped = (byte)Math.Clamp((int)brightnessPercent, 0, 100);
        byte[] brightnessBuf = [clamped];
        bool ok = ControlOut(DisplayProtocolConstants.CmdSetBrightness, 0, brightnessBuf);
        if (ok) _logger.LogInformation("SetBrightness to {Brightness}%", clamped);
        return ok;
    }

    public bool GoToScreen(byte screenId, byte transition = 0)
    {
        if (!_isConnected)
            return false;

        ushort wValue = (ushort)((transition << 2) | screenId);
        bool ok = ControlOut(DisplayProtocolConstants.CmdGoToScreen, wValue, null);
        if (ok)
        {
            _logger.LogInformation("GoToScreen 0x{ScreenId:X2} (wValue=0x{WValue:X4})", screenId, wValue);
            // Update current page if switching to a Base screen
            if (screenId >= 0x20 && screenId <= 0x22)
                _currentPage = screenId - 0x20;
        }
        return ok;
    }

    public bool ClearPage(byte page = 0)
    {
        if (!_isConnected)
            return false;

        bool ok = ControlOut(DisplayProtocolConstants.CmdClearPage, page, null);
        if (ok) _logger.LogInformation("ClearPage {Page}", page);
        return ok;
    }

    public bool ClearTimeout()
    {
        if (!_isConnected)
            return false;

        bool ok = ControlOut(DisplayProtocolConstants.CmdClearTimeout, 0, null);
        if (ok) _logger.LogInformation("ClearTimeout");
        return ok;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0) return;
        Cleanup();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0) return ValueTask.CompletedTask;
        Cleanup();
        return ValueTask.CompletedTask;
    }

    private static void LogToFile(string msg) => FileLog.Write(msg);
}
