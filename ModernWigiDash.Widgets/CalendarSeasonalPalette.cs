namespace ModernWigiDash.Widgets;

/// <summary>
/// A color scheme for a calendar month, matching the graphic poster reference.
/// </summary>
/// <param name="Background">The background color of the month card.</param>
/// <param name="Text">The primary typography color.</param>
/// <param name="Accent">The secondary accent or badge highlight color.</param>
/// <param name="MonthCode">The 3-letter month code (e.g. "SEP").</param>
/// <param name="FullMonthName">The full month name (e.g. "September").</param>
public readonly record struct CalendarSeasonalPalette(
    SKColor Background,
    SKColor Text,
    SKColor Accent,
    string MonthCode,
    string FullMonthName);

/// <summary>
/// Curated monthly color schemes from the 2026 editorial poster reference,
/// with resolution logic for seasonal vs custom themes.
/// </summary>
public static class CalendarSeasonalPalettes
{
    private static readonly CalendarSeasonalPalette[] Palettes =
    [
        // Jan: Midnight Navy
        new(new SKColor(20, 23, 56), SKColors.White, new SKColor(99, 102, 241), "JAN", "January"),
        // Feb: Vibrant Magenta
        new(new SKColor(229, 62, 122), SKColors.White, new SKColor(254, 232, 240), "FEB", "February"),
        // Mar: Forest Teal
        new(new SKColor(20, 54, 46), SKColors.White, new SKColor(16, 185, 129), "MAR", "March"),
        // Apr: Canary Yellow (Dark text for contrast)
        new(new SKColor(245, 200, 0), new SKColor(30, 41, 59), new SKColor(30, 41, 59), "APR", "April"),
        // May: Royal Indigo
        new(new SKColor(41, 59, 134), SKColors.White, new SKColor(147, 197, 253), "MAY", "May"),
        // Jun: Deep Cobalt
        new(new SKColor(10, 45, 117), SKColors.White, new SKColor(96, 165, 250), "JUN", "June"),
        // Jul: Deep Wine
        new(new SKColor(71, 19, 34), SKColors.White, new SKColor(251, 113, 133), "JUL", "July"),
        // Aug: Mint Sage (Dark green text)
        new(new SKColor(217, 242, 199), new SKColor(6, 78, 59), new SKColor(4, 120, 87), "AUG", "August"),
        // Sep: Warm Tangerine / Coral
        new(new SKColor(255, 77, 45), SKColors.White, new SKColor(254, 205, 211), "SEP", "September"),
        // Oct: Golden Ochre (Dark text)
        new(new SKColor(234, 168, 18), new SKColor(30, 41, 59), new SKColor(30, 41, 59), "OCT", "October"),
        // Nov: Electric Azure
        new(new SKColor(0, 92, 185), SKColors.White, new SKColor(191, 219, 254), "NOV", "November"),
        // Dec: Warm Crimson / Berry
        new(new SKColor(139, 30, 63), new SKColor(255, 245, 245), new SKColor(254, 226, 226), "DEC", "December"),
    ];

    /// <summary>
    /// Gets the curated seasonal palette for a specific month (1 = Jan, 12 = Dec).
    /// </summary>
    /// <param name="month">1-based month number.</param>
    /// <returns>The seasonal palette for that month.</returns>
    public static CalendarSeasonalPalette GetForMonth(int month)
    {
        int idx = Math.Clamp(month, 1, 12) - 1;
        return Palettes[idx];
    }

    /// <summary>
    /// Resolves the active palette for a given date and theme mode.
    /// </summary>
    /// <param name="viewDate">The date being viewed.</param>
    /// <param name="themeMode">The theme mode ("Seasonal" or "Custom").</param>
    /// <param name="customAccent">The custom accent hex color.</param>
    /// <param name="customText">The custom text hex color.</param>
    /// <returns>The resolved color palette.</returns>
    public static CalendarSeasonalPalette Resolve(DateTime viewDate, string? themeMode, string customAccent, string customText)
    {
        if (string.Equals(themeMode, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            SKColor text = SKColor.TryParse(customText, out var t) ? t : SKColors.White;
            SKColor accent = SKColor.TryParse(customAccent, out var a) ? a : new SKColor(79, 140, 255);
            // Deep dark slate background for custom mode
            SKColor bg = new(18, 20, 29);
            string[] months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
            string[] full = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
            int m = Math.Clamp(viewDate.Month, 1, 12) - 1;
            return new CalendarSeasonalPalette(bg, text, accent, months[m], full[m]);
        }

        return GetForMonth(viewDate.Month);
    }
}
