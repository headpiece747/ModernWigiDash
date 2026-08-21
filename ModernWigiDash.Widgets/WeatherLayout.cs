using System.Runtime.InteropServices;

namespace ModernWigiDash.Widgets;

/// <summary>The header tap zones of the Weather widget, in precedence order.</summary>
internal enum WeatherHeaderAction
{
    /// <summary>The point is not on any header control.</summary>
    None,

    /// <summary>The unit-toggle badge — tap toggles the unit system.</summary>
    ToggleUnit,

    /// <summary>The left header zone — tap cycles the layout mode.</summary>
    CycleLayout,
}

/// <summary>
/// The Weather widget's header geometry, computed once per frame from the
/// placement bounds and the same scale factors the render path uses, so the
/// drawn badge and the touch targets can never drift apart.
/// </summary>
// The sequential layout is pinned by MA0008: every field (four floats + the
// blittable SKRect) is blittable, and a deterministic layout is what makes
// the per-frame header record cheap to copy and layout-stable.
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct WeatherHeaderLayout(
    float HeaderHeight,
    SKRect BadgeRect,
    float HeaderTextY,
    float TitleFontSize,
    float Pad);

/// <summary>
/// The Weather widget's layout modes, in display order. The inspector property
/// stays a string; <see cref="WeatherLayout.ParseMode"/> is the single
/// string→mode mapping site.
/// </summary>
internal enum WeatherLayoutMode
{
    Detailed,
    DailyForecast,
    HourlyForecast,
    CurrentOnly,
    Compact
}

/// <summary>
/// Pure layout rules for the Weather widget: the scale factors, the header
/// geometry (title, unit badge, content padding), the header touch zones, the
/// layout-mode cycle (over the mode catalog), and the hero/pill shrink rules.
/// Moved out of the widget's render and touch paths so the drawn geometry and
/// the tap targets share one source of truth.
/// </summary>
internal static class WeatherLayout
{
    /// <summary>The widget's design-space dimensions — the scale base for both render and touch.</summary>
    public const float DesignWidth = 406f;

    /// <summary>The widget's design-space HEIGHT: the scale base for render and touch.</summary>
    public const float DesignHeight = 296f;

    /// <summary>The default layout mode — the single source for the property default and the cycle rule.</summary>
    public const string DefaultLayoutMode = "Detailed";

    /// <summary>The default mode (the <see cref="DefaultLayoutMode"/> entry of
    /// the catalog): the render's fallback and the cycle's home.</summary>
    public static WeatherLayoutMode DefaultMode => WeatherLayoutMode.Detailed;

    /// <summary>One entry of the mode catalog: the mode and its persisted/display name.</summary>
    public sealed record ModeEntry(WeatherLayoutMode Mode, string Name);

    /// <summary>
    /// The widget's ONE layout-mode catalog: cycle order and display names.
    /// The inspector property's <c>[WidgetProperty]</c> choice array is a
    /// compile-time LITERAL copy of this list (attributes cannot bind a
    /// runtime value) — the attribute's lockstep test keeps the two in
    /// agreement, so a renamed or hand-edited mode name fails a pin instead
    /// of surfacing at runtime as the default-mode fallback. Static readonly:
    /// the 30 FPS render path must not allocate the table per frame.
    /// </summary>
    public static IReadOnlyList<ModeEntry> Modes { get; } = [
        new(WeatherLayoutMode.Detailed, DefaultLayoutMode),
        new(WeatherLayoutMode.DailyForecast, "Daily Forecast"),
        new(WeatherLayoutMode.HourlyForecast, "Hourly Forecast"),
        new(WeatherLayoutMode.CurrentOnly, "Current Only"),
        new(WeatherLayoutMode.Compact, "Compact"),
    ];

    /// <summary>The placement → scale factors: X scale, Y scale, and the uniform min.</summary>
    public static (float Sx, float Sy, float S) Scale(SKRect bounds)
        => (bounds.Width / DesignWidth, bounds.Height / DesignHeight,
            Math.Min(bounds.Width / DesignWidth, bounds.Height / DesignHeight));

