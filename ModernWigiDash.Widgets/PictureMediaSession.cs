namespace ModernWigiDash.Widgets;

/// <summary>
/// The picture widget's media session: what is installed (GIF frames, still
/// bitmap, per-frame durations, the current frame's advance tick), the
/// cross-thread retirement of replaced bitmaps, the async load pipeline
/// (versioning, the pending decode task), and the folder-scan state
/// (throttled rescan, file index, path-probe caches). The render tick reads
/// <see cref="CurrentFrame"/> and drives <see cref="Retirement"/>; the touch
/// tick calls <see cref="AdvanceCycle"/>; property changes and teardown
/// call <see cref="Reset"/>. The injected clock drives both the GIF advance
/// and the folder-rescan throttle (a test's FakeTimeProvider); the log sink
/// receives the decoder's refusal lines (the caller owns the vocabulary)
/// and the render-request sink is the host's wake-up for a fresh publish.
/// </summary>
internal sealed class PictureMediaSession
{
    private readonly TimeProvider _clock;
    private readonly Action<string> _log;
    private readonly Action _requestRender;
    private readonly Lock _mediaLock = new();
    private readonly RetiredBitmapSet _mediaRetirement = new();

    /// <summary>The media session over the injected clock, log sink, and render-request sink.</summary>
    public PictureMediaSession(TimeProvider clock, Action<string> log, Action requestRender)
    {
        _clock = clock;
        _log = log;
        _requestRender = requestRender;
    }

    /// <summary>
    /// The retired-bitmap set: the render thread disposes at the start of
    /// its pass; teardown drains the rest.
    /// </summary>
    public RetiredBitmapSet Retirement => _mediaRetirement;

    // Folder-scan state: the scanned list, the throttle latch, and the
    // tap-to-cycle index.
    private string[] _folderImages = [];
    private bool _folderScanned;
    private int _imageIndex;
    private DateTimeOffset _folderLastScan = DateTimeOffset.MinValue;
    private static readonly TimeSpan FolderRescanPeriod = TimeSpan.FromSeconds(30);

    // Last-probed-path existence caches: File/Directory.Exists per frame is
    // a syscall; the probe is re-done only when the path changes (Reset
    // clears both keys on ImagePath/SourceMode changes). The folder's file
    // list already rescans on its own 30 s throttle, so a folder added or
    // removed mid-run is picked up on the next rescan without a per-frame
    // disk hit.
    private string _lastProbePath = "";
    private bool _lastProbeExists;
    private string _lastDirProbePath = "";
    private bool _lastDirProbeExists;

    /// <summary>
    /// The file-existence verdict (cached per path; re-probed when the path
    /// changes).
    /// </summary>
    public bool FileExists(string path)
    {
        if (!string.Equals(path, _lastProbePath, StringComparison.Ordinal))
        {
            _lastProbePath = path;
            _lastProbeExists = File.Exists(path);
        }
        return _lastProbeExists;
    }

    /// <summary>
    /// The folder-existence verdict (cached per path; a rescan invalidates
    /// the probe).
    /// </summary>
    public bool DirectoryExists(string path)
    {
        if (!string.Equals(path, _lastDirProbePath, StringComparison.Ordinal))
        {
            _lastDirProbePath = path;
            _lastDirProbeExists = Directory.Exists(path);
        }
        return _lastDirProbeExists;
    }

    // Installed media under the media lock. Media is decoded on a background
    // thread by the MediaDecoder module (bytes in, installable
    // MediaDecodeResult out, never throws for malformed media) and published
    // atomically here; the render thread may be drawing a bitmap at publish
    // time, so replaced bitmaps are retired via the retirement set and
    // disposed only on the render thread (start of Render) or teardown —
    // never from the decode task.
    private SKBitmap? _staticBitmap;
    private SKBitmap[]? _gifFrames;
    private long[]? _gifFrameDurationsMs;
    private int _gifFrameIndex;
    private long _gifNextFrameTick;

