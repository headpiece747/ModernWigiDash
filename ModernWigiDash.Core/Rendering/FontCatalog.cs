using SkiaSharp;

namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// Thin pass-through facade over <see cref="FontHelper"/>, which owns all typeface
/// resolution and caching (family list, per-(family, style) typefaces, per-codepoint
/// fallback). Retained so existing callers and tests keep compiling unchanged.
/// </summary>
public static class FontCatalog
{
    /// <summary>
    /// Returns the installed system font family list with "Geist" first.
    /// </summary>
    public static string[] GetAllFamilies() => FontHelper.GetAllFamilies();

    /// <summary>
    /// Gets an SKTypeface for the requested family and style, using Geist Variable Font
    /// for all primary typography and as the fallback for unknown families.
    /// </summary>
    public static SKTypeface GetTypeface(string familyName, SKFontStyle style)
        => FontHelper.GetTypeface(familyName, style);
}
