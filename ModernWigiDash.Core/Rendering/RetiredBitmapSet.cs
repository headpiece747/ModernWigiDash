using SkiaSharp;

namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// Owns the cross-thread bitmap retirement policy: bitmaps replaced from
/// background threads are retired here and disposed only when the owner calls
/// <see cref="DisposeRetired"/> on its render thread — a bitmap is never freed
/// while the canvas could still be drawing it (sk_image_new_from_bitmap UAF,
/// 0xc0000005). Retire calls are thread-safe; the dispose passes run on the
/// caller's thread, so the render thread controls the moment of free.
/// </summary>
public sealed class RetiredBitmapSet
{
    private static readonly List<SKBitmap> Empty = [];

    private readonly Lock _lock = new();
    private readonly List<SKBitmap> _pending = [];

    /// <summary>Retires a bitmap for later disposal. Null is ignored.</summary>
    public void Retire(SKBitmap? bitmap)
    {
        if (bitmap is null) return;
        lock (_lock)
        {
            _pending.Add(bitmap);
        }
    }

    /// <summary>Retires a batch of bitmaps; nulls in the batch are ignored.</summary>
    public void RetireAll(IEnumerable<SKBitmap?>? bitmaps)
    {
        if (bitmaps is null) return;
        lock (_lock)
        {
            _pending.AddRange(bitmaps.OfType<SKBitmap>());
        }
    }

    /// <summary>
    /// Drains and disposes every retired bitmap. Call on the render thread at
    /// the start of a render pass (or on widget teardown), never from the
    /// background thread that replaced the bitmaps. Safe to call concurrently
    /// with Retire: bitmaps retired mid-drain stay pending for the next call.
    /// </summary>
    public void DisposeRetired()
    {
        var drained = Drain();
        foreach (var bitmap in drained)
        {
            bitmap.Dispose();
        }
    }

    /// <summary>Teardown: drains and disposes every retired bitmap.</summary>
    public void DisposeAll() => DisposeRetired();

    /// <summary>Number of bitmaps currently awaiting disposal (test seam).</summary>
    internal int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    private List<SKBitmap> Drain()
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return Empty;
            var drained = _pending;
            _pending.Clear();
            return drained;
        }
    }
}
