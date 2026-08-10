namespace ModernWigiDash.Widgets;

/// <summary>
/// One ticker widget's feed-identity ownership (the IWidgetIconGrab image):
/// diffs the tracked identity against the last one, releases the old claim,
/// subscribes the new one, and seeds the fallback fetch. The widget keeps
/// only its Symbol/AssetType properties and calls <see cref="Track"/> /
/// <see cref="Untrack"/> — the subscription bookkeeping is testable without a
/// widget instance.
/// </summary>
internal sealed class FeedSubscription
{
    private readonly Action _seedFallback;
    private PriceFeedManager? _feed;
    private string? _trackedSymbol;
    private AssetKind _trackedKind;

    /// <param name="seedFallback">Fires right after a new subscription so the
    /// first price arrives before the live feed's first tick (the widget wires
    /// its one-shot fallback fetch here).</param>
    public FeedSubscription(Action seedFallback)
    {
        _seedFallback = seedFallback;
    }

    /// <summary>
    /// Reconciles the tracked identity with the requested one: an unchanged
    /// identity is a no-op (the shared manager's refcount keeps N widgets on
    /// one symbol alive); a changed identity unsubscribes the old symbol and
    /// subscribes the new one. A blank symbol never subscribes — and never
    /// triggers the shared manager's shutdown on dispose.
    /// </summary>
    public void Track(string? symbol, AssetKind kind, PriceFeedManager feed)
    {
        if (_trackedSymbol == symbol && _trackedKind == kind) return;

        if (_trackedSymbol != null)
        {
            _feed?.Unsubscribe(_trackedSymbol, _trackedKind);
        }
        _feed = feed;
        _trackedSymbol = string.IsNullOrWhiteSpace(symbol) ? null : symbol;
        _trackedKind = kind;
        if (string.IsNullOrWhiteSpace(symbol)) return;

        feed.Subscribe(symbol, kind);
        _seedFallback();
    }

    /// <summary>Releases the tracked subscription (widget removed from the canvas).</summary>
    public void Untrack()
    {
        if (_trackedSymbol == null) return;

        _feed?.Unsubscribe(_trackedSymbol, _trackedKind);
        _trackedSymbol = null;
    }
}
