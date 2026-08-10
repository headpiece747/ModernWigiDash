using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>The header tap zones of the Weather widget, in precedence order.</summary>
public enum WeatherHeaderAction
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
public readonly record struct WeatherHeaderLayout(
    float HeaderHeight,
    SKRect BadgeRect,
    float HeaderTextY,
    float TitleFontSize,
    float Pad);

/// <summary>
/// Pure layout rules for the Weather widget: the scale factors, the header
/// geometry (title, unit badge, content padding), the header touch zones, the
/// layout-mode cycle, and the hero/pill shrink rules. Moved out of the
/// widget's render and touch paths so the drawn geometry and the tap targets
/// share one source of truth.
/// </summary>
public static class WeatherLayout
{
    /// <summary>The widget's design-space dimensions — the scale base for both render and touch.</summary>
    public const float DesignWidth = 406f;

    /// <summary>The widget's design-space dimensions — the scale base for both render and touch.</summary>
    public const float DesignHeight = 296f;

    /// <summary>The default layout mode — the single source for the property default and the cycle rule.</summary>
    public const string DefaultLayoutMode = "Detailed";

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
    /// The tap-cycle rule: Detailed → Daily Forecast → Hourly Forecast →
    /// Current Only → Compact → Detailed. Unknown modes reset to the default.
    /// </summary>
    public static string NextLayoutMode(string current)
        => current switch
        {
            "Detailed" => "Daily Forecast",
            "Daily Forecast" => "Hourly Forecast",
            "Hourly Forecast" => "Current Only",
            "Current Only" => "Compact",
            _ => DefaultLayoutMode,
        };

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
}
