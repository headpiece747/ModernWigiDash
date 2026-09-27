namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The live backend and the rules for using and retiring it: the tracked
/// control/bulk transfers every path goes through, the adopt-and-release
/// lifecycle the connect loop drives, and the drain that keeps a native handle
/// from being freed mid-transfer.
///
/// This is the one place that state lives. It was four fields and six methods on
/// <see cref="DisplayHidTransport"/> — the backend, the teardown-only swap lock,
/// the in-flight count, the liveness flag, and the three transfer wrappers — so
/// the frame path, the touch path and the teardown all reached into the same
/// object to manage it, and the rule that binds them (never free a handle while a
/// transfer is in flight) was only visible by reading the whole file (the
/// architecture review's transport candidate, 2026-09-27).
///
/// The interface is small and the discipline is invisible at the call site: a
/// caller says "send this control out" and gets a verdict, with the tracking, the
/// null-backend safety and the retirement ordering handled here.
/// </summary>
internal sealed class DisplayWire
{
    // The teardown-only swap lock: it serializes the backend swap/dispose
    // against the in-flight drain wait. The hot transfer paths never take it —
    // that coarse lock used to hold a 16 ms touch read behind a ~55 ms frame
    // write. A plain object (not System.Threading.Lock) because the drain wait
    // calls Monitor.Wait on it directly.
    private readonly object _swapLock = new();

    private ITransferBackend? _backend;

    // The in-flight wire-transfer count: every control in/out and bulk write
    // increments it before touching the backend and decrements it in finally.
    // Teardown waits for zero before freeing the backend's native handle, so
    // the handle is never freed mid-transfer — the rule the coarse lock used
    // to hold, now scoped to teardown alone.
    private int _inFlightTransfers;

    private volatile bool _isConnected;

    /// <summary>Whether a backend is adopted and live. The connect loop's gate,
    /// the frame path's gate and the standby path's gate all read this one
    /// fact rather than testing the backend reference themselves.</summary>
    internal bool IsConnected => _isConnected;

    /// <summary>The adopted backend, for the init sequence (which is handed the
    /// backend to talk through). Null when nothing is adopted.</summary>
    internal ITransferBackend? Backend => _backend;

    /// <summary>Binds a freshly opened backend as the live one. The connect loop
    /// adopts BEFORE the init sequence, because that sequence talks through the
    /// backend it is about to accept.</summary>
    internal void Adopt(ITransferBackend backend)
    {
        lock (_swapLock)
        {
            _backend = backend;
        }
    }

    /// <summary>Records the transport's connection truth: set once the init
    /// sequence completes, cleared by a failed attempt or a release.</summary>
    internal void SetConnected(bool connected) => _isConnected = connected;

    /// <summary>Adopts a backend and records it live in one step (the injected-
    /// backend test constructor's case, where liveness comes from the backend
    /// itself).</summary>
    internal void AdoptLive(ITransferBackend backend)
    {
        Adopt(backend);
        _isConnected = backend.IsOpen;
    }

    /// <summary>
    /// Releases a failed attempt's LOCAL device and clears the adopted backend,
    /// under the swap lock. For a leg's own failure exit: it disposes the device
    /// that leg opened and never the adopted global backend, which belongs to a
    /// previous attempt.
    /// </summary>
    internal void AbandonAttempt(ITransferBackend? localDevice)
    {
        lock (_swapLock)
        {
            localDevice?.Dispose();
            _backend = null;
        }
    }

    /// <summary>
    /// Retires the live backend: stop new transfers (the liveness gate), wait for
    /// the in-flight transfers to drain (so the native handle is never freed
    /// mid-transfer), then dispose it.
    ///
    /// The drain wait is bounded by the pipe timeouts (a hung write waits out its
    /// own timeout — the CloseBound the close budgets derive from), and the lock
    /// is reentrant, so a re-entrant Dispose calling in is safe. Teardown is the
    /// only lock user.
    /// </summary>
    internal void Release()
    {
        lock (_swapLock)
        {
            _isConnected = false;

            while (Volatile.Read(ref _inFlightTransfers) > 0)
                Monitor.Wait(_swapLock, 10);

            // The backend owns the device-specific teardown (WinUSB handle free,
            // LibUsb interface release + close); the wire just drops it.
            _backend?.Dispose();
            _backend = null;
        }

        // Context is a shared singleton - don't dispose
    }

    /// <summary>
    /// Vendor OUT control transfer through the active backend.
    /// bmRequestType = 0x21 (Class | Interface | Host-to-Device). Tracked as
    /// an in-flight transfer (the teardown drain waits it out).
    /// </summary>
    internal bool ControlOut(byte request, ushort wValue, byte[]? data)
    {
        TrackTransferBegin();
        try
        {
            return _backend?.ControlOut(request, wValue, data) ?? false;
        }
        finally
        {
            TrackTransferEnd();
        }
    }

    /// <summary>
    /// Vendor IN control transfer through the active backend.
    /// bmRequestType = 0xA1 (Vendor | Device-to-Host | Interface).
    /// Reports the transferred byte count so callers can require a full report.
    /// Tracked as an in-flight transfer (the teardown drain waits it out).
    /// </summary>
    internal bool ControlIn(byte request, ushort wValue, ushort wIndex, byte[] buffer, out int transferred)
    {
        transferred = 0;
        TrackTransferBegin();
        try
        {
            return _backend?.ControlIn(request, buffer, out transferred, wValue, wIndex) ?? false;
        }
        finally
        {
            TrackTransferEnd();
        }
    }

    /// <summary>
    /// Writes bulk data through the active backend. Direct WinUSB writes the
    /// whole payload in one pipe write; the LibUsb adapter chunks for the
    /// legacy driver's throughput. Tracked as an in-flight transfer (the
    /// teardown drain waits it out).
    /// </summary>
    internal bool WriteBulkData(byte[] data)
    {
        TrackTransferBegin();
        try
        {
            return _backend?.BulkWrite(DisplayProtocolConstants.BulkOutPipeId, data, out _) ?? false;
        }
        finally
        {
            TrackTransferEnd();
        }
    }

    /// <summary>Runs the given action with transfer tracking (the teardown
    /// drain waits it out), for a leg that is several transfers wide rather than
    /// one — the init sequence today.</summary>
    internal bool RunTracked(Func<bool> action)
    {
        TrackTransferBegin();
        try
        {
            return action();
        }
        finally
        {
            TrackTransferEnd();
        }
    }

    /// <summary>
    /// Marks a wire transfer in flight before it reads the backend. The
    /// increment precedes the backend read: a transfer that lands while
    /// teardown is draining sees the backend still live and completes against
    /// it; one that lands after the drain sees a null backend and exits
    /// without touching a freed handle.
    /// </summary>
    private void TrackTransferBegin() => Interlocked.Increment(ref _inFlightTransfers);

    /// <summary>
    /// Releases the in-flight mark. Teardown re-checks the count after each
    /// wait slice, so the final release is observed within one slice.
    /// </summary>
    private void TrackTransferEnd() => Interlocked.Decrement(ref _inFlightTransfers);
}
