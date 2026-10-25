namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The bounded 24-slot LRU cache of downscaled Steam poster textures.
/// Decodes run on a background thread straight into the target-dimension
/// <c>Rgb565</c> bitmap (the raw 600×900×4 source is never materialized —
/// a direct <c>SKBitmap.Decode</c> into the smaller <see cref="SKImageInfo"/>
/// scales inside the decoder), so the worst-case footprint is
/// 24 × 180×270×2 ≈ 2.3 MB, inside the widget's 5 MB RAM budget. The
/// render thread only ever LOOKS UP a published bitmap (never decodes),
/// and evicted textures are retired through the house
/// <see cref="RetiredBitmapSet"/>: a bitmap the 30 FPS canvas may still be
/// drawing is freed on the render thread's next drain, never by the
/// background thread that replaced it (the ArtworkLoader UAF rule).
/// </summary>
internal sealed class SteamPosterCache : IDisposable
{
    /// <summary>The cache capacity: 24 downscaled textures (the PRD's LRU
    /// limit).</summary>
    public const int Capacity = 24;

    private readonly System.Threading.Lock _gate = new();
    private readonly Dictionary<uint, CacheSlot> _slots = new();
    private readonly LinkedList<uint> _recency = new(); // front = most recently used
    private readonly RetiredBitmapSet _retired = new();
    private bool _disposed;

    /// <summary>The retirement set (test seam: observes the pending-drain state).</summary>
    internal RetiredBitmapSet RetirementSet => _retired;

    /// <summary>The number of textures currently cached.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _slots.Count;
        }
    }

    /// <summary>
    /// The render-thread lookup: the published bitmap for an app id, or null
    /// (the renderer draws its placeholder). A hit refreshes recency. This
    /// is the ONLY render-path entry point: it never decodes, never reads
    /// disk, and never blocks on a decode in flight.
    /// </summary>
    public bool TryGet(uint appId, out SKBitmap? bitmap)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(appId, out CacheSlot? slot))
            {
                Touch(slot);
                bitmap = slot.Bitmap;
                return true;
            }

            bitmap = null;
            return false;
        }
    }

    /// <summary>
    /// Decodes the poster bytes into the target-dimension Rgb565 bitmap and
    /// stores it (evicting the least-recently-used texture at capacity).
    /// Called on a background thread; a null/failed decode stores nothing
    /// (the caller's next refresh retries). The decode is a scaled one — the
    /// source is never decoded at full resolution.
    /// </summary>
    public void Store(uint appId, byte[] data, int maxWidth, int maxHeight)
    {
        SKBitmap? decoded = DecodeScaled(data, maxWidth, maxHeight);
        if (decoded is null)
            return;

        lock (_gate)
        {
            // Re-check inside the lock: a decode that started before Dispose
            // must not insert into the cleared cache (which would leak the
            // bitmap, since the disposal pass has already run).
            if (_disposed)
            {
                decoded.Dispose();
                return;
            }

            if (_slots.TryGetValue(appId, out CacheSlot? existing))
            {
                _retired.Retire(existing.Bitmap);
                _recency.Remove(existing.Node);
                _slots.Remove(appId);
            }

            while (_slots.Count >= Capacity)
            {
                uint evicted = _recency.Last!.Value;
                _recency.RemoveLast();
                if (_slots.Remove(evicted, out CacheSlot? evictedSlot))
                    _retired.Retire(evictedSlot.Bitmap);
            }

            var node = _recency.AddFirst(appId);
            _slots[appId] = new CacheSlot(decoded, node);
        }
    }

    /// <summary>
    /// Drains and disposes the retired textures. Call on the render thread
    /// at the start of a render pass (or on teardown), never from the
    /// background decode thread.
    /// </summary>
    public void DisposeRetired() => _retired.DisposeRetired();

    /// <summary>Teardown: disposes every cached texture and the pending retirements.</summary>
    public void Dispose()
    {
        List<SKBitmap> cached;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            cached = _slots.Values.Select(s => s.Bitmap).ToList();
            _slots.Clear();
            _recency.Clear();
        }

        foreach (var bitmap in cached)
            bitmap.Dispose();
        _retired.DisposeAll();
    }

    /// <summary>
    /// Test seam: publishes a bitmap under an app id without a decode (the
    /// capacity/eviction tests drive the LRU without image bytes).
    /// </summary>
    internal void InsertForTest(uint appId, SKBitmap bitmap)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(appId, out CacheSlot? existing))
            {
                _retired.Retire(existing.Bitmap);
                _recency.Remove(existing.Node);
                _slots.Remove(appId);
            }

            while (_slots.Count >= Capacity)
            {
                uint evicted = _recency.Last!.Value;
                _recency.RemoveLast();
                if (_slots.Remove(evicted, out CacheSlot? evictedSlot))
                    _retired.Retire(evictedSlot.Bitmap);
            }

            var node = _recency.AddFirst(appId);
            _slots[appId] = new CacheSlot(bitmap, node);
        }
    }

    /// <summary>
    /// The scaled decode: the source's codec reports its native dimensions,
    /// the target size is the largest that fits the bounds (no upscaling),
    /// and the result lands in the target-dimension Rgb565 bitmap. Skia's
    /// JPEG decoder scales natively only at 1/2^n ratios, so the direct
    /// one-pass decode is the fast path and an arbitrary ratio falls back to
    /// a source-size decode plus a resample (per-decode, never per-frame).
    /// </summary>
    internal static SKBitmap? DecodeScaled(byte[] data, int maxWidth, int maxHeight)
    {
        if (data.Length == 0)
            return null;

        using SKData skData = SKData.CreateCopy(data);
        using SKCodec? codec = SKCodec.Create(skData);
        if (codec is null)
            return null; // not an image.

        SKSizeI source = codec.Info.Size;
        if (source.Width <= 0 || source.Height <= 0)
            return null;

        float scale = Math.Min(1f, Math.Min(maxWidth / (float)source.Width, maxHeight / (float)source.Height));
        int width = Math.Max(1, (int)MathF.Round(source.Width * scale));
        int height = Math.Max(1, (int)MathF.Round(source.Height * scale));

        var info = new SKImageInfo(width, height, SKColorType.Rgb565, SKAlphaType.Opaque);

        SKBitmap? direct = SKBitmap.Decode(data, info);
        if (direct is not null)
            return direct;

        var sourceInfo = new SKImageInfo(source.Width, source.Height, SKColorType.Rgb565, SKAlphaType.Opaque);
        using SKBitmap? full = SKBitmap.Decode(data, sourceInfo);
        if (full is null)
            return null;

        // SKFilterQuality is not in the 4.152 compile surface (the
        // SKSamplingOptions struct is the 4.x resample control; Linear is
        // the bilinear mode).
        return full.Resize(info, new SKSamplingOptions(SKFilterMode.Linear));
    }

    private void Touch(CacheSlot slot)
    {
        _recency.Remove(slot.Node);
        _recency.AddFirst(slot.Node);
    }

    private sealed record CacheSlot(SKBitmap Bitmap, LinkedListNode<uint> Node);
}
