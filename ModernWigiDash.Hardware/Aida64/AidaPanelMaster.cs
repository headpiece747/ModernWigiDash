using System.Text;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Hardware.Aida64;

/// <summary>
/// The AIDA64 panel master: plays the role the vendor Manager plays, so AIDA64
/// publishes live frames without it. It registers widget slot 0 in the vendor's
/// named shared map (header + one 5x4 widget record) through the
/// <see cref="IAidaMmapWriter"/> seam, then runs the vendor's poll protocol:
/// heartbeat the master counter, watch the slave counter for liveness, and —
/// when the 4-byte frame marker at the widget's bitmap offset is non-zero —
/// read the frame directly from the map and clear the marker (the ack AIDA64
/// needs before it writes the next frame). The ack zeroes the slot's BMP
/// signature, so consumers read the published COPY
/// (<see cref="TryGetPublished"/>), never the slot.
///
/// Nothing here throws: a failed leg degrades to no published frame and a
/// logged reason. The loop is owned by a <see cref="PollLoop"/> (the house shape)
/// so the vendor I/O never touches the UI thread.
/// </summary>
public sealed class AidaPanelMaster : IDisposable
{
    /// <summary>The master heartbeat counter the vendor's Manager increments.</summary>
    internal const int MasterCounterOffset = 0;

    /// <summary>AIDA64's write counter, watched for liveness.</summary>
    internal const int SlaveCounterOffset = 4;

    /// <summary>The header + first widget record, contiguous (map 8..115).</summary>
    internal const int MapBlockOffset = 8;

    /// <summary>The vendor's <c>HeaderStruct</c> size.</summary>
    internal const int HeaderSize = 16;

    /// <summary>The vendor's <c>WidgetInfoStruct</c> size.</summary>
    internal const int WidgetRecordSize = 92;

    /// <summary>The header + first widget record, as one write (108 bytes -> the bitmap at 116).</summary>
    internal const int MapBlockSize = HeaderSize + WidgetRecordSize;

    /// <summary>The vendor's map magic tag.</summary>
    internal const uint MmapMagicTag = 0xC0FFFEEE;

    /// <summary>The vendor's widget-version value.</summary>
    internal const int WidgetVersion = 100;

    /// <summary>The registered widget's width in tiles.</summary>
    internal const int WidthTiles = 5;

    /// <summary>The registered widget's height in tiles.</summary>
    internal const int HeightTiles = 4;

    /// <summary>The master's poll cadence (the vendor's Manager polls at ~150 ms).</summary>
    internal const int PollIntervalMs = 150;

    /// <summary>The first widget's bitmap offset: the frame's 66-byte BMP header starts here.</summary>
    internal const int BitmapOffset = AidaMmapReader.MapHeaderSize;

    /// <summary>The bitmap byte count the vendor registers (BMP header + 1016x592 RGB565).</summary>
    internal const int BitmapSize = AidaMmapReader.BmpHeaderSize + AidaMmapReader.PanelPayloadBytes;

    private const int RingDepth = 3;
    private const string FriendlyName = "ModernWigiDash";
    private static readonly byte[] ZeroMarker = new byte[4];

