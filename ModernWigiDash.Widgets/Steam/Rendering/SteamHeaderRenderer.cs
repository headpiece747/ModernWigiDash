using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Widgets.Steam.Models;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// Reusable, zero-allocation renderer for the shared Steam Library header bar.
/// Uses standard ASCII glyphs to avoid tofu/missing character boxes on custom fonts.
/// </summary>
internal sealed class SteamHeaderRenderer : IDisposable
{
    private readonly SKPaint _pillFillPaint = new() { IsAntialias = true, Color = new SKColor(255, 255, 255, 18) };
    private readonly SKPaint _pillBorderPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = new SKColor(255, 255, 255, 36) };
    private readonly SKPaint _activeTabPaint = new() { IsAntialias = true, Color = new SKColor(245, 158, 11, 45) };
    private readonly SKPaint _activeTabBorderPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(245, 158, 11, 220) };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _subTextPaint = new() { IsAntialias = true };
    private readonly SKPaint _accentPaint = new() { IsAntialias = true };

    private readonly Dictionary<int, string> _countCache = [];

    public void DrawHeader(
        SKCanvas canvas,
        SteamHeaderGeometry geom,
        SteamFilterMode filter,
        SteamLayoutMode mode,
        int gameCount,
        SKColor accent,
        SKColor text)
    {
        SKFont pillFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 13f);

        // Filter Pills: All, Favorites, Recent (clean ASCII labels, no tofu glyphs)
        DrawFilterPill(canvas, geom.FilterAllBounds, "All", filter == SteamFilterMode.All, pillFont, accent, text);
        DrawFilterPill(canvas, geom.FilterFavoritesBounds, "Favorites", filter == SteamFilterMode.Favorites, pillFont, accent, text);
        DrawFilterPill(canvas, geom.FilterRecentBounds, "Recent", filter == SteamFilterMode.Recent, pillFont, accent, text);

        // Game Count Badge
        string countText = GetCachedCountString(gameCount);
        canvas.DrawRoundRect(geom.GameCountBounds, 8f, 8f, _pillFillPaint);
        _subTextPaint.Color = text.WithAlpha(170);
        canvas.DrawText(countText, geom.GameCountBounds.MidX, geom.GameCountBounds.MidY + 4f, SKTextAlign.Center, pillFont, _subTextPaint);

        // Mode Switch Pill: Hero, Showcase, Grid (clean text with amber border)
        string modeLabel = mode switch
        {
            SteamLayoutMode.HeroDeck => "Hero",
            SteamLayoutMode.Showcase => "Showcase",
            _ => "Grid",
        };

        canvas.DrawRoundRect(geom.LayoutToggleBounds, 8f, 8f, _pillFillPaint);
        _activeTabBorderPaint.Color = accent;
        canvas.DrawRoundRect(geom.LayoutToggleBounds, 8f, 8f, _activeTabBorderPaint);
        _accentPaint.Color = accent;
        canvas.DrawText(modeLabel, geom.LayoutToggleBounds.MidX, geom.LayoutToggleBounds.MidY + 4f, SKTextAlign.Center, pillFont, _accentPaint);
    }

    private void DrawFilterPill(
        SKCanvas canvas,
        SKRect bounds,
        string label,
        bool isActive,
        SKFont font,
        SKColor accent,
        SKColor text)
    {
        if (isActive)
        {
            _activeTabPaint.Color = accent.WithAlpha(40);
            _activeTabBorderPaint.Color = accent;
            canvas.DrawRoundRect(bounds, 8f, 8f, _activeTabPaint);
            canvas.DrawRoundRect(bounds, 8f, 8f, _activeTabBorderPaint);
            _textPaint.Color = accent;
        }
        else
        {
            canvas.DrawRoundRect(bounds, 8f, 8f, _pillFillPaint);
            canvas.DrawRoundRect(bounds, 8f, 8f, _pillBorderPaint);
            _textPaint.Color = text.WithAlpha(180);
        }

        canvas.DrawText(label, bounds.MidX, bounds.MidY + 4f, SKTextAlign.Center, font, _textPaint);
    }

    private string GetCachedCountString(int count)
    {
        if (!_countCache.TryGetValue(count, out string? cached))
        {
            if (_countCache.Count >= 32)
                _countCache.Clear();

            cached = count == 1 ? "1 Game" : $"{count} Games";
            _countCache[count] = cached;
        }
        return cached;
    }

    public void Dispose()
    {
        _pillFillPaint.Dispose();
        _pillBorderPaint.Dispose();
        _activeTabPaint.Dispose();
        _activeTabBorderPaint.Dispose();
        _textPaint.Dispose();
        _subTextPaint.Dispose();
        _accentPaint.Dispose();
    }
}
