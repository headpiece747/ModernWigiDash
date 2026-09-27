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
    /// <param name="info">The last known record, or null when there is none.</param>
    /// <param name="now">The reader's current time. It is a PARAMETER, not a
    /// clock read here, because the record's own stamp comes from the store's
    /// injected clock: with the decision made against an ambient clock, a test
    /// that moved the store's clock would move the stamp and not the verdict, and
    /// nothing in the suite could see it. The single read site
    /// (<c>CryptoStockTickerWidget.Render</c>) names the now it decides against.
    /// </param>
    public static bool IsStale(PriceInfo? info, DateTime now)
        => info is null || (now - info.Timestamp).TotalSeconds > PriceInfo.FreshnessSeconds;

    /// <summary>The stale badge's freshness dot: the bullet prefix ensures a
    /// stale last-known value is never mistaken for live data.</summary>
    public static string BadgeText(string changeBadge, bool isStale)
        => isStale ? $"• {changeBadge}" : changeBadge;

    /// <summary>The stale badge's alpha — the neutral gray the renderer
    /// applies to the change text when the price is stale.</summary>
    public static byte StaleBadgeAlpha => 120;
}