    private readonly IAidaMmapSource _source;
    private readonly IAidaMmapWriter _writer;
    private readonly AidaMmapReader _reader;
    private readonly Action<string> _log;
    private readonly Task? _waitBeforeRegister;
    private readonly TimeProvider _clock;
    // Three gates, one owner each. `_gate` guards registered/counter/error
    // state and is never held across vendor I/O. `_startGate` guards the
    // loop lifecycle (start/dispose ordering). `_readerGate` guards the map
    // reads against the reader's disposal. The render tick touches only
    // `_startGate`, so vendor I/O can never block a frame (the 2026-11-11
    // freeze: the tick waited on a gate held across WCF calls).
    private readonly System.Threading.Lock _gate = new();
    private readonly System.Threading.Lock _startGate = new();
    private readonly System.Threading.Lock _readerGate = new();
    private readonly LogOnChange _failureDedup = new();
    private readonly byte[] _slaveBuffer = new byte[4];
    private readonly byte[] _markerBuffer = new byte[4];
    private readonly byte[] _headerBuffer = new byte[4];
    private readonly byte[][] _ring = new byte[RingDepth][];
    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly TaskCompletionSource _slotQuiesced =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PollLoop? _loop;
    private TimeSpan _pollInterval;
    private AidaPublishedFrame? _published;
    private int _ringIndex;
    private int _inFlightSlotWork;
    private long _generation;
    private uint _masterCounter;
    private uint _slaveBaseline;
    private DateTimeOffset _lastSlaveProgressUtc = DateTimeOffset.MinValue;
    private int _consecutiveWriteFailures;
    private int _consecutiveFrameReadFailures;
    private int _idleHeaderCheckTicks;
    private bool _slaveAlive;
    private bool _registered;
    private volatile bool _started;
    private volatile bool _disposed;
    private string? _lastError;

    /// <summary>Binds the master to the map it reads and the handshake it writes.</summary>
    /// <param name="source">The map read seam (the master owns and disposes it).</param>
    /// <param name="writer">The handshake write seam.</param>
    /// <param name="log">The diagnostic sink; defaults to <see cref="FileLog"/>.</param>
    /// <param name="pollInterval">Optional initial poll interval (defaults to 150 ms).</param>
    /// <param name="timeProvider">Optional clock for liveness timeouts (defaults to <see cref="TimeProvider.System"/>).</param>
    public AidaPanelMaster(
        IAidaMmapSource source,
        IAidaMmapWriter writer,
        Action<string>? log = null,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null)
        : this(source, writer, waitBeforeRegister: null, log, pollInterval, timeProvider)
    {
    }

    internal AidaPanelMaster(
        IAidaMmapSource source,
        IAidaMmapWriter writer,
        Task? waitBeforeRegister,
        Action<string>? log = null,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(PollIntervalMs);
        PollLoop.ValidateInterval(interval, nameof(pollInterval));
        _source = source;
        _writer = writer;
        _reader = new AidaMmapReader(source);
        _waitBeforeRegister = waitBeforeRegister;
        _log = log ?? FileLog.Write;
        _pollInterval = interval;
        _clock = timeProvider ?? TimeProvider.System;
        for (int i = 0; i < RingDepth; i++)
        {
            _ring[i] = new byte[AidaMmapReader.PanelPayloadBytes];
        }
    }

    /// <summary>True once slot 0 is registered (the master is driving AIDA64).</summary>
    public bool IsRegistered
    {
        get { lock (_gate) return _registered; }
    }

    /// <summary>The last registration/frame failure, or null after a good frame.</summary>
    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>Completes once teardown is committed and no slot-0 registration
    /// or write call remains in flight on the background worker.</summary>
    internal Task SlotReleasedTask => _slotQuiesced.Task;

    /// <summary>
    /// The latest published frame, or null before the first one. The returned
    /// copy's buffer is stable until the ring wraps (three publishes later); a
    /// consumer that builds its bitmap within a frame is safe.
    /// </summary>
    public AidaPublishedFrame? TryGetPublished() => Volatile.Read(ref _published);

    /// <summary>Starts the poll loop (idempotent); the loop registers slot 0 on its first ready tick.</summary>
    public void Start()
    {
        // Fast path: the render tick calls this every frame, so the steady
        // state must not take a lock at all.
        if (_started || _disposed) return;

        lock (_startGate)
        {
            if (_started || _disposed) return;
            var loop = new PollLoop(
                "AIDA-MASTER",
                _pollInterval,
                ready: EnsureRegistered,
                tick: PollOnce,
                onTickFailure: static () => { },
                log: _log);
            _loop = loop;
            _started = true;
            // Started INSIDE the gate: Dispose captures the same fields under
            // it, so a loop can never be created after the stop pass.
            loop.Start();
        }
    }