    // Load pipeline: the tracked loaded path, the version stamp, and the
    // most recently started decode task.
    private string _loadedPath = "";
    private int _loadVersion;
    private Task? _pendingDecodeTask;

    /// <summary>
    /// Resolves the active image file for the source: file mode is the path
    /// itself; folder mode is the scanned list's current index (rescanned on
    /// first use and on a throttled cadence, so files added to a cycling
    /// folder while the app runs appear within one period, without a
    /// per-frame disk scan). A folder removed (or made unreadable) mid-run
    /// degrades to no images instead of throwing into the render tick — the
    /// next rescan or path change picks the folder back up.
    /// </summary>
    public string? GetActiveImageFile(string sourceMode, string path)
    {
        switch (PictureSourcePolicy.Resolve(sourceMode, FileExists(path), DirectoryExists(path)))
        {
            case PictureSourcePolicy.PictureSourceKind.File:
                return path;

            case PictureSourcePolicy.PictureSourceKind.Folder:
                if (!_folderScanned || _clock.GetUtcNow() - _folderLastScan >= FolderRescanPeriod)
                {
                    try
                    {
                        _folderImages = Directory.GetFiles(path, "*.*")
                            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                    }
                    catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
                    {
                        _folderImages = [];
                    }
                    _folderScanned = true;
                    _folderLastScan = _clock.GetUtcNow();
                    // The rescan is the folder's existence verdict too: a
                    // folder deleted mid-run must stop promising a
                    // tap-to-cycle within one rescan period (the CanCycle
                    // promise reads the probe), not only on the next path
                    // edit. Invalidating the probe makes the next
                    // DirectoryExists re-stat, then re-caches for the
                    // following period.
                    _lastDirProbePath = "";
                }

                if (_folderImages.Length > 0)
                {
                    _imageIndex %= _folderImages.Length;
                    return _folderImages[_imageIndex];
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Tracks the active load: when the path changed since the last tracked
    /// load, starts a fresh async decode (versioned; a stale decode
    /// discards its buffers).
    /// </summary>
    public void EnsureMediaLoad(string path)
    {
        if (string.Equals(path, _loadedPath, StringComparison.Ordinal))
            return;
        _loadedPath = path;
        int version = ++_loadVersion;
        _pendingDecodeTask = Task.Run(() => DecodeMediaAsync(path, version));
    }

    private async Task DecodeMediaAsync(string path, int version)
    {
        byte[] data;
        try
        {
            // Snapshot the file bytes so no file handle outlives this task:
            // decodes read from memory, the source file can be replaced or
            // the tests can delete it without racing an open handle.
            data = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        catch
        {
            // The file vanished mid-read: nothing to install, the render
            // tick re-probes and re-loads.
            return;
        }

        // The decode itself (bomb caps, delta compositing, the malformed
        // media rule) is the MediaDecoder module: it never throws and hands
        // back an installable result or None with its buffers disposed.
        MediaDecodeResult decoded = MediaDecoder.Decode(data, _log);

        if (version != _loadVersion || !string.Equals(path, _loadedPath, StringComparison.Ordinal))
        {
            decoded.DisposeMedia();
            return;
        }

        PublishMedia(decoded.Frames, decoded.Durations, decoded.Still);
    }

    /// <summary>
    /// Reads the installed media under the media lock and advances the GIF
    /// frame when the injected clock passes the frame's tick. Returns the
    /// draw state: the frames array (animated when Length &gt; 1), the
    /// still, and the index into frames to draw. Even if a publish swaps
    /// the frames array mid-draw, the index stays valid against the array
    /// it was computed on: the replaced array (and its bitmaps) stays alive
    /// until the next retirement pass.
    /// </summary>
    public (SKBitmap[]? Frames, SKBitmap? Still, int FrameIndex) CurrentFrame()
    {
        lock (_mediaLock)
        {
            int frameIndex = _gifFrameIndex;
            if (_gifFrames is { Length: > 1 } && _clock.GetTimestamp() >= _gifNextFrameTick)
            {
                frameIndex = (_gifFrameIndex + 1) % _gifFrames.Length;
                _gifFrameIndex = frameIndex;
                _gifNextFrameTick = _clock.GetTimestamp() + TimeSpan.FromMilliseconds(FrameDurationMs(_gifFrameDurationsMs, frameIndex)).Ticks;
            }
            return (_gifFrames, _staticBitmap, frameIndex);
        }
    }

    /// <summary>
    /// Atomically installs decoded media (from the background task), retires
    /// whatever the render thread might still be drawing, and wakes the
    /// host's render loop.
    /// </summary>
    private void PublishMedia(SKBitmap[]? gifFrames, long[]? durations, SKBitmap? staticBitmap)
    {
        lock (_mediaLock)
        {
            _mediaRetirement.RetireAll(_gifFrames);
            _mediaRetirement.Retire(_staticBitmap);

            _gifFrames = gifFrames;
            _gifFrameDurationsMs = durations;
            _staticBitmap = staticBitmap;
            _gifFrameIndex = 0;
            _gifNextFrameTick = _clock.GetTimestamp() + TimeSpan.FromMilliseconds(FrameDurationMs(durations, 0)).Ticks;
        }

        _requestRender();
    }

    /// <summary>
    /// The tap-to-cycle decision: advance to the next image of an actually
    /// cycling folder (the policy's cycle verdict plus the scanned-list
    /// guard — a folder with no images yet has nothing to cycle) and clear
    /// the tracked load so the next render reloads. Returns whether the
    /// cycle advanced (the host requests a render then).
    /// </summary>
    public bool AdvanceCycle(string sourceMode, string path)
    {
        if (_folderImages.Length > 0 &&
            PictureSourcePolicy.CanCycle(sourceMode, FileExists(path), DirectoryExists(path)))
        {
            _imageIndex = PictureSourcePolicy.NextImageIndex(_imageIndex, _folderImages.Length);
            _loadedPath = "";
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resets the installed media, the folder scan, and the probe caches
    /// (property change / teardown). The draw owner's clip invalidation
    /// stays with the host (its canvas). Retired media is disposed promptly
    /// because this runs on the UI thread where nothing is mid-draw.
    /// </summary>
    public void Reset()
    {
        _loadVersion++;
        _loadedPath = "";
        lock (_mediaLock)
        {
            _mediaRetirement.RetireAll(_gifFrames);
            _gifFrames = null;
            _mediaRetirement.Retire(_staticBitmap);
            _staticBitmap = null;
            _gifFrameDurationsMs = null;
            _gifFrameIndex = 0;
        }
        _folderImages = [];
        _folderScanned = false;
        _imageIndex = 0;
        _lastProbePath = "";
        _lastDirProbePath = "";
        _mediaRetirement.DisposeRetired();
    }

    private static long FrameDurationMs(long[]? durations, int frameIndex)
        => durations is { Length: > 0 } ? durations[Math.Min(frameIndex, durations.Length - 1)] : 100L;

    // --- Test seams (the widget's test surface drives this face) ---

    /// <summary>The most recently started decode task (tests await it to make the async decode deterministic).</summary>
    internal Task? PendingDecodeTaskForTest => _pendingDecodeTask;

    /// <summary>The installed media state under the media lock (null when nothing is installed — the placeholder state).</summary>
    internal (SKBitmap[]? Frames, SKBitmap? Still) InstalledMediaForTest
    {
        get
        {
            lock (_mediaLock)
            {
                return (_gifFrames, _staticBitmap);
            }
        }
    }

    /// <summary>The current GIF frame index under the media lock, so the clock-driven advance is assertable without pixel sampling.</summary>
    internal int GifFrameIndexForTest
    {
        get
        {
            lock (_mediaLock)
            {
                return _gifFrameIndex;
            }
        }
    }

    /// <summary>The currently listed folder images (test seam).</summary>
    internal IReadOnlyList<string> FolderImagesForTest => _folderImages;
}
