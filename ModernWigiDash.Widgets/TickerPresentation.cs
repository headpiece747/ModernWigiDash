namespace ModernWigiDash.Widgets;

/// <summary>
/// Pure display rules for the ticker widget: the price-decimal tier rule, the
/// formatted price, and the display-label fallback order — previously inline
/// in the widget.
/// </summary>
public static class TickerPresentation
{
    /// <summary>
    /// The decimal count for a price: an explicit choice wins; otherwise the
    /// price tier decides (>=100: 2, >=1: 4, >=0.01: 6, else 8) so small
    /// crypto prices never collapse to zero.
    /// </summary>
    public static int DecimalsFor(string decimalsChoice, decimal rawPrice)
        => decimalsChoice switch
        {
            "2" => 2,
            "4" => 4,
            "6" => 6,
            "8" => 8,
            _ when rawPrice >= 100 => 2,
            _ when rawPrice >= 1 => 4,
            _ when rawPrice >= 0.01m => 6,
            _ => 8
        };

    /// <summary>The display price with the currency symbol.</summary>
    public static string FormatPrice(decimal rawPrice, string decimalsChoice, string currencySymbol = "$")
        => currencySymbol + rawPrice.ToString("N" + DecimalsFor(decimalsChoice, rawPrice));

    /// <summary>
    /// The widget's label: the user's display name wins, then an FX pair's
    /// "BASE / QUOTE", then the normalized symbol.
    /// </summary>
    public static string DisplayLabel(string displayName, string? fxLabel, string normalizedSymbol)
        => !string.IsNullOrEmpty(displayName) ? displayName : fxLabel ?? normalizedSymbol;
}
