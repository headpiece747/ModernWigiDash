// <copyright file="DisplayHidTransport.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModernWigiDash.Sdk;
using System.Runtime.InteropServices;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// LibUsbDotNet 3.0.224 transport for the WigiDash display device.
/// Uses standard control and bulk USB transfer patterns:
///   ControlOut(0x21, request, wValue, 0, data) for control OUT
///   iface.OutPipe.Write(buffer) for bulk OUT
///   ControlIn(0xA1, request, wValue, 0, buffer) for control IN
/// The backend (WinUSB or LibUsbDotNet) is chosen once in
/// <see cref="Connect"/> and everything else talks to the
/// <see cref="ITransferBackend"/> seam. Connect iterates the
/// <see cref="ProviderFactories"/> list (WinUSB first, LibUsbDotNet fallback):
/// each provider opens the device and returns a backend (or null), and the
/// first backend that survives the init sequence is adopted. The WinUSB
/// provider constructs a real <see cref="WinUsbBulkDevice"/>; tests inject
/// fake providers — including a fake WinUSB leg — through the list, so the
/// connect policy — open, init, fallback — is drivable without hardware.
/// </summary>
internal sealed class DisplayHidTransport : IDisplayTransport
{
    private readonly ILogger<DisplayHidTransport> _logger;

    private ITransferBackend? _backend;
    private static readonly Lazy<UsbContext> SharedContext = new(() => new UsbContext());

    private volatile bool _isConnected;
    private int _isDisposed;
    private long _framesFailed;
    private readonly Lock _usbLock = new();

    // 3-page initialization (Base screens 0x20..0x22 — ScreenBase0..2 in
    // DisplayProtocolConstants; only Base0 is const'ed here). The app frames
    // are always written to page 0; page navigation is compositor-side.
    private const int NumPages = 3;
    private const byte Base0 = DisplayProtocolConstants.ScreenBase0;

    public bool IsConnected => _isConnected;
    public long FramesFailed => Volatile.Read(ref _framesFailed);

    public DisplayHidTransport(ILogger<DisplayHidTransport>? logger = null)
    {
        _logger = logger ?? NullLogger<DisplayHidTransport>.Instance;
        ProviderFactories = [WinUsbProvider, LibUsbProvider];
    }

    /// <summary>
    /// The provider list <see cref="Connect"/> iterates: WinUSB first, the
    /// LibUsbDotNet fallback second. Test seam: tests replace the list — with
    /// fake WinUSB and/or LibUsb legs — to drive the connect and fallback
    /// policies deterministically.
    /// </summary>
    internal ConnectProvider[] ProviderFactories { get; set; }

    /// <summary>
    /// Test seam: the LibUsb leg's device lookup. Defaults to the shared-context
    /// find (VID/PID); tests inject a fake <see cref="IUsbDevice"/> so the
    /// open/config/claim/endpoint sequence and its teardown are drivable
    /// without hardware (the <see cref="WinUsbApi"/> delegate-bag precedent).
    /// </summary>
    internal Func<IUsbDevice?>? LibUsbDeviceProvider { get; set; }

    /// <summary>The real WinUSB leg. Internal so tests can keep it in a
    /// fake provider list (e.g. <c>[transport.WinUsbProvider, fakeLibUsb]</c>).</summary>
    internal ConnectProvider WinUsbProvider => new(
        "USB-WINUSB",
        TryCreateWinUsbBackend,
        "WinUSB",
        "[USB-WINUSB] Using WinUSB for all transfers (control + bulk)",
        "[USB-WINUSB] Init commands failed — falling back to LibUsbDotNet");

    /// <summary>The real LibUsbDotNet leg. Internal so tests can keep it in a
    /// fake provider list.</summary>
    internal ConnectProvider LibUsbProvider => new(
        "USB-LIBUSB",
        TryCreateLibUsbBackend,
        "LibUsbDotNet 3.0",
        null,
        "[USB-LIBUSB] Init commands failed — treating connection as failed");

