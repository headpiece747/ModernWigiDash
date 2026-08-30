using System.Collections.Concurrent;
using System.Text;

namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// Provides centralized management and lazy-loading for primary application font typefaces (Vercel Geist Variable Font).
/// </summary>
public static class FontHelper
{
    /// <summary>
    /// The value identity of an <see cref="SKFontStyle"/>: weight, width, slant.
    /// SkiaSharp exposes a style as a fresh managed wrapper on every property
    /// read (<c>SKFontStyle.GetObject</c> allocates; there is no value equality),
    /// so every FontHelper cache keys a style by these three values, never by
    /// wrapper reference — a reference key would miss on every draw for the same
    /// logical style. Reading the components is allocation-free (three P/Invoke
    /// int/enum getters).
    /// </summary>
    private static (int Weight, int Width, int Slant) StyleValue(SKFontStyle style)
        => (style.Weight, style.Width, (int)style.Slant);

    /// <summary>
    /// Memoized fallback typeface per (codepoint, style VALUE). The style is
    /// keyed by its weight/width/slant value, not by the interop wrapper
    /// reference, so equal styles (even on different wrapper instances) share
    /// one entry. Bounded by the shared clear-on-overflow rule
    /// (<see cref="FontCacheEviction"/>).
    /// </summary>
    private static readonly ConcurrentDictionary<(int Codepoint, int Weight, int Width, int Slant), Lazy<SKTypeface>> _fallbackCache = new();

    // One native typeface per (family, style VALUE): serves direct family resolution
    // (GetTypeface) and dedupes MatchCharacter results so duplicate native typefaces
    // for the same key are disposed. The style is keyed by its weight/width/slant
    // value (the interop wrapper is a fresh object per read, with no value equality).
    // Values are stored eagerly (the former Lazy wrapper was an eager
    // Lazy(value) - pure ceremony): a race loser's typeface is disposed
    // immediately instead of by finalizer.
    private static readonly ConcurrentDictionary<(string Family, int Weight, int Width, int Slant), SKTypeface> _typefaceCache = new();

    private static readonly Lock _fontManagerLock = new();

    private static readonly Lock _familyListLock = new();
    private static string[]? _families;

    /// <summary>
    /// Memoized glyph presence per (typeface handle, codepoint). All typefaces here are
    /// process-lifetime cached (Geist lazy, system fonts via the fallback cache), so a
    /// typeface handle is never reused for a different font while its entries are live.
    /// Bounded by the shared clear-on-overflow rule (<see cref="FontCacheEviction"/>).
    /// </summary>
    private static readonly ConcurrentDictionary<(long TypefaceHandle, int Codepoint), bool> _glyphPresenceCache = new();

    /// <summary>
    /// Memoized run splits per (text, style VALUE, preferred typeface HANDLE):
    /// the per-glyph fallback decision depends only on that tuple — not on font
    /// size or target width — so the expensive split is computed once per
    /// distinct input and the per-call measure/draw loops iterate the cached
    /// runs. Both identities are value/handle, never wrapper reference: the
    /// interop <c>SKFontStyle</c> wrapper is a fresh managed object on every
    /// <c>.FontStyle</c> read (and carries no value equality), and a reference
    /// key would miss on every draw while the underlying style and typeface
    /// are unchanged. The cached lists are shared — callers must treat them as
    /// read-only. Bounded by the shared clear-on-overflow rule
    /// (<see cref="FontCacheEviction"/>).
    /// </summary>
    private static readonly ConcurrentDictionary<(string Text, int Weight, int Width, int Slant, long TypefaceHandle), List<(string Text, SKTypeface Typeface)>> _textRunsCache = new();

    private static readonly Lazy<SKTypeface?> _geistTypeface = new(() =>
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string fontPath = Path.Combine(baseDir, "Resources", "Fonts", "Geist-VariableFont_wght.ttf");
            if (File.Exists(fontPath))
            {
                var tf = SKTypeface.FromFile(fontPath);
                if (tf != null) return tf;
            }
        }
        catch
        {
            FileLog.Write("Geist font load failed, using clean fallback");
        }

