using System.Collections.Concurrent;
using System.Text;
using SkiaSharp;

namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// Provides centralized management and lazy-loading for primary application font typefaces (Vercel Geist Variable Font).
/// </summary>
public static class FontHelper
{
    private static readonly ConcurrentDictionary<(int Codepoint, SKFontStyle Style), Lazy<SKTypeface>> _fallbackCache = new();

    // One native typeface per (family, style): serves direct family resolution (GetTypeface)
    // and dedupes MatchCharacter results so duplicate native typefaces for the same key are
    // disposed. The Lazy wrapper makes a concurrent GetOrAdd double-run benign — only the
    // stored Lazy is ever evaluated, so at most one native typeface per key is materialized.
    private static readonly ConcurrentDictionary<(string Family, SKFontStyle Style), Lazy<SKTypeface>> _typefaceCache = new();

    private static readonly Lock _fontManagerLock = new();

    private static readonly Lock _familyListLock = new();
    private static string[]? _families;

    /// <summary>
    /// Memoized glyph presence per (typeface handle, codepoint). All typefaces here are
    /// process-lifetime cached (Geist lazy, system fonts via the fallback cache), so a
    /// typeface handle is never reused for a different font while its entries are live.
    /// Bounded by a simple clear-on-overflow reset.
    /// </summary>
    private static readonly ConcurrentDictionary<(long TypefaceHandle, int Codepoint), bool> _glyphPresenceCache = new();

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
            System.Diagnostics.Debug.WriteLine("Geist font load failed, using clean fallback");
        }

        return SKTypeface.FromFamilyName(DefaultFontName) ?? SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
    });

    /// <summary>The project font family — the one name the widgets pass to
    /// GetCachedFont (previously a string literal at 60+ call sites).</summary>
    public const string DefaultFontName = "Geist";

    /// <summary>
    /// Gets the loaded Geist Variable SKTypeface instance.
    /// </summary>
    public static SKTypeface GeistTypeface => _geistTypeface.Value ?? SKTypeface.Default;

    /// <summary>
    /// Returns the installed system font family list with "Geist" first, deduped
    /// case-insensitively and cached once. Falls back to a bare Geist list when
    /// the font manager itself fails, so the inspector never crashes on a
    /// broken font store.
    /// </summary>
    public static string[] GetAllFamilies()
    {
        if (_families != null) return _families;
        lock (_familyListLock)
        {
            if (_families == null)
            {
                try
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    List<string> list = [];
                    if (seen.Add(DefaultFontName))
                        list.Add(DefaultFontName);
                    list.AddRange(SKFontManager.Default.FontFamilies.Where(family => !string.IsNullOrWhiteSpace(family) && seen.Add(family)));
                    _families = list.ToArray();
                }
                catch
                {
                    // Broken font store — never crash the inspector; Geist alone
                    // still renders (the typeface caches fall back to Default).
                    System.Diagnostics.Debug.WriteLine("Font family enumeration failed, falling back to Geist only");
                    _families = [DefaultFontName];
                }
            }
        }
        return _families;
    }

    private static readonly Lazy<SKTypeface> _segoeEmojiTypeface = new(() => SKTypeface.FromFamilyName("Segoe UI Emoji") ?? SKTypeface.Default);
    private static readonly Lazy<SKTypeface> _segoeSymbolTypeface = new(() => SKTypeface.FromFamilyName("Segoe UI Symbol") ?? SKTypeface.Default);
    private static readonly Lazy<SKTypeface> _segoeUiTypeface = new(() => SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default);

    /// <summary>
    /// Checks whether a typeface contains a glyph for the given codepoint using SKFont (the non-obsolete API).
    /// </summary>
    private static bool ContainsGlyphSafe(SKTypeface typeface, int codepoint)
    {
        var key = (typeface.Handle.ToInt64(), codepoint);
        if (_glyphPresenceCache.TryGetValue(key, out bool known))
        {
            return known;
        }

        bool result = ComputeGlyphPresence(typeface, codepoint);
        if (_glyphPresenceCache.Count > 4096)
        {
            _glyphPresenceCache.Clear();
        }
        _glyphPresenceCache[key] = result;
        return result;
    }

    private static bool ComputeGlyphPresence(SKTypeface typeface, int codepoint)
    {
        try
        {
            using var font = new SKFont(typeface, 12f);
            return font.ContainsGlyph(codepoint);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves an appropriate SKTypeface for a given codepoint and style. The preferred typeface (the
    /// font the caller actually selected, e.g. from the Font Family property) wins when it contains the
    /// glyph; otherwise Geist Variable Font is tried, then system matched fonts (Segoe UI Emoji/Symbol/Segoe UI).
    /// </summary>
    public static SKTypeface GetTypefaceForCodepoint(int codepoint, SKFontStyle style, SKTypeface? preferred = null)
    {
        if (preferred is { Handle: not 0 } && ContainsGlyphSafe(preferred, codepoint))
        {
            return preferred;
        }

        var geist = GeistTypeface;
        if (geist is { Handle: not 0 } && ContainsGlyphSafe(geist, codepoint))
        {
            return geist;
        }

        var key = (codepoint, style);
        if (_fallbackCache.Count > 2048)
        {
            _fallbackCache.Clear();
        }
        return _fallbackCache.GetOrAdd(key, k => new Lazy<SKTypeface>(() => ResolveFallback(k))).Value;
    }

    /// <summary>
    /// Resolves a fallback typeface for a codepoint/style. Fallback order: Segoe UI Emoji →
    /// Segoe UI Symbol → Segoe UI → system MatchCharacter → Default. MatchCharacter results are
    /// deduped by family name so at most one native typeface per family is retained. The Lazy
    /// wrapper makes a concurrent GetOrAdd double-run benign: only the stored Lazy is ever
    /// evaluated (a discarded Lazy never materializes a typeface), and any duplicate native
    /// typeface produced by MatchCharacter is disposed by <see cref="DedupeByFamily"/>.
    /// </summary>
    private static SKTypeface ResolveFallback((int Codepoint, SKFontStyle Style) key)
    {
        var emoji = _segoeEmojiTypeface.Value;
        if (emoji is { Handle: not 0 } && ContainsGlyphSafe(emoji, key.Codepoint))
        {
            return emoji;
        }

        var symbol = _segoeSymbolTypeface.Value;
        if (symbol is { Handle: not 0 } && ContainsGlyphSafe(symbol, key.Codepoint))
        {
            return symbol;
        }

        var segoe = _segoeUiTypeface.Value;
        if (segoe is { Handle: not 0 } && ContainsGlyphSafe(segoe, key.Codepoint))
        {
            return segoe;
        }

        try
        {
            SKTypeface? matched;
            lock (_fontManagerLock)
            {
                matched = SKFontManager.Default.MatchCharacter(key.Codepoint);
            }
            if (matched is { Handle: not 0 })
            {
                return DedupeByFamily(matched, key.Style);
            }
        }
        catch
        {
            // Silently fall through to default typeface
            System.Diagnostics.Debug.WriteLine("Font match failed, using default typeface");
        }

        return SKTypeface.Default;
    }

    /// <summary>
    /// Returns a single cached typeface per (family, style), disposing any duplicate native
    /// typeface from MatchCharacter (safe: the loser is never used or stored).
    /// </summary>
    private static SKTypeface DedupeByFamily(SKTypeface typeface, SKFontStyle style)
    {
        var lazy = _typefaceCache.GetOrAdd((typeface.FamilyName, style), _ => new Lazy<SKTypeface>(typeface));
        var cached = lazy.Value;
        if (!ReferenceEquals(cached, typeface))
        {
            typeface.Dispose();
            return cached;
        }
        return cached;
    }

    /// <summary>
    /// Splits text into runs of contiguous characters sharing the same SKTypeface for rendering.
    /// The preferred typeface is honored first for every codepoint it covers.
    /// </summary>
    public static List<(string Text, SKTypeface Typeface)> GetTextRuns(string text, SKFontStyle style, SKTypeface? preferred = null)
    {
        List<(string Text, SKTypeface Typeface)> runs = [];
        if (string.IsNullOrEmpty(text))
        {
            return runs;
        }

        var currentRun = new StringBuilder();
        SKTypeface? currentTf = null;

        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int codepoint = char.ConvertToUtf32(text, i);
            var rune = new Rune(codepoint); // no intermediate heap string
            var tf = GetTypefaceForCodepoint(codepoint, style, preferred);

            if (currentTf == null)
            {
                currentTf = tf;
                currentRun.Append(rune);
            }
            else if (currentTf.Handle == tf.Handle || currentTf.FamilyName == tf.FamilyName)
            {
                currentRun.Append(rune);
            }
            else
            {
                runs.Add((currentRun.ToString(), currentTf));
                currentRun.Clear();
                currentRun.Append(rune);
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
        var runs = GetTextRuns(text, style, baseFont.Typeface);
        float totalWidth = 0f;

        foreach (var run in runs)
        {
            var font = GetCachedFont(run.Typeface, baseFont.Size);
            totalWidth += font.MeasureText(run.Text);
        }

        return totalWidth;
    }

    /// <summary>
    /// Draws text on the canvas with dynamic font fallback per character run to prevent missing glyph placeholders.
    /// Center/Right alignment measures the runs built for drawing — one pass, not a second GetTextRuns call.
    /// </summary>
    public static void DrawTextWithFallback(this SKCanvas canvas, string text, float x, float y, SKFont baseFont, SKPaint paint, SKTextAlign align = SKTextAlign.Left)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var style = baseFont.Typeface?.FontStyle ?? SKFontStyle.Normal;
        var runs = GetTextRuns(text, style, baseFont.Typeface);

        if (align != SKTextAlign.Left)
        {
            // Measure the runs we already built (the old code re-split the
            // text via MeasureTextWithFallback — a second run computation
            // per draw on the 30 FPS path).
            float totalW = 0f;
            foreach (var run in runs)
            {
                totalW += GetCachedFont(run.Typeface, baseFont.Size).MeasureText(run.Text);
            }
            x -= align == SKTextAlign.Right ? totalW : totalW * 0.5f;
        }

        float currentX = x;
        foreach (var run in runs)
        {
            var font = GetCachedFont(run.Typeface, baseFont.Size);
            canvas.DrawText(run.Text, currentX, y, SKTextAlign.Left, font, paint);
            currentX += font.MeasureText(run.Text);
        }
    }

    /// <summary>
    /// Gets an SKTypeface for the requested family and style, using Geist Variable Font for all primary typography.
    /// </summary>
    public static SKTypeface GetTypeface(string familyName, SKFontStyle style)
    {
        if (string.IsNullOrWhiteSpace(familyName) ||
            familyName.Equals(DefaultFontName, StringComparison.OrdinalIgnoreCase))
        {
            // Geist is a variable font covering every style — the style-pinned
            // FromFamilyName fallback chain after _geistTypeface.Value was dead
            // (the lazy never yields null; GeistTypeface already ends in
            // SKTypeface.Default).
            return GeistTypeface;
        }

        return _typefaceCache.GetOrAdd((familyName, style), key => new Lazy<SKTypeface>(() => ResolveDirectTypeface(key))).Value;
    }

    /// <summary>
    /// Resolves a typeface for (family, style) from the font manager, falling back to the
    /// app font (Geist) when the family is unknown or the lookup fails. Runs once per key,
    /// inside the <see cref="_typefaceCache"/> Lazy.
    /// </summary>
    private static SKTypeface ResolveDirectTypeface((string Family, SKFontStyle Style) key)
    {
        try
        {
            SKTypeface? tf = SKTypeface.FromFamilyName(key.Family, key.Style);
            if (tf is { Handle: not 0 }) return tf;
        }
        catch
        {
            // Fall through to the app font below.
            System.Diagnostics.Debug.WriteLine("Typeface lookup failed, falling back to app font");
        }

        return GeistTypeface;
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
    /// Returns a CACHED high-quality SKFont for (typeface, size). Widget renders
    /// run at 30 FPS and sizes change only on resize, so per-render font
    /// allocation is pure native churn (~10-20 SKFont objects per widget per
    /// frame). Callers must NOT dispose the returned font.
    /// </summary>
    public static SKFont GetCachedFont(SKTypeface typeface, float size)
    {
        int sizeKey = (int)Math.Round(size * 2); // half-point resolution
        // Key by the typeface HANDLE (stable — typefaces are cached for the
        // process lifetime): the family name alone cannot distinguish
        // Regular from Bold.
        return CachedFonts.GetOrAdd(
            (typeface.Handle.ToInt64(), sizeKey),
            _ => CreateFont(typeface, size));
    }

    /// <summary>
    /// Creates or returns the cached font for a family by name.
    /// Callers must NOT dispose the returned font.
    /// </summary>
    public static SKFont GetCachedFont(string familyName, SKFontStyle style, float size)
        => GetCachedFont(GetTypeface(familyName, style), size);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(long TypefaceHandle, int SizeKey), SKFont> CachedFonts = new();

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
