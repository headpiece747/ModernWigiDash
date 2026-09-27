namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The touch path's wire: one control IN, the short-transfer rule, and the
/// vendor report's parse.
///
/// The short-transfer rule is the reason this is its own module. It is a
/// correctness rule, not a formatting one: a short transfer leaves stale bytes
/// past the transferred count in the REUSED buffer, so only a full report may be
/// parsed as a touch. That rule used to sit in the middle of the transport's
/// touch method, among the connect loop and the frame framing, where it read as
/// one more null check (the architecture review's transport candidate, 2026-09-27).
/// </summary>
internal sealed class DisplayTouchReader(DisplayWire wire)
{
    // Reused per-call buffer: the touch buffer has a single consumer loop (the
    // 16 ms poll thread) and is distinct from the sender thread's frame header,
    // so it is never touched concurrently and costs no per-call allocation. The
    // backend swap (not the buffer) is what the in-flight count protects.
    private readonly byte[] _touchBuffer = new byte[DisplayProtocolConstants.TouchReportSize];

    // Diagnostic cadences: the two failure sites share one counter (they are
    // mutually exclusive branches), the Raw dump is every 200th. Note: the
    // raw-dump cadence counts success-path calls only (every 200th successful
    // read).
    private const string TouchDiagCategory = "TOUCH-DIAG";
    private readonly DiagLog _touchDiagLog = new(TouchDiagCategory, 20, logFirst: true);
    private readonly DiagLog _touchDiagRawLog = new(TouchDiagCategory, 200);

    /// <summary>
    /// Reads one touch report, or null when there is none to report: not
    /// connected, the control transfer failed, the transfer was short, the vendor
    /// type is "none", or the coordinates fall outside the framebuffer.
    /// </summary>
    internal TouchReport? Read()
    {
        if (!wire.IsConnected)
        {
            _touchDiagLog.Write($"Not connected: isConnected={wire.IsConnected}");
            return null;
        }

        try
        {
            byte[] touchBuf = _touchBuffer;

            bool ok = wire.ControlIn(DisplayProtocolConstants.CmdGetTouch, 0, 0, touchBuf, out int transferred);

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