        return SKTypeface.FromFamilyName(DefaultFontName) ?? SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
    });

    /// <summary>The project font family — the one name the widgets pass to
    /// GetCachedFont.</summary>
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
                    FileLog.Write("Font family enumeration failed, falling back to Geist only");
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
        FontCacheEviction.EvictIfFull(_glyphPresenceCache, FontCacheEviction.GlyphPresenceLimit);
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

        var styleValue = StyleValue(style);
        var key = (codepoint, styleValue.Weight, styleValue.Width, styleValue.Slant);
        FontCacheEviction.EvictIfFull(_fallbackCache, FontCacheEviction.FallbackTypefaceLimit);
        return _fallbackCache.GetOrAdd(key, static k => new Lazy<SKTypeface>(() => ResolveFallback(k))).Value;
    }

    /// <summary>
    /// Resolves a fallback typeface for a codepoint/style. Fallback order: Segoe UI Emoji →
    /// Segoe UI Symbol → Segoe UI → system MatchCharacter → Default. MatchCharacter results are
    /// deduped by family name so at most one native typeface per family is retained. The Lazy
    /// wrapper makes a concurrent GetOrAdd double-run benign: only the stored Lazy is ever
    /// evaluated (a discarded Lazy never materializes a typeface), and any duplicate native
    /// typeface produced by MatchCharacter is disposed by <see cref="DedupeByFamily"/>.
    /// </summary>
    private static SKTypeface ResolveFallback((int Codepoint, int Weight, int Width, int Slant) key)
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
                return DedupeByFamily(matched, (key.Weight, key.Width, key.Slant));
            }
        }
        catch
        {
            // Silently fall through to default typeface
            FileLog.Write("Font match failed, using default typeface");
        }

        return SKTypeface.Default;
    }

    /// <summary>
    /// Returns a single cached typeface per (family, style), disposing any duplicate native
    /// typeface from MatchCharacter (safe: the loser is never used or stored).
    /// </summary>
    private static SKTypeface DedupeByFamily(SKTypeface typeface, (int Weight, int Width, int Slant) styleValue)
    {
        var key = (typeface.FamilyName, styleValue.Weight, styleValue.Width, styleValue.Slant);
        var winner = _typefaceCache.GetOrAdd(key, typeface);
        if (!ReferenceEquals(winner, typeface))
        {
            typeface.Dispose();
            return winner;
        }

        return winner;
    }

    /// <summary>
    /// Splits text into runs of contiguous characters sharing the same SKTypeface for rendering.
    /// The preferred typeface is honored first for every codepoint it covers.
    /// The split is memoized per (text, style, preferred typeface handle) — it is
    /// independent of font size and target width — and the returned list is shared:
    /// callers must not mutate it.
    /// </summary>
    public static IReadOnlyList<(string Text, SKTypeface Typeface)> GetTextRuns(string text, SKFontStyle style, SKTypeface? preferred = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return GetTextRuns(text, StyleValue(style), preferred?.Handle.ToInt64() ?? 0, style, preferred);
    }

    private static IReadOnlyList<(string Text, SKTypeface Typeface)> GetTextRuns(string text, (int Weight, int Width, int Slant) styleValue, long preferredHandle, SKFontStyle style, SKTypeface? preferred)
    {
        // Value/handle-keyed fast path: the widget draw path hands in fresh
        // interop wrappers on every call, and the per-call TryGetValue is
        // allocation-free, while a per-call GetOrAdd closure is not.
        var key = (text, styleValue.Weight, styleValue.Width, styleValue.Slant, preferredHandle);
        if (_textRunsCache.TryGetValue(key, out List<(string Text, SKTypeface Typeface)>? existing))
        {
            return existing;
        }

        FontCacheEviction.EvictIfFull(_textRunsCache, FontCacheEviction.TextRunsLimit);
        // Value overload, no factory: the per-call closure would allocate its
        // display class on the entry path of every measure/draw call.
        var computed = ComputeTextRuns(text, style, preferred);
        return _textRunsCache.GetOrAdd(key, computed);
    }

    private static List<(string Text, SKTypeface Typeface)> ComputeTextRuns(string text, SKFontStyle style, SKTypeface? preferred)
    {
        List<(string Text, SKTypeface Typeface)> runs = [];

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
            else if (currentTf.Handle == tf.Handle || string.Equals(currentTf.FamilyName, tf.FamilyName, StringComparison.Ordinal))
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

        var (typefaceHandle, styleValue, style, typeface) = GetFontMeta(baseFont);
        var runs = GetTextRuns(text, styleValue, typefaceHandle, style, typeface);
        float size = baseFont.Size;
        float totalWidth = 0f;

        foreach (var run in runs)
        {
            var font = GetCachedFont(run.Typeface, size);
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

        var (typefaceHandle, styleValue, style, typeface) = GetFontMeta(baseFont);
        var runs = GetTextRuns(text, styleValue, typefaceHandle, style, typeface);
        float size = baseFont.Size;

        if (align != SKTextAlign.Left)
        {
            // Measure the runs we already built — no second run computation per draw.
            float totalW = 0f;
            foreach (var run in runs)
            {
                totalW += GetCachedFont(run.Typeface, size).MeasureText(run.Text);
            }
            x -= align == SKTextAlign.Right ? totalW : totalW * 0.5f;
        }

        float currentX = x;
        foreach (var run in runs)
        {
            var font = GetCachedFont(run.Typeface, size);
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
            // Geist is a variable font covering every style — no style-pinned
            // fallback needed.
            return GeistTypeface;
        }

        var styleValue = StyleValue(style);
        var key = (familyName, styleValue.Weight, styleValue.Width, styleValue.Slant);
        if (_typefaceCache.TryGetValue(key, out SKTypeface? existing))
        {
            return existing;
        }

        // Eager resolve on the miss path, no closure: a per-call factory
        // closure allocates its display class on the method's entry path even
        // though the miss branch runs once per (family, style). A race loser's
        // typeface is disposed immediately (the finalizer is no owner).
        var resolved = ResolveDirectTypeface((familyName, style));
        var winner = _typefaceCache.GetOrAdd(key, resolved);
        if (!ReferenceEquals(winner, resolved))
        {
            resolved.Dispose();
        }

        return winner;
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
            FileLog.Write("Typeface lookup failed, falling back to app font");
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
    /// frame). The TryGetValue fast path keeps the 30 FPS hit allocation-free:
    /// a per-call GetOrAdd closure allocates its display class even when the
    /// entry is already present. Callers must NOT dispose the returned font.
    /// </summary>
    public static SKFont GetCachedFont(SKTypeface typeface, float size)
    {
        int sizeKey = (int)Math.Round(size * 2); // half-point resolution
        // Key by the typeface HANDLE (stable — typefaces are cached for the
        // process lifetime): the family name alone cannot distinguish
        // Regular from Bold. Bounded by the shared clear-on-overflow rule
        // (<see cref="FontCacheEviction"/>), so a long session with many
        // distinct sizes cannot grow the native-font cache without bound.
        var key = (typeface.Handle.ToInt64(), sizeKey);
        if (CachedFonts.TryGetValue(key, out SKFont? existing))
        {
            return existing;
        }

        FontCacheEviction.EvictIfFull(CachedFonts, FontCacheEviction.CachedFontLimit);
        // Value overload, no factory: a per-call closure allocates its display
        // class on the method's entry path even when the miss branch never runs.
        var created = CreateFont(typeface, size);
        var winner = CachedFonts.GetOrAdd(key, created);
        if (!ReferenceEquals(winner, created))
        {
            created.Dispose();
        }

        return winner;
    }

    /// <summary>
    /// Creates or returns the cached font for a family by name.
    /// Callers must NOT dispose the returned font.
    /// </summary>
    public static SKFont GetCachedFont(string familyName, SKFontStyle style, float size)
        => GetCachedFont(GetTypeface(familyName, style), size);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(long TypefaceHandle, int SizeKey), SKFont> CachedFonts = new();

    /// <summary>
    /// The per-font resolution of (typeface handle, style value + wrapper),
    /// keyed by the font's native handle: the interop <c>SKFont.Typeface</c> /
    /// <c>SKTypeface.FontStyle</c> properties each do interop work on every
    /// read, so the draw/measure path resolves them ONCE per font instance
    /// (fonts are cached singletons, so the one-time cost amortizes to zero)
    /// and never pays it per call. The stored wrapper pins the native typeface
    /// for the cache's lifetime (bounded by the shared clear-on-overflow rule,
    /// <see cref="FontCacheEviction"/>); the typefaces themselves are
    /// process-lifetime singletons.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (long TypefaceHandle, (int Weight, int Width, int Slant) StyleValue, SKFontStyle Style, SKTypeface? Wrapper)> FontMeta = new();

    private static (long TypefaceHandle, (int Weight, int Width, int Slant) StyleValue, SKFontStyle Style, SKTypeface? Wrapper) GetFontMeta(SKFont font)
    {
        long key = font.Handle.ToInt64();
        if (FontMeta.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var typeface = font.Typeface;
        var style = typeface?.FontStyle ?? SKFontStyle.Normal;
        var meta = (typeface?.Handle.ToInt64() ?? 0, StyleValue(style), style, typeface);
        FontCacheEviction.EvictIfFull(FontMeta, FontCacheEviction.FontMetaLimit);
        return FontMeta.GetOrAdd(key, meta);
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
