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
    private readonly System.Threading.Lock _gate = new();
    private readonly LogOnChange _failureDedup = new();
    private readonly byte[] _slaveBuffer = new byte[4];
    private readonly byte[] _markerBuffer = new byte[4];
    private readonly byte[][] _ring = new byte[RingDepth][];
    private readonly Guid _instanceId = Guid.NewGuid();

    private PollLoop? _loop;
    private AidaPublishedFrame? _published;
    private int _ringIndex;
    private long _generation;
    private uint _masterCounter;
    private uint _slaveBaseline;
    private bool _slaveAlive;
    private bool _registered;
    private bool _disposed;
    private string? _lastError;

    /// <summary>Binds the master to the map it reads and the handshake it writes.</summary>
    /// <param name="source">The map read seam (the master owns and disposes it).</param>
    /// <param name="writer">The handshake write seam.</param>
    /// <param name="log">The diagnostic sink; defaults to <see cref="FileLog"/>.</param>
    public AidaPanelMaster(IAidaMmapSource source, IAidaMmapWriter writer, Action<string>? log = null)
    {
        _source = source;
        _writer = writer;
        _reader = new AidaMmapReader(source);
        _log = log ?? FileLog.Write;
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

    /// <summary>
    /// The latest published frame, or null before the first one. The returned
    /// copy's buffer is stable until the ring wraps (three publishes later); a
    /// consumer that builds its bitmap within a frame is safe.
    /// </summary>
    public AidaPublishedFrame? TryGetPublished() => Volatile.Read(ref _published);

    /// <summary>Starts the poll loop (idempotent); the loop registers slot 0 on its first ready tick.</summary>
    public void Start()
    {
        PollLoop loop;
        lock (_gate)
        {
            if (_disposed || _loop is not null) return;
            loop = new PollLoop(
                "AIDA-MASTER",
                TimeSpan.FromMilliseconds(PollIntervalMs),
                ready: EnsureRegistered,
                tick: PollOnce,
                onTickFailure: static () => { },
                log: _log);
            _loop = loop;
        }

        loop.Start();
    }

    /// <summary>Stops the poll loop (idempotent). The slot registration is left in place.</summary>
    public void Stop()
    {
        PollLoop? loop;
        lock (_gate)
        {
            loop = _loop;
            _loop = null;
        }

        loop?.Dispose();
    }

    /// <summary>Stops the loop and disposes the reader and its map source.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
        _reader.Dispose();
    }

    /// <summary>
    /// Initializes the vendor's AIDA64 provider and registers widget slot 0
    /// (the header plus one 5x4 widget record). Idempotent; returns false with a
    /// deduplicated log line while it cannot register, so the poll loop backs
    /// off (it pauses 500 ms whenever the ready probe is false).
    /// </summary>
    internal bool EnsureRegistered()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_registered) return true;

            if (!_writer.TryInit(out string? initError))
            {
                LogOnce($"provider unavailable: {initError}");
                return false;
            }

            if (!_writer.TryWrite(MapBlockOffset, BuildMapBlock(), out string? writeError))
            {
                LogOnce($"slot registration failed: {writeError}");
                return false;
            }

            _registered = true;
            _lastError = null;
            _failureDedup.Changed(null);
            _log("[AIDA-MASTER] registered slot 0 (1016x592, 5x4)");
            return true;
        }
    }

    /// <summary>
    /// One master tick: heartbeat the vendor's master counter, watch the slave
    /// counter for liveness, then consume a frame when the marker is set —
    /// publishing a copy and clearing the marker (the ack).
    /// </summary>
    internal void PollOnce()
    {
        lock (_gate)
        {
            if (_disposed || !_registered) return;

            _writer.TryWrite(MasterCounterOffset, BitConverter.GetBytes(_masterCounter++), out _);

            if (!_source.TryRead(SlaveCounterOffset, 4, _slaveBuffer, out _)) return;

            uint slave = BitConverter.ToUInt32(_slaveBuffer, 0);
            if (!_slaveAlive)
            {
                if (slave == _slaveBaseline) return; // AIDA64 has not written yet
                _slaveAlive = true;
            }

            _slaveBaseline = slave;

            if (!_source.TryRead(BitmapOffset, 4, _markerBuffer, out _)) return;
            if (BitConverter.ToUInt32(_markerBuffer, 0) == 0) return; // no new frame

            var frame = _reader.TryReadFrame();
            if (frame is null)
            {
                _lastError = _reader.LastError;
                LogOnce($"frame read failed: {_lastError}");
            }
            else
            {
                Publish(frame);
                _lastError = null;
                _failureDedup.Changed(null);
            }

            // Ack regardless: AIDA64 writes the next frame only once the marker
            // is cleared, and an unreadable frame is still consumed.
            _writer.TryWrite(BitmapOffset, ZeroMarker, out _);
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