    /// <summary>
    /// Updates the master poll loop cadence in place without blocking behind
    /// in-flight vendor WCF calls or joining the background worker thread.
    /// </summary>
    public void UpdatePollingPeriod(TimeSpan interval)
    {
        PollLoop.ValidateInterval(interval, nameof(interval));
        lock (_startGate)
        {
            if (_disposed) return;
            _pollInterval = interval;
            _loop?.SetInterval(interval);
        }
    }

    /// <summary>Stops the loop and disposes the reader and its map source.</summary>
    public void Dispose()
    {
        PollLoop? loop;
        lock (_startGate)
        {
            if (_disposed) return;
            _disposed = true;
            loop = _loop;
            _loop = null;
        }

        try
        {
            // Stop outside _startGate: the join may take seconds, and _startGate
            // is what Start/Dispose use to order themselves.
            loop?.Dispose();

            // The reader gate waits for at most one in-flight map read; every
            // read section re-checks _disposed, so no I/O starts after this.
            lock (_readerGate)
            {
                _reader.Dispose();
            }
        }
        finally
        {
            bool quiesced;
            lock (_gate)
            {
                quiesced = _inFlightSlotWork == 0;
            }

            if (quiesced)
            {
                _slotQuiesced.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Initializes the vendor's AIDA64 provider and registers widget slot 0
    /// (the header plus one 5x4 widget record). Idempotent; returns false with a
    /// deduplicated log line while it cannot register, so the poll loop backs
    /// off (it pauses 500 ms whenever the ready probe is false).
    /// </summary>
    internal bool EnsureRegistered()
    {
        if (!TryEnterSlotWork()) return false;
        try
        {
            if (_waitBeforeRegister is { IsCompleted: false })
            {
                LogOnce("waiting for previous panel master to release slot 0");
                return false;
            }

            lock (_gate)
            {
                if (_disposed) return false;
                if (_registered) return true;
            }

            // Vendor I/O outside _gate: the ready probe runs until slot 0
            // registers, and a slow WCF service must not block the render tick
            // (which only ever touches _startGate) or the diagnostic readers.
            if (!_writer.TryInit(out string? initError))
            {
                lock (_gate) _lastError = initError;
                LogOnce($"provider unavailable: {initError}");
                return false;
            }

            lock (_readerGate)
            {
                if (_disposed) return false;
                _source.Reset();
            }

            if (!_writer.TryWrite(MapBlockOffset, BuildMapBlock(), out string? writeError))
            {
                lock (_gate) _lastError = writeError;
                LogOnce($"slot registration failed: {writeError}");
                return false;
            }

            // A write that returns after teardown must not re-register the master.
            lock (_gate)
            {
                if (_disposed) return false;
                _registered = true;
                _slaveAlive = false;
                _slaveBaseline = 0;
                _consecutiveFrameReadFailures = 0;
                _consecutiveWriteFailures = 0;
                _idleHeaderCheckTicks = 0;
                _lastSlaveProgressUtc = _clock.GetUtcNow();
                _lastError = null;
                _failureDedup.Changed(null);
            }

            _log("[AIDA-MASTER] registered slot 0 (1016x592, 5x4)");
            return true;
        }
        finally
        {
            ExitSlotWork();
        }
    }

    /// <summary>
    /// One master tick: heartbeat the vendor's master counter, watch the slave
    /// counter for liveness, then consume a frame when the marker is set —
    /// publishing a copy and clearing the marker (the ack).
    /// </summary>
    internal void PollOnce()
    {
        if (!TryEnterSlotWork()) return;
        try
        {
            uint counter;
            lock (_gate)
            {
                if (_disposed || !_registered) return;
                counter = _masterCounter++;
            }

            if (!_writer.TryWrite(MasterCounterOffset, BitConverter.GetBytes(counter), out _))
            {
                RecordWriteOutcome(false);
                return;
            }

            if (_disposed) return;

            bool ackOk = TryConsumeAvailableFrame();
            if (_disposed) return;
            RecordWriteOutcome(ackOk);
        }
        finally
        {
            ExitSlotWork();
        }
    }

    private bool TryConsumeAvailableFrame()
    {
        if (++_idleHeaderCheckTicks >= 10)
        {
            _idleHeaderCheckTicks = 0;
            bool headerRead;
            string? headerError;
            uint magic = 0;
            int numWidgets = 0;
            lock (_readerGate)
            {
                if (_disposed) return true;
                headerRead = _source.TryRead(AidaMmapReader.MagicTagOffset, 4, _headerBuffer, out headerError);
                if (headerRead)
                {
                    magic = BitConverter.ToUInt32(_headerBuffer, 0);
                    headerRead = _source.TryRead(AidaMmapReader.NumWidgetsOffset, 4, _headerBuffer, out headerError);
                    if (headerRead)
                    {
                        numWidgets = BitConverter.ToInt32(_headerBuffer, 0);
                    }
                }

                if (headerRead && (magic != MmapMagicTag || numWidgets <= 0))
                {
                    _source.Reset();
                }
            }

            if (!headerRead)
            {
                lock (_gate) _lastError = headerError;
                LogOnce($"map header read failed: {headerError}");
                // MMF read failure while WCF MasterCounter write succeeded: keep slot 0 registered
                // and preserve the read error instead of tripping WCF write failure strikes.
                return true;
            }

            if (magic != MmapMagicTag || numWidgets <= 0)
            {
                lock (_gate)
                {
                    _registered = false;
                    _slaveAlive = false;
                    _slaveBaseline = 0;
                    _consecutiveFrameReadFailures = 0;
                    _consecutiveWriteFailures = 0;
                    _lastError = "AIDA64 slot registration reset";
                }
                Volatile.Write(ref _published, null);
                LogOnce("AIDA64 map header reset; re-registering slot 0");
                return true;
            }
        }

        bool slaveRead;
        string? slaveError;
        uint slave;
        lock (_readerGate)
        {
            if (_disposed) return true;
            slaveRead = _source.TryRead(SlaveCounterOffset, 4, _slaveBuffer, out slaveError);
            slave = slaveRead ? BitConverter.ToUInt32(_slaveBuffer, 0) : 0;
        }

        if (!slaveRead)
        {
            lock (_gate) _lastError = slaveError;
            LogOnce($"slave counter read failed: {slaveError}");
            return true;
        }

        var now = _clock.GetUtcNow();
        if (!_slaveAlive)
        {
            if (slave == _slaveBaseline) return true; // AIDA64 has not written yet
            _slaveAlive = true;
            _lastSlaveProgressUtc = now;
        }
        else if (slave != _slaveBaseline)
        {
            _lastSlaveProgressUtc = now;
        }
        else if (now - _lastSlaveProgressUtc >= TimeSpan.FromSeconds(5))
        {
            _slaveAlive = false;
            _consecutiveFrameReadFailures = 0;
            Volatile.Write(ref _published, null);
            lock (_gate) _lastError = "Waiting for AIDA64 frames";
            LogOnce("AIDA64 frame producer inactive; cleared stale frame");
            return true;
        }

        _slaveBaseline = slave;

        bool markerRead;
        string? markerError;
        uint marker;
        lock (_readerGate)
        {
            if (_disposed) return true;
            markerRead = _source.TryRead(BitmapOffset, 4, _markerBuffer, out markerError);
            marker = markerRead ? BitConverter.ToUInt32(_markerBuffer, 0) : 0;
        }

        if (!markerRead)
        {
            lock (_gate) _lastError = markerError;
            LogOnce($"frame marker read failed: {markerError}");
            return true;
        }

        if (marker == 0)
        {
            _consecutiveFrameReadFailures = 0;
            return true; // no new frame
        }

        AidaFrameSnapshot? frame;
        string? readError;
        lock (_readerGate)
        {
            if (_disposed) return true;
            frame = _reader.TryReadFrame();
            readError = _reader.LastError;
        }

        if (frame is null)
        {
            _consecutiveFrameReadFailures++;
            lock (_gate)
            {
                _lastError = readError;
            }

            LogOnce($"frame read failed: {readError}");
            if (_consecutiveFrameReadFailures < 2)
            {
                // Unlocked reads can catch AIDA64 mid-write; retry once on the next
                // tick before clearing the marker and dropping the frame.
                return true;
            }

            _consecutiveFrameReadFailures = 0;
        }
        else
        {
            _consecutiveFrameReadFailures = 0;
            Publish(frame);
            lock (_gate)
            {
                _lastError = null;
                _failureDedup.Changed(null);
            }
        }

        // Ack regardless once retried: AIDA64 writes the next frame only once the marker
        // is cleared, and a persistently unreadable frame must advance the protocol.
        if (_disposed) return true;
        return _writer.TryWrite(BitmapOffset, ZeroMarker, out _);
    }

    private bool TryEnterSlotWork()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            _inFlightSlotWork++;
            return true;
        }
    }

    private void ExitSlotWork()
    {
        bool complete;
        lock (_gate)
        {
            complete = --_inFlightSlotWork == 0 && _disposed;
        }

        if (complete)
        {
            _slotQuiesced.TrySetResult();
        }
    }

    private void RecordWriteOutcome(bool succeeded)
    {
        lock (_gate)
        {
            if (succeeded)
            {
                _consecutiveWriteFailures = 0;
                return;
            }

            if (++_consecutiveWriteFailures >= 3)
            {
                _registered = false;
                _slaveAlive = false;
                _slaveBaseline = 0;
                _consecutiveFrameReadFailures = 0;
                _consecutiveWriteFailures = 0;
                _lastError = "AIDA64 slot registration lost";
                Volatile.Write(ref _published, null);
            }
        }
    }

    private void Publish(AidaFrameSnapshot frame)
    {
        var buffer = _ring[_ringIndex];
        _ringIndex = (_ringIndex + 1) % RingDepth;
        Array.Copy(frame.Buffer, frame.PixelOffset, buffer, 0, frame.PayloadLength);
        Volatile.Write(
            ref _published,
            new AidaPublishedFrame(buffer, frame.PayloadLength, frame.Width, frame.Height, ++_generation));
    }

    /// <summary>
    /// The header + first widget record as one contiguous write. Field offsets
    /// are the vendor's <c>Aida64MmapDef</c> layout, verified against the live
    /// map (header at 8, record at 24, bitmap at 116).
    /// </summary>
    private byte[] BuildMapBlock()
    {
        var block = new byte[MapBlockSize];

        // Header (map 8..23)
        BitConverter.GetBytes(MmapMagicTag).CopyTo(block, 0);
        BitConverter.GetBytes(WidgetVersion).CopyTo(block, 4);
        BitConverter.GetBytes(1).CopyTo(block, 8);  // NumWidgets
        BitConverter.GetBytes(1).CopyTo(block, 12); // ConfigUpdated

        // First widget record (map 24..115, block 16..107)
        const int record = HeaderSize;
        _instanceId.ToByteArray().CopyTo(block, record);
        BitConverter.GetBytes(AidaMmapReader.PanelWidth).CopyTo(block, record + 16);
        BitConverter.GetBytes(AidaMmapReader.PanelHeight).CopyTo(block, record + 20);
        BitConverter.GetBytes(WidthTiles).CopyTo(block, record + 24);
        BitConverter.GetBytes(HeightTiles).CopyTo(block, record + 28);
        Encoding.ASCII.GetBytes(FriendlyName).CopyTo(block, record + 32);
        BitConverter.GetBytes(AidaMmapReader.BitmapFormatRgb565).CopyTo(block, record + 64);
        BitConverter.GetBytes(BitmapSize).CopyTo(block, record + 68);
        BitConverter.GetBytes(BitmapOffset).CopyTo(block, record + 72);

        return block;
    }

    private void LogOnce(string message)
    {
        if (_failureDedup.Changed(message))
        {
            _log($"[AIDA-MASTER] {message}");
        }
    }
}
