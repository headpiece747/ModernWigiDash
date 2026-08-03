using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using SkiaSharp;

namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// Provides centralized management and lazy-loading for primary application font typefaces (Vercel Geist Variable Font).
/// </summary>
public static class FontHelper
{
    private static readonly ConcurrentDictionary<(int Codepoint, SKFontStyle Style), SKTypeface> _fallbackCache = new();
    private static readonly object _fontManagerLock = new();

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
    /// Resolves an appropriate SKTypeface for a given codepoint and style, using Geist Variable Font if available,
    /// or falling back to system matched fonts (e.g. Segoe UI Emoji/Symbol/Segoe UI).
    /// </summary>
    public static SKTypeface GetTypefaceForCodepoint(int codepoint, SKFontStyle style)
    {
        var geist = GeistTypeface;
        if (geist != null && geist.Handle != IntPtr.Zero && geist.ContainsGlyph(codepoint))
        {
            return geist;
        }

        return _fallbackCache.GetOrAdd((codepoint, style), key =>
        {
            SKTypeface? matched;
            lock (_fontManagerLock)
            {
                matched = SKFontManager.Default.MatchCharacter(key.Codepoint);
            }
            if (matched != null && matched.Handle != IntPtr.Zero)
            {
                return matched;
            }

            var emojiTf = SKTypeface.FromFamilyName("Segoe UI Emoji", key.Style);
            if (emojiTf != null && emojiTf.Handle != IntPtr.Zero) return emojiTf;

            var symbolTf = SKTypeface.FromFamilyName("Segoe UI Symbol", key.Style);
            if (symbolTf != null && symbolTf.Handle != IntPtr.Zero) return symbolTf;

            var segoeTf = SKTypeface.FromFamilyName("Segoe UI", key.Style);
            if (segoeTf != null && segoeTf.Handle != IntPtr.Zero) return segoeTf;

            return SKTypeface.Default;
        });
    }

    /// <summary>
    /// Splits text into runs of contiguous characters sharing the same SKTypeface for rendering.
    /// </summary>
    public static List<(string Text, SKTypeface Typeface)> GetTextRuns(string text, SKFontStyle style)
    {
        var runs = new List<(string Text, SKTypeface Typeface)>();
        if (string.IsNullOrEmpty(text))
        {
            return runs;
        }

        var currentRun = new StringBuilder();
        SKTypeface? currentTf = null;

        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int codepoint = char.ConvertToUtf32(text, i);
            string charStr = char.ConvertFromUtf32(codepoint);
            var tf = GetTypefaceForCodepoint(codepoint, style);

            if (currentTf == null)
            {
                currentTf = tf;
                currentRun.Append(charStr);
            }
            else if (currentTf.Handle == tf.Handle || currentTf.FamilyName == tf.FamilyName)
            {
                currentRun.Append(charStr);
            }
            else
            {
                runs.Add((currentRun.ToString(), currentTf));
                currentRun.Clear();
                currentRun.Append(charStr);
                currentTf = tf;
            }
        }

        if (currentRun.Length > 0 && currentTf != null)
        {
            runs.Add((currentRun.ToString(), currentTf));
        }

        return runs;
    }

    /// <summary>
    /// Measures the total width of text, accounting for font glyph fallback runs.
    /// </summary>
    public static float MeasureTextWithFallback(string text, SKFont baseFont)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0f;
        }

        var style = baseFont.Typeface?.FontStyle ?? SKFontStyle.Normal;
        var runs = GetTextRuns(text, style);
        float totalWidth = 0f;

        foreach (var run in runs)
        {
            using var font = new SKFont(run.Typeface, baseFont.Size);
            ConfigureHighQualityFont(font);
            totalWidth += font.MeasureText(run.Text);
        }

        return totalWidth;
    }

    /// <summary>
    /// Draws text on the canvas with dynamic font fallback per character run to prevent missing glyph placeholders.
    /// </summary>
    public static void DrawTextWithFallback(this SKCanvas canvas, string text, float x, float y, SKFont baseFont, SKPaint paint, SKTextAlign align = SKTextAlign.Left)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var style = baseFont.Typeface?.FontStyle ?? SKFontStyle.Normal;
        var runs = GetTextRuns(text, style);

        if (align == SKTextAlign.Right)
        {
            float totalW = MeasureTextWithFallback(text, baseFont);
            x -= totalW;
        }
        else if (align == SKTextAlign.Center)
        {
            float totalW = MeasureTextWithFallback(text, baseFont);
            x -= totalW * 0.5f;
        }

        float currentX = x;
        foreach (var run in runs)
        {
            using var font = new SKFont(run.Typeface, baseFont.Size);
            ConfigureHighQualityFont(font);
            canvas.DrawText(run.Text, currentX, y, SKTextAlign.Left, font, paint);
            currentX += font.MeasureText(run.Text);
        }
    }

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