    /// <summary>
    /// Test seam: constructs the transport bound to an injected backend, so the
    /// connect/init/frame/touch policy is drivable without hardware.
    /// </summary>
    internal DisplayHidTransport(ITransferBackend backend, ILogger<DisplayHidTransport>? logger = null)
        : this(logger)
    {
        _backend = backend;
        _isConnected = backend.IsOpen;
    }

    public bool Connect()
    {
        if (_isConnected)
            return true;

        _logger.LogInformation("Connecting to WigiDash...");

        // STRATEGY: try each provider in order — WinUSB first, LibUsbDotNet as
        // the fallback. WinUSB and LibUsbDotNet cannot share the same USB
        // interface, so exactly one backend is adopted: the first that
        // survives the init sequence. Each provider owns its open attempt and
        // partial-state teardown; the loop owns the init gate and the
        // dispose-on-init-failure (under _usbLock like Cleanup: the backend
        // handle must not be freed while a transfer could be in flight).
        foreach (var provider in ProviderFactories)
        {
            ITransferBackend? backend;
            try
            {
                backend = provider.TryCreate();
            }
            catch (Exception ex)
            {
                // The real providers catch their own failures; this only fires
                // for a provider that let an exception escape, and is treated
                // as terminal like the LibUsb leg's connect exception.
                LogToFile($"[{provider.Tag}] Connect exception: {ex.GetType().FullName}: {ex.Message}");
                _logger.LogError(ex, "Failed to connect to WigiDash");
                Cleanup();
                return false;
            }

            if (backend is null)
                continue;

            // Adopt the backend before init: SendInitCommands talks through it.
            _backend = backend;

            if (SendInitCommands())
            {
                // Connected only once init completes — the transport's contract
                // must not report connected while the init sequence is still
                // running (the engine's ConnectionState gate already blocks
                // frame flow until Connect() returns; this keeps the transport's
                // own flag truthful too).
                _isConnected = true;
                if (provider.SuccessFileLog is not null)
                    LogToFile(provider.SuccessFileLog);
                _logger.LogInformation("Connected to WigiDash via {Via}", provider.ConnectedVia);
                return true;
            }

            // Init failed through this stack — the same control sequence may
            // complete through the next provider's driver stack, so try it.
            LogToFile(provider.InitFailureLog);
            lock (_usbLock)
            {
                _backend?.Dispose();
                _backend = null;
            }
            _isConnected = false;
        }

        return false;
    }

    /// <summary>
    /// WinUSB attempt: open a real <see cref="WinUsbBulkDevice"/> → backend.
    /// There is deliberately no PING
    /// here — the only PING lives in <see cref="SendInitCommands"/> (the
    /// pre-PING that used to run in <see cref="Connect"/> duplicated it and had
    /// drifted). Partial-state teardown is owned here: a failed open disposes
    /// the device under <c>_usbLock</c> (same rule as <see cref="Cleanup"/>).
    /// Both failure exits dispose the LOCAL device — never the adopted global
    /// backend, which belongs to a previous provider attempt.
    /// </summary>
    private ITransferBackend? TryCreateWinUsbBackend()
    {
        var winUsb = new WinUsbBulkDevice();
        try
        {
            if (winUsb.Open(DisplayProtocolConstants.WinUsbInterfaceGuid))
            {
                LogToFile("[USB-WINUSB] Direct WinUSB connection opened");
                return winUsb;
            }

            LogToFile("[USB-WINUSB] Failed to open WinUSB, falling back to LibUsbDotNet");
            TearDownWinUsb(winUsb);
            return null;
        }
        catch (Exception ex)
        {
            LogToFile($"[USB-WINUSB] Exception: {ex.Message}, falling back to LibUsbDotNet");
            lock (_usbLock)
            {
                winUsb.Dispose();
                _backend = null;
            }
            return null;
        }
    }

