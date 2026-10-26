namespace ModernWigiDash.Widgets;

/// <summary>
/// One single-slot memo (Widgets): caches one computed value keyed by a
/// value-equality key, re-computing only when the key changes — the shared
/// shape of the widgets' hand-rolled memo fields (the ticker's formatted
/// price, the Twitch channel badge and status line), so the pattern is
/// implemented once and tested instead of restated per widget. The
/// <see cref="IEqualityComparer{TKey}"/> ctor covers the two non-value
/// key shapes: tolerance keys (a sub-pixel scroll epsilon) and
/// reference-identity keys (a fresh snapshot record is a content change).
/// </summary>
public sealed class MemoSlot<TKey, TValue>
{
    private readonly IEqualityComparer<TKey> _comparer;
    private TKey _key = default!;
    private TValue _value = default!;
    private bool _hasValue;
    private int _recomputes;

    /// <summary>The value-equality slot (the shared default key equality).</summary>
    public MemoSlot()
        : this(EqualityComparer<TKey>.Default)
    {
    }

    /// <summary>
    /// The custom-key-equality slot: the tolerance-key shape (the Steam grid's
    /// sub-pixel scroll epsilon) and the reference-identity shape (the
    /// frame-time display memo, where a fresh snapshot record is a content
    /// change) are the two non-value-equality key shapes the canvases need.
    /// </summary>
    public MemoSlot(IEqualityComparer<TKey> comparer)
    {
        _comparer = comparer;
    }

    /// <summary>
    /// The closure-free read: true when <paramref name="key"/> equals the last
    /// key (serving the cached value), false when the caller must compute a
    /// value and <see cref="Set"/> it. The compute is the caller's inline job,
    /// so a hit allocates nothing (no delegate, no closure) — the zero-alloc
    /// per-tick contract the canvases depend on.
    /// </summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_hasValue && _comparer.Equals(_key, key))
        {
            value = _value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Stores a value the caller computed after a <see cref="TryGet"/> miss.
    /// A store counts as a recompute (the hit path does not).
    /// </summary>
    public void Set(TKey key, TValue value)
    {
        _key = key;
        _value = value;
        _hasValue = true;
        _recomputes++;
    }

    /// <summary>The last computed/stored value, or default when nothing has been
    /// computed yet. Test seam: lets a consumer observe the memoized result
    /// without re-driving the compute.</summary>
    public TValue? LastValue => _hasValue ? _value : default;

    /// <summary>How many times the compute delegate actually ran (a cache hit
    /// does not increment this). Test seam: distinguishes a memo hit from a
    /// recompute that happens to produce an equal value.</summary>
    public int RecomputeCount => _recomputes;

    /// <summary>Clears the cached state: the next <see cref="TryGet"/> misses
    /// even for the last key (the canvas Invalidate path after a rehydrated
    /// instance re-binds its sources).</summary>
    public void Invalidate()
    {
        _key = default!;
        _value = default!;
        _hasValue = false;
    }
}
