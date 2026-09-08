namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// The font fallback resolution policy: the order in which a codepoint is
/// resolved to a typeface when the preferred typeface does not contain the
/// glyph. Pure decision over injected probes (glyph presence per candidate,
/// the matched-typeface lookup), so the ladder's shape and its early-out rule
/// are assertable without SkiaSharp native calls or a real font store.
/// FontHelper binds the production probes (the cached glyph-presence check and
/// the font manager's MatchCharacter) and routes every miss through this
/// module, so the one spelling of "which typeface covers this codepoint" lives
/// here instead of inline in the cache-miss path.
/// </summary>
public static class TypefaceResolver
{
    /// <summary>The candidate ladder for a codepoint the preferred typeface
    /// does not cover: Geist first, then the Segoe UI Emoji/Symbol/Segoe UI
    /// system fonts, then the font manager's character match, then Default.
    /// The first candidate that contains the glyph wins; an empty ladder
    /// (every probe false) yields null, which the caller maps to Default.</summary>
    public static SKTypeface? ResolveFallback(
        int codepoint,
        Func<SKTypeface, int, bool> containsGlyph,
        SKTypeface geist,
        SKTypeface emoji,
        SKTypeface symbol,
        SKTypeface segoeUi,
        Func<int, SKTypeface?> matchCharacter)
    {
        if (geist is { Handle: not 0 } && containsGlyph(geist, codepoint))
            return geist;

        if (emoji is { Handle: not 0 } && containsGlyph(emoji, codepoint))
            return emoji;

        if (symbol is { Handle: not 0 } && containsGlyph(symbol, codepoint))
            return symbol;

        if (segoeUi is { Handle: not 0 } && containsGlyph(segoeUi, codepoint))
            return segoeUi;

        try
        {
            SKTypeface? matched = matchCharacter(codepoint);
            if (matched is { Handle: not 0 })
                return matched;
        }
        catch
        {
            FileLog.Write("Font match failed, using default typeface");
        }

        return null;
    }
}