    /// <summary>
    /// LibUsbDotNet attempt: find → open → configure → claim → endpoint →
    /// backend. Partial-state teardown is owned here: every failure path
    /// closes the LOCAL device — the claim-failure path closes it directly,
    /// the terminal catch closes the open/config/claimed device it created.
    /// Never the adopted global backend, which belongs to a previous provider
    /// attempt (the old catch disposed <c>_backend</c> and leaked this device).
    /// </summary>
    private ITransferBackend? TryCreateLibUsbBackend()
    {
        _logger.LogInformation("Connecting to WigiDash via LibUsbDotNet 3.0 (fallback)...");

        IUsbDevice? device = null;
        try
        {
            device = LibUsbDeviceProvider is not null ? LibUsbDeviceProvider() : FindLibUsbDevice();

            if (device is null)
            {
                _logger.LogWarning("No WigiDash device found (VID=0x{VID:X4}, PID=0x{PID:X4})",
                    DisplayProtocolConstants.VendorId, DisplayProtocolConstants.ProductId);
                return null;
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
                return null;
            }

            LogToFile("[USB-CLAIM] ClaimInterface(0) succeeded");

            WriteEndpointID endpointId = DiscoverBulkOutEndpoint(device);
            LogToFile($"[USB-ENDPOINT] Using bulk OUT endpoint: {endpointId}");

            var backend = new LibUsbTransferBackend(device, device.OpenEndpointWriter(endpointId, EndpointType.Bulk));
            LogToFile($"[USB-LIBUSB] Connected: endpoint={endpointId}");
            return backend;
        }
        catch (Exception ex)
        {
            LogToFile($"[USB-LIBUSB] Connect exception: {ex.GetType().FullName}: {ex.Message}");
            _logger.LogError(ex, "Failed to connect to WigiDash");
            // Terminal teardown of the LOCAL device: an opened (and possibly
            // configured + claimed) device must be released — under _usbLock,
            // the same never-free-while-in-flight rule as Cleanup. If the
            // backend was already constructed, its dispose path closes the
            // device; here the construction never completed.
            if (device is not null)
            {
                lock (_usbLock)
                {
                    device.Close();
                }
            }
            return null;
        }
    }

    /// <summary>Finds the WigiDash device via the shared LibUsbDotNet context
    /// (the production default behind <see cref="LibUsbDeviceProvider"/>).</summary>
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

