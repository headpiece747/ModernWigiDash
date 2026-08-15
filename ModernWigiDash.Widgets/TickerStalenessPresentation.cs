namespace ModernWigiDash.Widgets;

/// <summary>
/// The ticker's stale-price display rules (Widgets): given the last known
/// price record, whether the price reads stale and the stale badge's render
/// shape — pure and assertable; the widget's render is a thin adapter over
/// it (the exact brush color stays in the renderer, which owns the theme).
/// </summary>
public static class TickerStalenessPresentation
{
    /// <summary>A missing record reads stale — no price must look live.</summary>
    public static bool IsStale(PriceInfo? info) => info?.IsStale ?? true;

    /// <summary>The stale badge's freshness dot: the bullet prefix ensures a
    /// stale last-known value is never mistaken for live data.</summary>
    public static string BadgeText(string changeBadge, bool isStale)
        => isStale ? $"• {changeBadge}" : changeBadge;

    /// <summary>The stale badge's alpha — the neutral gray the renderer
    /// applies to the change text when the price is stale.</summary>
    public static byte StaleBadgeAlpha => 120;
}
