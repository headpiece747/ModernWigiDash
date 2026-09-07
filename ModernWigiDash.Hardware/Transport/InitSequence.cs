namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The transport's init sequence (candidate 6 of the architecture review):
/// PING → wake → brightness → 3-page clear+widget → blank framebuffer →
/// GoToScreen(Base0), returning a named verdict. Before this module the
/// sequence lived inline in <see cref="DisplayHidTransport.SendInitCommands"/>
/// and its verdict (including the blank-frame bulk write that once timed out
/// on-device) was only testable through the whole connect path. Now the
/// sequence takes an <see cref="ITransferBackend"/> + the protocol constants
/// and returns the verdict, so it has its own test surface without a device.
/// </summary>
internal sealed class InitSequence(ITransferBackend backend, DiagLog log)
{
    private const int NumPages = 3;
    private static readonly byte Base0 = DisplayProtocolConstants.ScreenBase0;

    /// <summary>Runs the full init sequence against the given backend. Returns
    /// true when every step succeeded (PING is logged but not gated); false
    /// when any control write, page init, blank-frame write, or GoToScreen
    /// failed.</summary>
    public bool Run()
    {
        // PING (CMD_PING, Control IN) — the liveness probe, logged not gated.
        byte[] pingBuf = new byte[4];
        bool pingOk = backend.ControlIn(DisplayProtocolConstants.CmdPing, pingBuf, out _);
        log.Write($"PING: ok={pingOk}");

        // Explicit wake: a display left asleep by the previous session's standby
        // (backlight off) is woken before the brightness/page/frame work — the
        // vendor Manager's own wake ritual (WakeDevice = ClearScreenTimeout).
        bool wakeOk = backend.ControlOut(DisplayProtocolConstants.CmdWakeDevice, 0, null);
        log.Write($"Wake: ok={wakeOk}");

        // Set brightness to 100%
        backend.ControlOut(DisplayProtocolConstants.CmdSetBrightness, 0, [DisplayProtocolConstants.InitBrightnessLevel]);

        // Initialize all 3 pages (3-page double-buffering). Each page gets:
        // ClearPage → AddWidget(full-screen) → blank framebuffer.
        bool initOk = true;
        for (int page = 0; page < NumPages; page++)
        {
            bool clearOk = backend.ControlOut(DisplayProtocolConstants.CmdClearPage, (ushort)page, null);
            byte[] widgetConfig = DisplayProtocolConstants.BuildWidgetConfig(
                x: 0, y: 0,
                width: DisplayProtocolConstants.FramebufferWidth,
                height: DisplayProtocolConstants.FramebufferHeight);
            bool widgetOk = backend.ControlOut(DisplayProtocolConstants.CmdAddWidget, (ushort)((page << 8) | 0), widgetConfig);
            log.Write($"Page {page}: ClearPage + AddWidget(0,0) sent ({widgetConfig.Length} bytes), ok={clearOk && widgetOk}");
            initOk &= clearOk && widgetOk;
        }

        // Write blank framebuffer to page 0 only (first visible page). The
        // verdict folds this in like the control writes: a blank frame that
        // never arrives means the init sequence did not survive.
        initOk &= WriteBlankFramebuffer(backend, log, page: 0, widgetId: 0);

        // GoToScreen(Base0): CMD_SEND_UI_CMD (0x70) wValue=0x20
        bool gotoOk = backend.ControlOut(DisplayProtocolConstants.CmdGoToScreen, Base0, null);
        log.Write($"GoToScreen(Base0) sent — all 3 pages initialized, ok={gotoOk}");

        return initOk && gotoOk;
    }

    /// <returns>True when the blank frame fully arrived (the header control
    /// write plus the full bulk write).</returns>
    private static bool WriteBlankFramebuffer(ITransferBackend backend, DiagLog log, byte page, byte widgetId)
    {
        if (!backend.IsOpen)
        {
            log.Write("Blank framebuffer skipped: backend not open");
            return false;
        }

        try
        {
            byte[] blankFrame = new byte[DisplayProtocolConstants.FrameBufferSize];
            log.Write($"Writing blank framebuffer ({blankFrame.Length} bytes) to page={page} widget={widgetId}");

            byte[] header = new byte[DisplayProtocolConstants.FrameHeaderDataSize];
            DisplayProtocolConstants.BuildFrameHeader(header, blankFrame.Length);

            ushort wValue = (ushort)((page << 8) | widgetId);
            bool headerOk = backend.ControlOut(DisplayProtocolConstants.CmdFrameHeader, wValue, header);
            log.Write($"FrameHeader control write: ok={headerOk}");
            if (!headerOk)
            {
                return false;
            }

            bool bulkOk = backend.BulkWrite(DisplayProtocolConstants.BulkOutPipeId, blankFrame, out _);
            log.Write($"Blank framebuffer bulk write: ok={bulkOk}");
            return bulkOk;
        }
        catch (Exception ex)
        {
            log.Write($"Blank framebuffer write exception: {ex.Message}");
            return false;
        }
    }
}