    /// <summary>
    /// Sends the device initialization sequence (PING + 3-page setup + blank
    /// framebuffer + GoToScreen). Called by <see cref="Connect"/>; internal so
    /// tests can drive the sequence through an injected backend.
    /// </summary>
    internal bool SendInitCommands()
    {
        _logger.LogInformation("Sending device initialization commands...");

        // PING command (CMD_PING = 0x00, Control IN)
        byte[] pingBuf = new byte[4];
        bool pingOk = ControlIn(0x00, 0, 0, pingBuf, out _);
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
        if (_backend is not { IsOpen: true }) return;

        try
        {
            byte[] blankFrame = new byte[DisplayProtocolConstants.FrameBufferSize];
            LogToFile($"[HW-INIT] Writing blank framebuffer ({blankFrame.Length} bytes) to page={page} widget={widgetId}");

            // Control transfer header: offset=0, length=FrameBufferSize (the
            // single wire-format owner, shared with the 30 FPS send path).
            byte[] header = new byte[DisplayProtocolConstants.FrameHeaderDataSize];
            BuildFrameHeader(header, blankFrame.Length);

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

    /// <summary>Writes the 8-byte frame-header wire format [offset(4 LE),
    /// length(4 LE)] into <paramref name="dest"/> — the single owner of the
    /// layout, shared by the cold blank-framebuffer path and the 30 FPS send
    /// path (the layout is documented in <see cref="DisplayProtocolConstants"/>;
    /// a protocol change edits one method).</summary>
    internal static void BuildFrameHeader(byte[] dest, int length)
    {
        dest[0] = 0;
        dest[1] = 0;
        dest[2] = 0;
        dest[3] = 0;
        dest[4] = (byte)length;
        dest[5] = (byte)(length >> 8);
        dest[6] = (byte)(length >> 16);
        dest[7] = (byte)(length >> 24);
    }

    /// <summary>
    /// Vendor OUT control transfer through the active backend.
    /// bmRequestType = 0x21 (Class | Interface | Host-to-Device)
    /// </summary>
    private bool ControlOut(byte request, ushort wValue, byte[]? data)
        => _backend?.ControlOut(request, wValue, data) ?? false;

    /// <summary>
    /// Vendor IN control transfer through the active backend.
    /// bmRequestType = 0xA1 (Vendor | Device-to-Host | Interface).
    /// Reports the transferred byte count so callers can require a full report.
    /// </summary>
    private bool ControlIn(byte request, ushort wValue, ushort wIndex, byte[] buffer, out int transferred)
    {
        transferred = 0;
        return _backend?.ControlIn(request, buffer, out transferred, wValue, wIndex) ?? false;
    }

    /// <summary>
    /// Writes bulk data through the active backend. Direct WinUSB writes the
    /// whole payload in one pipe write; the LibUsb adapter chunks for the
    /// legacy driver's throughput.
    /// </summary>
    private bool WriteBulkData(byte[] data)
        => _backend?.BulkWrite(DisplayProtocolConstants.BulkOutPipeId, data, out _) ?? false;

    // Diagnostic cadences: the two touch-diag failure sites share one counter
    // (they are mutually exclusive branches), the Raw dump is every 200th.
    // Note: the raw-dump cadence counts success-path calls only (every 200th
    // successful read) — the old single shared counter made it positional in
    // total ReadTouch calls; steady states are identical.
    private const string TouchDiagCategory = "TOUCH-DIAG";
    private readonly DiagLog _touchDiagLog = new(TouchDiagCategory, 20, logFirst: true);
    private readonly DiagLog _touchDiagRawLog = new(TouchDiagCategory, 200);
    // The send-skipped log rides the ILogger, not FileLog, so it keeps a bare
    // LogCadence instead of a DiagLog.
    private readonly LogCadence _sendFrameSkippedLog = new(60);

    // Reused per-call buffers: SendFrame and ReadTouch are both serialized by
    // _usbLock (the 16ms poll loop and the frame path can interleave), and the
    // two buffers are distinct — so these are never touched concurrently, with
    // no per-call allocation on the hot paths.
    private readonly byte[] _frameHeader = new byte[DisplayProtocolConstants.FrameHeaderDataSize];
    private readonly byte[] _touchBuffer = new byte[DisplayProtocolConstants.TouchReportSize];

    public TouchReport? ReadTouch()
    {
        lock (_usbLock)
        {
            if (_backend is not { IsOpen: true })
            {
                _touchDiagLog.Write($"Not connected: isConnected={_isConnected}");
                return null;
            }

            try
            {
                byte[] touchBuf = _touchBuffer;

                bool ok = ControlIn(DisplayProtocolConstants.CmdGetTouch, 0, 0, touchBuf, out int transferred);

                if (!ok)
                {
                    _touchDiagLog.Write("ControlIn FAILED");
                    return null;
                }

                if (transferred != DisplayProtocolConstants.TouchReportSize)
                {
                    // A short transfer leaves stale bytes past the transferred
                    // count in the reused buffer; only a full report can be
                    // parsed as a touch.
                    _touchDiagLog.Write($"ControlIn short transfer: {transferred}/{DisplayProtocolConstants.TouchReportSize} bytes");
                    return null;
                }

                byte type = touchBuf[0];
                short x = BitConverter.ToInt16(touchBuf, 2);
                short y = BitConverter.ToInt16(touchBuf, 4);

                _touchDiagRawLog.Write(() => $"Raw: type={type} x={x} y={y}");

                if (type == DisplayProtocolConstants.TouchTypeNone)
                    return null;

                if (x < 0 || x >= DisplayProtocolConstants.FramebufferWidth ||
                    y < 0 || y >= DisplayProtocolConstants.FramebufferHeight)
                    return null;

                return new TouchReport
                {
                    Type = type,
                    X = x,
                    Y = y
                };
            }
            catch (Exception ex)
            {
                LogToFile($"[TOUCH-DIAG] Exception: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Tears down a failed WinUSB attempt: frees the device and clears the
    /// backend under <c>_usbLock</c> — the handle must never be freed while a
    /// transfer could be in flight (same rule as <see cref="Cleanup"/>).
    /// </summary>
    private void TearDownWinUsb(WinUsbBulkDevice winUsb)
    {
        lock (_usbLock)
        {
            winUsb.Dispose();
            _backend = null;
        }
    }

    private void Cleanup()
    {
        // Serialized against SendFrame/ReadTouch/GoToStandby: the backend's
        // teardown frees the native handle, which must never happen while a
        // transfer is in flight (the Lock is reentrant, so Dispose/DisposeAsync
        // calling in is safe).
        lock (_usbLock)
        {
            _isConnected = false;

            // The backend owns the device-specific teardown (WinUSB handle free,
            // LibUsb interface release + close); the transport just drops it.
            _backend?.Dispose();
            _backend = null;
        }

        // Context is a shared singleton - don't dispose
    }

    public bool SendFrame(ReadOnlyMemory<byte> frameBuffer)
    {
        if (!_isConnected)
        {
            if (_sendFrameSkippedLog.Due())
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
            // Zero-alloc fast path: the frame pipeline hands us byte[] buffers,
            // so the memory is almost always an array segment already — reuse it
            // instead of copying 1.2 MB per frame. Fall back to a copy only for
            // genuine non-array memory.
            byte[] frameArray;
            if (!MemoryMarshal.TryGetArray(frameBuffer, out ArraySegment<byte> segment) ||
                segment.Offset != 0 ||
                segment.Count != frameBuffer.Length)
            {
                frameArray = frameBuffer.ToArray();
            }
            else
            {
                frameArray = segment.Array!;
            }

            lock (_usbLock)
            {
                // WriteToWidget(page 0, widgetId=0, offset=0, data)
                // CMD_SDRAM_WIDGET_WRITE (0x61), wValue = (0 << 8) | 0.
                // Frames are always written to the initialized Base screen 0 —
                // page navigation is compositor-side, so there is no live page
                // bookkeeping to consult here.

                // Reused header buffer: SendFrame is serialized by _usbLock, so
                // no per-frame allocation on the 30 FPS path. The wire format
                // is owned once by BuildFrameHeader (the cold blank-framebuffer
                // path shares it); the in-place byte writes avoid BitConverter's
                // per-field byte[4] allocations.
                BuildFrameHeader(_frameHeader, frameArray.Length);

                if (!ControlOut(DisplayProtocolConstants.CmdFrameHeader, wValue: 0, _frameHeader))
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

            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _framesFailed);
            _logger.LogError(ex, "SendFrame FAILED (total failed: {Failed})", FramesFailed);
            return false;
        }
    }

    /// <summary>
    /// Switches the display to the specified screen. Private: only the standby
    /// path needs it — page navigation is compositor-side, frames are sent for
    /// Base screen 0 directly.
    /// </summary>
    private bool GoToScreen(byte screenId, byte transition = 0)
    {
        if (!_isConnected)
            return false;

        ushort wValue = (ushort)((transition << 2) | screenId);
        bool ok = ControlOut(DisplayProtocolConstants.CmdGoToScreen, wValue, null);
        if (ok)
        {
            _logger.LogInformation("GoToScreen 0x{ScreenId:X2} (wValue=0x{WValue:X4})", screenId, wValue);
        }
        return ok;
    }

    public bool GoToStandby()
    {
        lock (_usbLock)
        {
            if (!_isConnected)
                return false;

            // The built-in Welcome screen is the vendor standby state. Deliberately
            // no ClearTimeout afterwards: once the heartbeat source stops, the
            // display sleeps on its own timeout.
            bool ok = GoToScreen(DisplayProtocolConstants.ScreenWelcome);
            if (ok)
            {
                LogToFile("[STANDBY] Display set to standby (welcome screen)");
            }
            return ok;
        }
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
