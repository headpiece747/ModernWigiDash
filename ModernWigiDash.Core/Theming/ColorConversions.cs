using System.Runtime.InteropServices;

namespace ModernWigiDash.Core.Theming;

/// <summary>HSV color value. H in [0,360), S and V in [0,1].</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct HsvColor(double H, double S, double V);

/// <summary>
/// Pure color conversions between HSV and <see cref="RgbaColor"/>, plus the
/// canonical hex formatter. No WPF, no Skia — testable without a UI thread.
/// Parsing stays in <see cref="ThemeSettings.ParseColor"/> (the single parser).
/// </summary>
public static class ColorConversions
{
    /// <summary>Converts an <see cref="HsvColor"/> to an opaque <see cref="RgbaColor"/>.</summary>
    /// <param name="hsv">The HSV value (H wrapped into [0, 360), S and V clamped to [0, 1]).</param>
    /// <returns>The converted RGBA color (opaque, alpha 255).</returns>
    public static RgbaColor HsvToRgb(HsvColor hsv)
    {
        double h = hsv.H < 0 ? hsv.H % 360 + 360 : hsv.H % 360;
        double s = Math.Clamp(hsv.S, 0, 1);
        double v = Math.Clamp(hsv.V, 0, 1);

        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return new RgbaColor(
            255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Converts an <see cref="RgbaColor"/> to an <see cref="HsvColor"/> (the alpha channel is ignored).</summary>
    /// <param name="rgb">The RGBA value to convert.</param>
    /// <returns>The converted HSV value.</returns>
    public static HsvColor RgbToHsv(RgbaColor rgb)
    {
        double r = rgb.R / 255.0, g = rgb.G / 255.0, b = rgb.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            // S1244 suppressed below: `max` is bit-identical to one of the
            // r/g/b inputs (assigned from Math.Max of them), so `==` is exact
            // by construction — the guard needs no float tolerance.
#pragma warning disable S1244 // exact-by-construction: max is bit-identical to one input
            h = max switch
            {
                var m when m == r => 60 * ((g - b) / delta % 6),
                var m when m == g => 60 * ((b - r) / delta + 2),
                _ => 60 * ((r - g) / delta + 4)
            };
#pragma warning restore S1244
            if (h < 0) h += 360;
        }

        double s = max == 0 ? 0 : delta / max;
        return new HsvColor(h, s, max);
    }

    /// <summary>Formats as #RRGGBB (opaque) or #AARRGGBB (with alpha), uppercase.</summary>
    public static string FormatHex(RgbaColor color)
        => color.A == 255
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}

/// <summary>One curated preset swatch: a display name and its hex value.</summary>
public readonly record struct PresetSwatch(string Name, string Hex);

/// <summary>
/// The curated preset palette shown at the top of the color popup. Drawn from
/// the app's own palette so one-click picks stay theme-consistent.
/// </summary>
public static class PresetPalette
{
    /// <summary>The curated swatches in display order (the color popup renders them top to bottom).</summary>
    public static IReadOnlyList<PresetSwatch> Swatches { get; } =
    [
        new("White", "#FAFAFA"),
        new("Zinc", "#A1A1AA"),
        new("Amber", "#F59E0B"),
        new("Highlight", "#FBBF24"),
        new("Green", "#10B981"),
        new("Emerald", "#22C55E"),
        new("Red", "#EF4444"),
        new("Blue", "#3B82F6"),
        new("Page Default", "#12141D"),
        new("App Background", "#121214"),
        new("Panel", "#1A1A1E"),
        new("Card", "#26262B")
    ];
}