    /// <summary>
    /// The header geometry for a placement: the header band height, the unit
    /// badge's drawn rect, the title baseline, the title font size, and the
    /// content padding. The badge rect is the single tap target for the unit
    /// toggle — the render and touch paths both consume this record.
    /// </summary>
    public static WeatherHeaderLayout ComputeHeader(SKRect bounds, float s, float sy)
    {
        float pad = Math.Clamp(14f * s, 8f, 32f);
        float headerHeight = Math.Clamp(44f * sy, 24f, 90f);
        float badgeWidth = Math.Clamp(54f * s, 30f, 100f);
        float badgeHeight = Math.Clamp(26f * sy, 16f, 50f);
        float badgeTop = bounds.Top + (headerHeight - badgeHeight) / 2f;
        SKRect badgeRect = new(bounds.Right - pad - badgeWidth, badgeTop, bounds.Right - pad, badgeTop + badgeHeight);
        return new WeatherHeaderLayout(
            headerHeight,
            badgeRect,
            bounds.Top + headerHeight * 0.65f,
            Math.Clamp(24f * s, 12f, 44f),
            pad);
    }

    /// <summary>
    /// Hit-tests a touch point against the header controls, in the widget's
    /// precedence order: the unit badge rect first, then the left mode-cycle
    /// zone (the header band left of 140px of X scale). Anything else reads None.
    /// </summary>
    public static WeatherHeaderAction GetHeaderAction(SKRect bounds, SKPoint point, float s, float sy)
    {
        var header = ComputeHeader(bounds, s, sy);
        if (header.BadgeRect.Contains(point)) return WeatherHeaderAction.ToggleUnit;
        float sx = bounds.Width / DesignWidth;
        if (point.Y < header.HeaderHeight && point.X < 140f * sx) return WeatherHeaderAction.CycleLayout;
        return WeatherHeaderAction.None;
    }

    /// <summary>
    /// The single string→mode mapping site (the inspector property stays a
    /// string): every catalog name maps to its mode; unknown values fall back
    /// to <see cref="DefaultMode"/> — the property default.
    /// </summary>
    internal static WeatherLayoutMode ParseMode(string? mode)
        => Modes.FirstOrDefault(entry => string.Equals(entry.Name, mode, StringComparison.Ordinal))?.Mode ?? DefaultMode;

