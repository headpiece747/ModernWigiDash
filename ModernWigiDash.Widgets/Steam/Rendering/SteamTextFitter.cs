using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Widgets.Steam.Models;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// The Steam text-fit owner (Widgets/Steam): the one binary-search fit
/// (the full string when it measures inside the width, else a span-measured
/// prefix plus an ellipsis — the search allocates only the one result
/// string), the bounded per-cell name cache with the source-rename rule
/// (a steady-state frame reuses every entry, so the render tick allocates
/// no strings), and the library's empty-state draw. The grid and showcase
/// renderers keep drawing only; the fit, the cache policy, and the
/// empty state have one owner here.
/// </summary>
internal sealed class SteamTextFitter
{
    private const string EmptyLibraryMessage = "No Steam games found";

    private readonly int _cacheLimit;
    private readonly Dictionary<(uint AppId, int FontPx, int Width), (string Source, string Fitted)> _nameCache = [];

    /// <summary>
    /// Creates the fitter with its renderer's cache ceiling (the grid's 512
    /// per-cell entries, the showcase's 32 focused-label entries).
    /// </summary>
    public SteamTextFitter(int cacheLimit)
    {
        _cacheLimit = cacheLimit;
    }

    /// <summary>
    /// The zero-allocation fit: the full string when it measures inside
    /// <paramref name="maxWidth"/>, else a binary-search prefix plus an
    /// ellipsis. Measuring runs through a span, so the search allocates only
    /// the one result string (a fitting text is the same string, not a copy).
    /// </summary>
    public string Fit(string text, SKFont font, SKPaint paint, float maxWidth)
    {
        if (font.MeasureText(text, paint) <= maxWidth)
            return text;

        const string Ellipsis = "…";
        float ellipsisWidth = font.MeasureText(Ellipsis, paint);
        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (font.MeasureText(text.AsSpan(0, mid), paint) + ellipsisWidth <= maxWidth)
                low = mid;
            else
                high = mid - 1;
        }

        return low <= 0 ? Ellipsis : string.Concat(text.AsSpan(0, low), Ellipsis.AsSpan());
    }

    /// <summary>
    /// The cached fitted name for a card: the game's full name when it fits,
    /// else the ellipsized fit. Memoized per (app id, font px, width) so a
    /// steady-state frame measures and allocates nothing (the source name is
    /// compared to catch a rename).
    /// </summary>
    public string FitName(SteamGameItem game, SKFont font, SKPaint paint, float fontPx, float maxWidth)
    {
        var key = (game.AppId, (int)fontPx, (int)maxWidth);
        if (_nameCache.TryGetValue(key, out (string Source, string Fitted) entry)
            && string.Equals(entry.Source, game.Name, StringComparison.Ordinal))
        {
            return entry.Fitted;
        }

        string fitted = Fit(game.Name, font, paint, maxWidth);
        if (_nameCache.Count >= _cacheLimit)
            _nameCache.Clear();
        _nameCache[key] = (game.Name, fitted);
        return fitted;
    }

    /// <summary>The empty-library state: the centered message, drawn with
    /// the caller's hoisted paint (the placeholder is the per-frame state of
    /// the empty library, so the paint must not be allocated here).</summary>
    public void DrawEmptyState(SKCanvas canvas, SKRect bounds, SKColor text, SKPaint paint)
    {
        SKFont font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 20f);
        paint.Color = text.WithAlpha(160);
        float width = font.MeasureText(EmptyLibraryMessage, paint);
        canvas.DrawText(EmptyLibraryMessage, bounds.MidX - width / 2f, bounds.MidY, SKTextAlign.Left, font, paint);
    }

    /// <summary>Test seam: the live name-cache entry count (the limit
    /// eviction is observable as a count drop, not from the cache itself).</summary>
    internal int CacheCountForTest => _nameCache.Count;
}
