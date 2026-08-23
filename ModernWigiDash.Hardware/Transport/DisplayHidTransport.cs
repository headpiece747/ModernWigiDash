// <copyright file="DisplayHidTransport.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    private volatile bool _isConnected;
    private int _isDisposed;
    private readonly Lock _usbLock = new();

    // 3-page initialization (Base screens 0x20..0x22 — ScreenBase0..2 in
    // DisplayProtocolConstants; only Base0 is const'ed here). The app frames
    // are always written to page 0; page navigation is compositor-side.
    private const int NumPages = 3;
    private const byte Base0 = DisplayProtocolConstants.ScreenBase0;

    /// <summary>
    /// The transport's connection truth — kept for test observability only.
    /// It is NOT on the <see cref="IDisplayTransport"/> seam: production
    /// callers (the engine) gate on their own ConnectionState, and the
    /// transport's methods read the private field directly.
    /// </summary>
    internal bool IsConnected => _isConnected;

    /// <summary>
    /// The worst-case duration a hung device can hold the transport's teardown
    /// — the named budget behind the engine's never-stall-on-close invariant.
    /// The max of the two backend stall bounds: the WinUSB bulk pipe timeout
    /// (an in-flight frame write holds the transport lock for up to that long)
    /// and the LibUsb leg's chunk-timeout product over a full frame (every
    /// chunk exhausting its timeout). The engine's close waits are deliberately
    /// shorter than this — it abandons a slow close rather than follow it.
    /// </summary>
    internal static TimeSpan CloseBound => TimeSpan.FromMilliseconds(Math.Max(
        DisplayProtocolConstants.BulkPipeTimeoutMs,
        (long)ChunkedBulkWrite.WorstCaseWrite(DisplayGeometry.FrameBufferSize).TotalMilliseconds));

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
        "Using WinUSB for all transfers (control + bulk)");

    /// <summary>The real LibUsbDotNet leg. Internal so tests can keep it in a
    /// fake provider list.</summary>
    internal ConnectProvider LibUsbProvider => new(
        "USB-LIBUSB",
        TryCreateLibUsbBackend,
        "LibUsbDotNet 3.0");

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
            // This attempt's file-log vocabulary: the loop binds the provider's
            // tag into a DiagLog once, and every line it emits for this leg —
            // exception, success, init failure — rides that tag (the DiagLog
            // module; no hand-baked "[TAG] " prefixes at call sites).
            var legLog = new DiagLog(provider.Tag, 1);
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
                legLog.Write($"Connect exception: {ex.GetType().FullName}: {ex.Message}");
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
                if (provider.SuccessLine is not null)
                {
                    legLog.Write(provider.SuccessLine);
                }
                _logger.LogInformation("Connected to WigiDash via {Via}", provider.DisplayName);
                return true;
            }

            // Init failed through this stack — the same control sequence may
            // complete through the next provider's driver stack, so try it. The
            // loop owns this line (position-aware), spelled once from the tag.
            bool hasNextAttempt = Array.IndexOf(ProviderFactories, provider) < ProviderFactories.Length - 1;
            legLog.Write(hasNextAttempt
                ? "Init commands failed — trying the next provider"
                : "Init commands failed — connection failed");
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
    /// There is deliberately no PING here — the only PING lives in
    /// <see cref="SendInitCommands"/>. Partial-state teardown is owned here: a
    /// failed open disposes
    /// the device under <c>_usbLock</c> (same rule as <see cref="Cleanup"/>).
    /// Both failure exits dispose the LOCAL device — never the adopted global
    /// backend, which belongs to a previous provider attempt.
    /// </summary>
    private ITransferBackend? TryCreateWinUsbBackend()
    {
        // The leg's own file-log lines bind its tag once here; the device's
        // Open diagnostics (WinUsbBulkDevice) emit the same tag through its
        // own bound DiagLog — one vocabulary across the seam.
        DiagLog legLog = new("USB-WINUSB", 1);
        var winUsb = new WinUsbBulkDevice();
        try
        {
            if (winUsb.Open(DisplayProtocolConstants.WinUsbInterfaceGuid))
            {
                legLog.Write("Direct WinUSB connection opened");
                return winUsb;
            }

            legLog.Write("Failed to open the WinUSB device interface");
            TearDownWinUsb(winUsb);
            return null;
        }
        catch (Exception ex)
        {
            legLog.Write($"Open exception: {ex.Message}");
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
    /// attempt.
    /// </summary>
    private ITransferBackend? TryCreateLibUsbBackend()
    {
        // The leg's own file-log vocabulary (USB-FIND/OPEN/CONFIG/CLAIM/
        // ENDPOINT/DESC + the USB-LIBUSB attempt line) is owned by the adapter
        // — the same tags WinUsbBulkDevice binds for its Open (one vocabulary
        // across the seam). The transport keeps only the attempt's start line
        // and hands the device-lookup seam across.
        _logger.LogInformation("Connecting to WigiDash via LibUsbDotNet 3.0 (fallback)...");
        return LibUsbTransferBackend.TryOpen(LibUsbDeviceProvider);
    }

    /// <summary>
    /// Sends the device initialization sequence (PING + 3-page setup + blank
    /// framebuffer + GoToScreen). Called by <see cref="Connect"/>; internal so
    /// tests can drive the sequence through an injected backend.
    /// The verdict covers the wire steps that can fail: a failed page-setup
    /// or GoToScreen control write, or a failed blank-frame header/bulk
    /// write, fails the init — on-device, a reconnect observed exactly that
    /// split (every control write fine, the 1.2 MB init write timing out at
    /// the 30 s pipe bound), and a connected verdict for such a pipe would be
    /// a falsehood the engine would have to inherit.
    /// </summary>
    internal bool SendInitCommands()
    {
        _logger.LogInformation("Sending device initialization commands...");
        DiagLog initLog = new("USB-INIT", 1);

        // PING (CMD_PING, Control IN) — the liveness probe, logged not gated.
        byte[] pingBuf = new byte[4];
        bool pingOk = ControlIn(DisplayProtocolConstants.CmdPing, 0, 0, pingBuf, out _);
        initLog.Write($"PING: ok={pingOk}");

        // Explicit wake: a display left asleep by the previous session's standby
        // (backlight off) is woken before the brightness/page/frame work — the
        // vendor Manager's own wake ritual (WakeDevice = ClearScreenTimeout).
        bool wakeOk = ControlOut(DisplayProtocolConstants.CmdWakeDevice, 0, null);
        initLog.Write($"Wake: ok={wakeOk}");

        // Set brightness to 100%
        ControlOut(DisplayProtocolConstants.CmdSetBrightness, 0, [DisplayProtocolConstants.InitBrightnessLevel]);

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
            initLog.Write($"Page {page}: ClearPage + AddWidget(0,0) sent ({widgetConfig.Length} bytes), ok={clearOk && widgetOk}");
            initOk &= clearOk && widgetOk;
        }

        // Write blank framebuffer to page 0 only (first visible page). The
        // verdict folds this in like the control writes: a blank frame that
        // never arrives means the init sequence did not survive, and the
        // connect result must say so.
        initOk &= WriteBlankFramebuffer(page: 0, widgetId: 0);

        // GoToScreen(Base0): CMD_SEND_UI_CMD (0x70) wValue=0x20
        bool gotoOk = ControlOut(DisplayProtocolConstants.CmdGoToScreen, Base0, null);
        initLog.Write($"GoToScreen(Base0) sent — all 3 pages initialized, ok={gotoOk}");

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

    /// <returns>True when the blank frame fully arrived (the header control
    /// write plus the full bulk write) — <see cref="SendInitCommands"/> folds
    /// this into the init verdict like the control writes.</returns>
    private bool WriteBlankFramebuffer(byte page, byte widgetId)
    {
        DiagLog hwInitLog = new("HW-INIT", 1);
        if (_backend is not { IsOpen: true })
        {
            hwInitLog.Write("Blank framebuffer skipped: backend not open");
            return false;
        }

        try
        {
            byte[] blankFrame = new byte[DisplayProtocolConstants.FrameBufferSize];
            hwInitLog.Write($"Writing blank framebuffer ({blankFrame.Length} bytes) to page={page} widget={widgetId}");

            // Control transfer header: offset=0, length=FrameBufferSize (the
            // single wire-format owner, shared with the 30 FPS send path).
            byte[] header = new byte[DisplayProtocolConstants.FrameHeaderDataSize];
            BuildFrameHeader(header, blankFrame.Length);

            ushort wValue = (ushort)((page << 8) | widgetId);
            bool headerOk = ControlOut(DisplayProtocolConstants.CmdFrameHeader, wValue, header);
            hwInitLog.Write($"FrameHeader control write: ok={headerOk}");
            if (!headerOk)
            {
                return false;
            }

            bool bulkOk = WriteBulkData(blankFrame);
            hwInitLog.Write($"Blank framebuffer bulk write: ok={bulkOk}");
            return bulkOk;
        }
        catch (Exception ex)
        {
            hwInitLog.Write($"Blank framebuffer write exception: {ex.Message}");
            return false;
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
    // successful read).
    private const string TouchDiagCategory = "TOUCH-DIAG";
    private readonly DiagLog _touchDiagLog = new(TouchDiagCategory, 20, logFirst: true);
    private readonly DiagLog _touchDiagRawLog = new(TouchDiagCategory, 200);
    // Standby is the one-shot line of the shutdown path — always fires.
    private readonly DiagLog _standbyLog = new("STANDBY", 1);
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
                // Same vocabulary as the other touch-diag lines above — the
                // hand-baked "[TOUCH-DIAG] " prefix was the drift the DiagLog
                // binding exists to prevent.
                _touchDiagLog.Write($"Exception: {ex.Message}");
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

    public FrameSendResult SendFrame(ReadOnlyMemory<byte> frameBuffer)
    {
        if (!_isConnected)
        {
            if (_sendFrameSkippedLog.Due())
                _logger.LogWarning("SendFrame SKIPPED: not connected");
            return FrameSendResult.Refused;
        }

        if (frameBuffer.Length < DisplayProtocolConstants.FrameBufferSize)
        {
            _logger.LogWarning("Frame buffer too small: {Len} < {Req}",
                frameBuffer.Length, DisplayProtocolConstants.FrameBufferSize);
            return FrameSendResult.Refused;
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
                    return FrameSendResult.Failed;
                }

                if (!WriteBulkData(frameArray))
                {
                    ControlOut(DisplayProtocolConstants.CmdFrameAbort, 0, null);
                    return FrameSendResult.Failed;
                }
            }

            return FrameSendResult.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendFrame FAILED");
            return FrameSendResult.Failed;
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

            // The vendor's own sleep ritual (its Manager's exit path is exactly
            // this): the Welcome screen, then the immediate-sleep command that
            // turns the backlight off. Without the sleep command the display
            // has no active auto-sleep — it would idle on the Welcome screen
            // with the backlight on.
            bool ok = GoToScreen(DisplayProtocolConstants.ScreenWelcome)
                && ControlOut(DisplayProtocolConstants.CmdSleepDevice, 0, null);
            if (ok)
            {
                // One-shot per shutdown (the standby guarantee) — Cadence 1.
                _standbyLog.Write("Display set to standby (welcome screen + sleep, backlight off)");
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
}
