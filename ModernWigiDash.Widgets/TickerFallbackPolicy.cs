namespace ModernWigiDash.Widgets;

/// <summary>
/// The ticker's read-side fallback-seed cadence (Widgets): the live feed can
/// return nothing while the REST fallback is still warming up, and the widget
/// must seed/re-seed at most once per 15s. Stateful on the widget's clock
/// seam (read lazily, so tests that swap <c>Clock</c> after construction
/// still drive it), so the cadence is assertable without pixels — Render only
/// asks "may I seed now?".
/// </summary>
internal sealed class TickerFallbackPolicy(Func<TimeProvider> clockProvider)
{
    private readonly Func<TimeProvider> _clockProvider = clockProvider;
    private DateTime _lastSeed = DateTime.MinValue;

    /// <summary>True when a fallback seed may fire now (not within the 15s
    /// window); a granted call starts a fresh window.</summary>
    public bool TryBeginFallback()
    {
        TimeProvider clock = _clockProvider();
        if ((clock.GetUtcNow().UtcDateTime - _lastSeed).TotalSeconds < 15) return false;
        _lastSeed = clock.GetUtcNow().UtcDateTime;
        return true;
    }
}
