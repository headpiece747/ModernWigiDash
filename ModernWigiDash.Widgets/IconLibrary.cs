using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Lazy access to the bundled Material Symbols Rounded icon font and glyph lookups.
/// </summary>
public static class IconLibrary
{
    public const string FontFileName = "MaterialSymbolsRounded-Regular.ttf";
    public const string FontFamilyName = "Material Symbols Rounded";

    private static readonly Lazy<SKTypeface> _typeface = new(() =>
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string fontPath = Path.Combine(baseDir, "Resources", "Fonts", FontFileName);
            if (File.Exists(fontPath))
            {
                SKTypeface? tf = SKTypeface.FromFile(fontPath);
                if (tf != null) return tf;
            }

            string asmLocation = Path.GetDirectoryName(typeof(IconLibrary).Assembly.Location) ?? "";
            string asmFontPath = Path.Combine(asmLocation, "Resources", "Fonts", FontFileName);
            if (File.Exists(asmFontPath))
            {
                SKTypeface? tf = SKTypeface.FromFile(asmFontPath);
                if (tf != null) return tf;
            }
        }
        catch
        {
            // Fall back below.
        }

        return FontHelper.GeistTypeface;
    });

    public static SKTypeface GetTypeface() => _typeface.Value;

    public static IReadOnlyCollection<string> Names => IconCodepoints.Map.Keys.ToArray();

    public static bool TryGetGlyph(string iconName, out string glyph)
    {
        glyph = "";
        if (string.IsNullOrWhiteSpace(iconName)) return false;
        if (IconCodepoints.Map.TryGetValue(iconName.Trim(), out int codepoint))
        {
            glyph = char.ConvertFromUtf32(codepoint);
            return true;
        }
        return false;
    }

    public static string GlyphString(string iconName)
        => TryGetGlyph(iconName, out string glyph) ? glyph : "";
}