    /// <summary>
    /// The tap-cycle rule: walk the catalog in order and wrap to the home.
    /// The wrap (and any out-of-range value) resets to the default — the same
    /// default <see cref="ParseMode"/> falls back to for unknown strings.
    /// </summary>
    internal static WeatherLayoutMode NextMode(WeatherLayoutMode mode)
    {
        IReadOnlyList<ModeEntry> entries = Modes;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Mode == mode)
            {
                return entries[(i + 1) % entries.Count].Mode;
            }
        }

        return DefaultMode;
    }

    /// <summary>
    /// The tap-cycle rule for the persisted string value: a known mode string
    /// advances through the cycle; an unknown value (a hand-edited profile)
    /// parses to the default and the cycle must LAND on that default — not
    /// advance past it — so garbage resets the widget instead of stepping it.
    /// </summary>
    internal static WeatherLayoutMode NextMode(string? mode)
    {
        WeatherLayoutMode parsed = ParseMode(mode);
        return string.Equals(mode, DisplayName(parsed), StringComparison.Ordinal) ? NextMode(parsed) : parsed;
    }

    /// <summary>
    /// The single enum→display-name table — now a lookup over the mode catalog.
    /// The other copy of the mode names lives in the widget's
    /// <c>[WidgetProperty]</c> choice array (a compile-time string literal),
    /// pinned to the catalog by the attribute lockstep test.
    /// </summary>
    internal static string DisplayName(WeatherLayoutMode mode)
        => Modes.FirstOrDefault(entry => entry.Mode == mode)?.Name ?? DefaultLayoutMode;

    /// <summary>
    /// The shrink factor for the hero text stack (temp + condition): scales
    /// both lines down proportionally so the stack fits inside 85% of the hero
    /// height; 1 when it already fits.
    /// </summary>
    public static float HeroTextStackShrinkScale(float textStackTotalH, float heroHeight)
        => textStackTotalH > heroHeight * 0.85f ? (heroHeight * 0.85f) / textStackTotalH : 1f;

    /// <summary>
    /// The shrink factor for the metric pill strip when it overflows the
    /// content width: scales the pill text, padding, and gap down
    /// proportionally, never below 60% of the original width; 1 when it fits.
    /// </summary>
    public static float MetricPillShrinkScale(float totalPillsW, float width)
        => totalPillsW > width ? Math.Max(0.6f, width / totalPillsW) : 1f;

    /// <summary>The metric pill's font size at scale <paramref name="s"/>.</summary>
    public static float PillFontSize(float s) => Math.Clamp(13f * s, 8f, 24f);

    /// <summary>The metric pill's horizontal text padding at scale <paramref name="s"/>.</summary>
    public static float PillPadX(float s) => Math.Clamp(10f * s, 4f, 20f);

    /// <summary>The gap between metric pills at scale <paramref name="s"/>.</summary>
    public static float PillGap(float s) => Math.Clamp(8f * s, 3f, 16f);

    // -- Forecast strip ---------------------------------------------------------
    // One named rule per draw constant, so the render paths cannot drift from
    // the strip's pinned font/offset clamps (each mirrors the original
    // inline literal exactly).

    /// <summary>The forecast-strip day-name font size at scale <paramref name="s"/>.</summary>
    public static float ForecastDayFontSize(float s) => Math.Clamp(14f * s, 8f, 24f);

    /// <summary>The forecast-strip day-icon font size at scale <paramref name="s"/>.</summary>
    public static float ForecastDayIconFontSize(float s) => Math.Clamp(22f * s, 10f, 48f);

    /// <summary>The forecast-strip range font size at scale <paramref name="s"/>.</summary>
    public static float ForecastRangeFontSize(float s) => Math.Clamp(12f * s, 7f, 22f);

    /// <summary>The forecast-strip day-name top offset at scale <paramref name="s"/>.</summary>
    public static float ForecastDayTopOffset(float s) => Math.Clamp(18f * s, 10f, 36f);

    /// <summary>The forecast-strip range bottom inset at scale <paramref name="s"/>.</summary>
    public static float ForecastRangeBottomInset(float s) => Math.Clamp(10f * s, 5f, 20f);

    /// <summary>The minimum content height for the Detailed-mode strips: below
    /// it, the hero owns the whole content area (the forecast and metrics
    /// strips are hidden). One rule shared by both strips' visibility gates.</summary>
    public const float StripsMinHeight = 150f;

    /// <summary>The hero block's minimum height (px): below it the icon/temp
    /// stack is too small to read, so the hero never shrinks past this.</summary>
    public const float DetailedHeroMinHeight = 35f;

    /// <summary>The narrow-container auto-scale floor: the hero block scales
    /// down to at most half its natural width.</summary>
    public const float HeroBlockNarrowScaleFloor = 0.5f;

    /// <summary>The metric pill font's legibility floor (px): the shrink
    /// re-measure never goes below it.</summary>
    public const float MetricPillFontFloor = 7f;

    /// <summary>The forecast strip's height at scale <paramref name="sy"/>.</summary>
    public static float ForecastStripHeight(float sy) => Math.Clamp(80f * sy, 45f, 160f);

    /// <summary>The metrics pill strip's height at scale <paramref name="sy"/>.</summary>
    public static float MetricsStripHeight(float sy) => Math.Clamp(28f * sy, 16f, 50f);

    // -- Daily rows ---------------------------------------------------------------

    /// <summary>The Daily-forecast row's day-name font size at scale <paramref name="s"/>.</summary>
    public static float DailyDayFontSize(float s) => Math.Clamp(13f * s, 9f, 18f);

    /// <summary>The Daily-forecast row's icon font size at scale <paramref name="s"/>.</summary>
    public static float DailyIconFontSize(float s) => Math.Clamp(16f * s, 10f, 22f);

    /// <summary>The Daily-forecast row's description font size at scale <paramref name="s"/>.</summary>
    public static float DailyDescFontSize(float s) => Math.Clamp(11f * s, 8f, 15f);

    /// <summary>The Daily-forecast row's temp font size at scale <paramref name="s"/>.</summary>
    public static float DailyTempFontSize(float s) => Math.Clamp(12f * s, 8f, 16f);

    // -- Hourly columns ------------------------------------------------------------

    /// <summary>The Hourly-forecast column's time font size at scale <paramref name="s"/>.</summary>
    public static float HourlyTimeFontSize(float s) => Math.Clamp(11f * s, 8f, 15f);

    /// <summary>The Hourly-forecast column's icon font size at scale <paramref name="s"/>.</summary>
    public static float HourlyIconFontSize(float s) => Math.Clamp(20f * s, 12f, 28f);

    /// <summary>The Hourly-forecast column's temp font size at scale <paramref name="s"/>.</summary>
    public static float HourlyTempFontSize(float s) => Math.Clamp(12f * s, 8f, 16f);

    // -- Detailed hero -------------------------------------------------------------

    /// <summary>The Detailed hero icon size for a hero height: 75% of the hero
    /// height, clamped to the 20..220 range.</summary>
    public static float DetailedHeroIconSize(float heroHeight) => Math.Clamp(heroHeight * 0.75f, 20f, 220f);

    /// <summary>The Detailed hero temp size for a hero height: 45% of the hero
    /// height, clamped to the 14..140 range.</summary>
    public static float DetailedHeroTempSize(float heroHeight) => Math.Clamp(heroHeight * 0.45f, 14f, 140f);

    /// <summary>The Detailed hero description size for a hero height: 18% of
    /// the hero height, clamped to the 9..45 range.</summary>
    public static float DetailedHeroDescSize(float heroHeight) => Math.Clamp(heroHeight * 0.18f, 9f, 45f);

    /// <summary>The gap between the Detailed hero icon and the temp/condition
    /// stack at scale <paramref name="s"/>.</summary>
    public static float DetailedHeroGap(float s) => Math.Clamp(20f * s, 8f, 50f);

    // -- CurrentOnly / Compact heroes ----------------------------------------------

    /// <summary>The CurrentOnly hero icon size at scale <paramref name="s"/>.</summary>
    public static float CurrentOnlyIconSize(float s) => Math.Clamp(88f * s, 40f, 120f);

    /// <summary>The CurrentOnly hero temp size at scale <paramref name="s"/>.</summary>
    public static float CurrentOnlyTempSize(float s) => Math.Clamp(64f * s, 28f, 84f);

    /// <summary>The CurrentOnly hero description size at scale <paramref name="s"/>.</summary>
    public static float CurrentOnlyDescSize(float s) => Math.Clamp(24f * s, 12f, 32f);

    /// <summary>The Compact hero icon font size at scale <paramref name="s"/>.</summary>
    public static float CompactIconFontSize(float s) => Math.Clamp(26f * s, 14f, 32f);

    /// <summary>The Compact hero temp font size at scale <paramref name="s"/>.</summary>
    public static float CompactTempFontSize(float s) => Math.Clamp(20f * s, 12f, 26f);

    // -- Header badge / title --------------------------------------------------------

    /// <summary>The unit-toggle badge font size at scale <paramref name="s"/>.</summary>
    public static float BadgeFontSize(float s) => Math.Clamp(17f * s, 10f, 30f);

    /// <summary>The title's max draw width: the content width minus the badge,
    /// never below 30px (the header truncation rule).</summary>
    public static float TitleMaxWidth(float width, float pad, float badgeWidth)
        => Math.Max(30f, width - pad * 2f - badgeWidth);
}
