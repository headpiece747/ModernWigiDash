using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// The Hero Deck renderer: a 60/40 split with an atmospheric active hero card,
/// live process status, direct PLAY/STOP touch controls, header filter tabs,
/// and a vertical quick-switch shelf. Strictly zero heap allocations per tick.
/// </summary>
internal sealed class SteamHeroRenderer : IDisposable
{
    private const int TextCacheLimit = 64;
    private static readonly SKSamplingOptions LinearSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private readonly SKPaint _panelPaint = new() { IsAntialias = true, Color = new SKColor(18, 20, 24, 255) };
    private readonly SKPaint _cardPaint = new() { IsAntialias = true, Color = new SKColor(26, 29, 36, 255) };
    private readonly SKPaint _cardBorderPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = new SKColor(255, 255, 255, 22) };
    private readonly SKPaint _pillFillPaint = new() { IsAntialias = true, Color = new SKColor(255, 255, 255, 18) };
    private readonly SKPaint _pillBorderPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = new SKColor(255, 255, 255, 36) };
    private readonly SKPaint _activeTabPaint = new() { IsAntialias = true, Color = new SKColor(245, 158, 11, 45) };
    private readonly SKPaint _activeTabBorderPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(245, 158, 11, 220) };
    private readonly SKPaint _actionBtnPaint = new() { IsAntialias = true };
    private readonly SKPaint _posterPaint = new() { IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _subTextPaint = new() { IsAntialias = true };
    private readonly SKPaint _accentPaint = new() { IsAntialias = true };
    private readonly SKPaint _runningPaint = new() { IsAntialias = true, Color = new SKColor(34, 197, 94, 255) };
    private readonly SKPaint _glowPaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderPaint = new() { IsAntialias = true, Color = new SKColor(38, 42, 50, 255) };

    private readonly SteamHeaderRenderer _headerRenderer = new();
    private readonly Dictionary<uint, (double Hours, string Text)> _playtimeCache = [];
    private readonly Dictionary<uint, (DateTime? LastPlayed, string Text)> _subtitleCache = [];
    private readonly SteamTextFitter _textFitter = new(128);

    /// <summary>
    /// Renders the complete Hero Deck layout for the active selection.
    /// </summary>
    public void Render(
        SKCanvas canvas,
        SKRect bounds,
        SteamHeroGeometry geometry,
        SteamGameItem? hero,
        IReadOnlyList<SteamGameItem> shelfGames,
        ISteamPosterLookup posterLookup,
        SteamFilterMode activeFilter,
        SKColor accent,
        SKColor text)
    {
        // 1. Base Panel
        canvas.DrawRoundRect(bounds, 12f, 12f, _panelPaint);

        // 2. Header Bar
        _headerRenderer.DrawHeader(
            canvas,
            new SteamHeaderGeometry(
                geometry.HeaderBounds,
                geometry.FilterAllBounds,
                geometry.FilterFavoritesBounds,
                geometry.FilterRecentBounds,
                geometry.GameCountBounds,
                geometry.LayoutToggleBounds),
            activeFilter,
            SteamLayoutMode.HeroDeck,
            shelfGames.Count,
            accent,
            text);

        if (hero is null || shelfGames.Count == 0)
        {
            DrawEmptyState(canvas, bounds, text);
            return;
        }

        // 3. Hero Card (Left)
        DrawHeroCard(canvas, geometry, hero, posterLookup, accent, text);

        // 4. Shelf (Right)
        DrawShelf(canvas, geometry, shelfGames, posterLookup, accent, text);
    }

    private void DrawHeroCard(
        SKCanvas canvas,
        SteamHeroGeometry geom,
        SteamGameItem hero,
        ISteamPosterLookup posterLookup,
        SKColor accent,
        SKColor text)
    {
        // Elevated Hero Container
        canvas.DrawRoundRect(geom.HeroCardBounds, 12f, 12f, _cardPaint);
        canvas.DrawRoundRect(geom.HeroCardBounds, 12f, 12f, _cardBorderPaint);

        // Hero Artwork / Ambient Backdrop (100% Proportional, Never Stretched)
        _glowPaint.Color = accent.WithAlpha(35);
        canvas.DrawRoundRect(geom.HeroArtBounds, 10f, 10f, _glowPaint);

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(geom.HeroArtBounds, 10f, 10f), SKClipOperation.Intersect, antialias: true);
        canvas.DrawRoundRect(geom.HeroArtBounds, 10f, 10f, _placeholderPaint);

        if (posterLookup.TryGet(hero.AppId, out SKBitmap? poster) && poster is not null && poster.Width > 0 && poster.Height > 0)
        {
            float scale = Math.Min(geom.HeroArtBounds.Width / poster.Width, geom.HeroArtBounds.Height / poster.Height);
            float drawW = poster.Width * scale;
            float drawH = poster.Height * scale;
            float drawX = geom.HeroArtBounds.Left + (geom.HeroArtBounds.Width - drawW) / 2f;
            float drawY = geom.HeroArtBounds.Top + (geom.HeroArtBounds.Height - drawH) / 2f;
            SKRect fittedRect = new(drawX, drawY, drawX + drawW, drawY + drawH);

            _posterPaint.Color = SKColors.White;
            canvas.DrawBitmap(poster, fittedRect, LinearSampling, _posterPaint);
        }
        canvas.Restore();

        // Status Pill (Top of right info column)
        SKFont badgeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 12f);
        if (hero.State == SteamGameState.Running)
        {
            _activeTabPaint.Color = new SKColor(34, 197, 94, 45);
            canvas.DrawRoundRect(geom.HeroStatusPillBounds, 6f, 6f, _activeTabPaint);
            canvas.DrawCircle(geom.HeroStatusPillBounds.Left + 14f, geom.HeroStatusPillBounds.MidY, 4f, _runningPaint);
            _textPaint.Color = _runningPaint.Color;
            canvas.DrawText("RUNNING", geom.HeroStatusPillBounds.Left + 26f, geom.HeroStatusPillBounds.MidY + 4f, SKTextAlign.Left, badgeFont, _textPaint);
        }
        else
        {
            canvas.DrawRoundRect(geom.HeroStatusPillBounds, 6f, 6f, _pillFillPaint);
            canvas.DrawRoundRect(geom.HeroStatusPillBounds, 6f, 6f, _pillBorderPaint);
            _subTextPaint.Color = text.WithAlpha(200);
            canvas.DrawText("Ready to Play", geom.HeroStatusPillBounds.MidX, geom.HeroStatusPillBounds.MidY + 4f, SKTextAlign.Center, badgeFont, _subTextPaint);
        }

        // Playtime Pill (Top-Right beside status pill)
        string playtime = GetCachedPlaytimeString(hero);
        canvas.DrawRoundRect(geom.HeroPlaytimePillBounds, 6f, 6f, _pillFillPaint);
        canvas.DrawRoundRect(geom.HeroPlaytimePillBounds, 6f, 6f, _pillBorderPaint);
        _accentPaint.Color = accent;
        canvas.DrawText(playtime, geom.HeroPlaytimePillBounds.MidX, geom.HeroPlaytimePillBounds.MidY + 4f, SKTextAlign.Center, badgeFont, _accentPaint);

        // Title
        SKFont titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 22f);
        _textPaint.Color = text;
        string title = _textFitter.FitName(hero, titleFont, _textPaint, 22f, geom.HeroTitleBounds.Width);
        canvas.DrawText(title, geom.HeroTitleBounds.Left, geom.HeroTitleBounds.Top + 24f, SKTextAlign.Left, titleFont, _textPaint);

        // Subtitle / Last Played
        SKFont subFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 13f);
        _subTextPaint.Color = text.WithAlpha(160);
        string subtitle = GetCachedSubtitleString(hero);
        canvas.DrawText(subtitle, geom.HeroSubtitleBounds.Left, geom.HeroSubtitleBounds.Top + 14f, SKTextAlign.Left, subFont, _subTextPaint);

        // Primary Action CTA Button (Tactile 54px touch target)
        if (hero.State == SteamGameState.Running)
        {
            _actionBtnPaint.Color = new SKColor(239, 68, 68, 255); // Red for STOP
            canvas.DrawRoundRect(geom.HeroActionButtonBounds, 10f, 10f, _actionBtnPaint);

            SKFont btnFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 15f);
            _textPaint.Color = SKColors.White;
            canvas.DrawText("■ STOP GAME", geom.HeroActionButtonBounds.MidX, geom.HeroActionButtonBounds.MidY + 5f, SKTextAlign.Center, btnFont, _textPaint);
        }
        else
        {
            _actionBtnPaint.Color = accent; // Amber for PLAY
            canvas.DrawRoundRect(geom.HeroActionButtonBounds, 10f, 10f, _actionBtnPaint);

            SKFont btnFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 15f);
            _textPaint.Color = SKColors.Black;
            canvas.DrawText("▶ PLAY NOW", geom.HeroActionButtonBounds.MidX, geom.HeroActionButtonBounds.MidY + 5f, SKTextAlign.Center, btnFont, _textPaint);
        }
    }

    private void DrawShelf(
        SKCanvas canvas,
        SteamHeroGeometry geom,
        IReadOnlyList<SteamGameItem> games,
        ISteamPosterLookup posterLookup,
        SKColor accent,
        SKColor text)
    {
        canvas.Save();
        canvas.ClipRect(geom.ShelfBounds);

        SKFont rowTitleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 14f);
        SKFont rowSubFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 12f);

        for (int i = 0; i < geom.ShelfItems.Length; i++)
        {
            SteamShelfItemGeometry row = geom.ShelfItems[i];
            SteamGameItem game = games[row.Index];

            // Row background
            if (row.IsSelected)
            {
                canvas.DrawRoundRect(row.Bounds, 8f, 8f, _cardPaint);
                canvas.DrawRoundRect(row.Bounds, 8f, 8f, _activeTabBorderPaint);
            }
            else
            {
                canvas.DrawRoundRect(row.Bounds, 8f, 8f, _cardPaint);
                canvas.DrawRoundRect(row.Bounds, 8f, 8f, _cardBorderPaint);
            }

            // Thumbnail
            if (posterLookup.TryGet(game.AppId, out SKBitmap? thumb) && thumb is not null)
            {
                _posterPaint.Color = SKColors.White;
                canvas.Save();
                canvas.ClipRoundRect(new SKRoundRect(row.CapsuleThumb, 4f, 4f), SKClipOperation.Intersect, antialias: true);
                canvas.DrawBitmap(thumb, row.CapsuleThumb, LinearSampling, _posterPaint);
                canvas.Restore();
            }
            else
            {
                canvas.DrawRoundRect(row.CapsuleThumb, 4f, 4f, _placeholderPaint);
            }

            // Title
            _textPaint.Color = row.IsSelected ? accent : text;
            string rowTitle = _textFitter.FitName(game, rowTitleFont, _textPaint, 13f, row.TitleBounds.Width);
            canvas.DrawText(rowTitle, row.TitleBounds.Left, row.TitleBounds.Top + 14f, SKTextAlign.Left, rowTitleFont, _textPaint);

            // Sub text / Running indicator
            if (game.State == SteamGameState.Running)
            {
                canvas.DrawCircle(row.StatusBounds.Left + 4f, row.StatusBounds.MidY, 3.5f, _runningPaint);
                _textPaint.Color = _runningPaint.Color;
                canvas.DrawText("Running", row.StatusBounds.Left + 14f, row.StatusBounds.Top + 12f, SKTextAlign.Left, rowSubFont, _textPaint);
            }
            else
            {
                _subTextPaint.Color = text.WithAlpha(160);
                string rowPlaytime = GetCachedPlaytimeString(game);
                canvas.DrawText(rowPlaytime, row.StatusBounds.Left, row.StatusBounds.Top + 12f, SKTextAlign.Left, rowSubFont, _subTextPaint);
            }

            // Favorite star badge
            if (game.IsFavorite)
            {
                _accentPaint.Color = accent;
                canvas.DrawText("★", row.Bounds.Right - 16f, row.Bounds.Top + 18f, SKTextAlign.Right, rowTitleFont, _accentPaint);
            }
        }

        canvas.Restore();
    }

    private void DrawEmptyState(SKCanvas canvas, SKRect bounds, SKColor text)
    {
        SKFont font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 16f);
        _subTextPaint.Color = text.WithAlpha(160);
        canvas.DrawText("No games found in Steam library.", bounds.MidX, bounds.MidY, SKTextAlign.Center, font, _subTextPaint);
    }

    private string GetCachedPlaytimeString(SteamGameItem game)
    {
        if (_playtimeCache.TryGetValue(game.AppId, out (double Hours, string Text) entry)
            && Math.Abs(entry.Hours - game.HoursOnRecord) < 1e-9)
        {
            return entry.Text;
        }

        string str = game.HoursOnRecord > 0 ? $"{game.HoursOnRecord:0.#} hrs" : "0 hrs";
        if (_playtimeCache.Count >= TextCacheLimit)
            _playtimeCache.Clear();
        _playtimeCache[game.AppId] = (game.HoursOnRecord, str);
        return str;
    }

    private string GetCachedSubtitleString(SteamGameItem game)
    {
        if (_subtitleCache.TryGetValue(game.AppId, out (DateTime? LastPlayed, string Text) cached)
            && cached.LastPlayed == game.LastPlayedUtc)
        {
            return cached.Text;
        }

        string str = game.LastPlayedUtc.HasValue ? $"Last played: {game.LastPlayedUtc.Value:yyyy-MM-dd}" : "Never played";
        if (_subtitleCache.Count >= TextCacheLimit)
            _subtitleCache.Clear();

        _subtitleCache[game.AppId] = (game.LastPlayedUtc, str);
        return str;
    }

    public void Dispose()
    {
        _headerRenderer.Dispose();
        _panelPaint.Dispose();
        _cardPaint.Dispose();
        _cardBorderPaint.Dispose();
        _pillFillPaint.Dispose();
        _pillBorderPaint.Dispose();
        _activeTabPaint.Dispose();
        _activeTabBorderPaint.Dispose();
        _actionBtnPaint.Dispose();
        _posterPaint.Dispose();
        _textPaint.Dispose();
        _subTextPaint.Dispose();
        _accentPaint.Dispose();
        _runningPaint.Dispose();
        _glowPaint.Dispose();
        _placeholderPaint.Dispose();
    }
}
