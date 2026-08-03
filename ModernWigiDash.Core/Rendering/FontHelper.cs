using SkiaSharp;

namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// Provides centralized management and lazy-loading for primary application font typefaces (Vercel Geist Variable Font).
/// </summary>
public static class FontHelper
{
    private static readonly Lazy<SKTypeface?> _geistTypeface = new(() =>
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string fontPath = Path.Combine(baseDir, "Resources", "Fonts", "Geist-VariableFont_wght.ttf");
            if (File.Exists(fontPath))
            {
                var tf = SKTypeface.FromFile(fontPath);
                if (tf != null) return tf;
            }

            string asmLocation = Path.GetDirectoryName(typeof(FontHelper).Assembly.Location) ?? "";
            string asmFontPath = Path.Combine(asmLocation, "Resources", "Fonts", "Geist-VariableFont_wght.ttf");
            if (File.Exists(asmFontPath))
            {
                var tf = SKTypeface.FromFile(asmFontPath);
                if (tf != null) return tf;
            }
        }
        catch
        {
            // Clean fallback
        }

        return SKTypeface.FromFamilyName("Geist") ?? SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
    });

    /// <summary>
    /// Gets the loaded Geist Variable SKTypeface instance.
    /// </summary>
    public static SKTypeface GeistTypeface => _geistTypeface.Value ?? SKTypeface.Default;

    /// <summary>
    /// Gets an SKTypeface for the requested family and style, using Geist Variable Font for all primary typography.
    /// </summary>
    public static SKTypeface GetTypeface(string familyName, SKFontStyle style)
    {
        if (familyName.Equals("Segoe UI Emoji", StringComparison.OrdinalIgnoreCase))
        {
            return SKTypeface.FromFamilyName("Segoe UI Emoji", style) ?? SKTypeface.Default;
        }

        return _geistTypeface.Value ?? SKTypeface.FromFamilyName("Geist", style) ?? SKTypeface.FromFamilyName("Segoe UI", style) ?? SKTypeface.Default;
    }

    /// <summary>
    /// Creates a high-quality Geist SKFont (subpixel antialiasing + full hinting) for the requested size and style.
    /// </summary>
    public static SKFont CreateFont(float size, SKFontStyle style)
    {
        var font = new SKFont(GetTypeface("Geist", style), size);
        ConfigureHighQualityFont(font);
        return font;
    }

    /// <summary>
    /// Creates a high-quality SKFont (subpixel antialiasing + full hinting) for the requested family, size, and style.
    /// </summary>
    public static SKFont CreateFont(string familyName, float size, SKFontStyle style)
    {
        var font = new SKFont(GetTypeface(familyName, style), size);
        ConfigureHighQualityFont(font);
        return font;
    }

    /// <summary>
    /// Creates a high-quality SKFont (subpixel antialiasing + full hinting) for the requested family, style, and size.
    /// </summary>
    public static SKFont CreateFont(string familyName, SKFontStyle style, float size)
    {
        var font = new SKFont(GetTypeface(familyName, style), size);
        ConfigureHighQualityFont(font);
        return font;
    }

    /// <summary>
    /// Creates a high-quality SKFont (subpixel antialiasing + full hinting) for the requested typeface and size.
    /// </summary>
    public static SKFont CreateFont(SKTypeface typeface, float size)
    {
        var font = new SKFont(typeface, size);
        ConfigureHighQualityFont(font);
        return font;
    }

    /// <summary>
    /// Configures high-quality anti-aliasing, subpixel text positioning, and ClearType rendering flags on an SKFont instance.
    /// </summary>
    public static void ConfigureHighQualityFont(SKFont font)
    {
        font.Subpixel = true;
        font.Edging = SKFontEdging.SubpixelAntialias;
        font.Hinting = SKFontHinting.Full;
        font.LinearMetrics = true;
    }
}
