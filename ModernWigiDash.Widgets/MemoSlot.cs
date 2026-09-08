namespace ModernWigiDash.Widgets;

/// <summary>
/// One single-slot memo (Widgets): caches one computed value keyed by a
/// value-equality key, re-computing only when the key changes — the shared
/// shape of the widgets' hand-rolled memo fields (the ticker's formatted
/// price, the Twitch channel badge and status line), so the pattern is
/// implemented once and tested instead of restated per widget.
/// </summary>
public sealed class MemoSlot<TKey, TValue>
{
    private TKey _key = default!;
    private TValue _value = default!;
    private bool _hasValue;
    private int _recomputes;

    /// <summary>Returns the cached value when <paramref name="key"/> equals
    /// the last key, else computes, stores, and returns it.</summary>
    public TValue GetOrCompute(TKey key, Func<TValue> compute)
    {
        if (_hasValue && EqualityComparer<TKey>.Default.Equals(_key, key))
        {
            return _value;
        }

        _key = key;
        _value = compute();
        _hasValue = true;
        _recomputes++;
        return _value;
    }

    /// <summary>The last computed/stored value, or default when nothing has been
    /// computed yet. Test seam: lets a consumer observe the memoized result
    /// without re-driving the compute.</summary>
    public TValue? LastValue => _hasValue ? _value : default;

    /// <summary>How many times the compute delegate actually ran (a cache hit
    /// does not increment this). Test seam: distinguishes a memo hit from a
    /// recompute that happens to produce an equal value.</summary>
    public int RecomputeCount => _recomputes;
}
