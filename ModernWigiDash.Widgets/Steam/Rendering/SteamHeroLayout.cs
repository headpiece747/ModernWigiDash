using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// Pure layout calculator for the Steam Hero Deck mode: 60/40 hero + shelf layout,
/// header pills, and pixel-accurate hit testing for touch controls.
/// </summary>
internal static class SteamHeroLayout
{
    /// <summary>The design-space width — the scale base for both render and touch.</summary>
    public const float DesignWidth = DisplayGeometry.FramebufferWidth;

    /// <summary>The design-space height — the scale base for both render and touch.</summary>
    public const float DesignHeight = DisplayGeometry.FramebufferHeight;

    /// <summary>The standard 2:3 vertical capsule poster aspect ratio.</summary>
    public const float PosterAspect = 1.5f;

    /// <summary>
    /// Computes the complete immutable geometry record for the Hero Deck presentation.
    /// </summary>
    public static SteamHeroGeometry Compute(
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        uint selectedAppId,
        float scrollY = 0f)
    {
        float scale = bounds.Height > 0 ? bounds.Height / DesignHeight : 1f;
        float pad = 12f * scale;

        // 1. Header Bar
        float headerHeight = 38f * scale;
        SKRect headerBounds = new(bounds.Left + pad, bounds.Top + pad, bounds.Right - pad, bounds.Top + pad + headerHeight);

        float pillHeight = 32f * scale;
        float pillY = headerBounds.Top + (headerHeight - pillHeight) / 2f;

        float allWidth = 64f * scale;
        SKRect filterAllBounds = new(headerBounds.Left, pillY, headerBounds.Left + allWidth, pillY + pillHeight);

        float favWidth = 88f * scale;
        SKRect filterFavBounds = new(filterAllBounds.Right + 8f * scale, pillY, filterAllBounds.Right + 8f * scale + favWidth, pillY + pillHeight);

        float recWidth = 78f * scale;
        SKRect filterRecBounds = new(filterFavBounds.Right + 8f * scale, pillY, filterFavBounds.Right + 8f * scale + recWidth, pillY + pillHeight);

        float modeWidth = 100f * scale;
        SKRect layoutToggleBounds = new(headerBounds.Right - modeWidth, pillY, headerBounds.Right, pillY + pillHeight);

        float countWidth = 72f * scale;
        SKRect gameCountBounds = new(layoutToggleBounds.Left - 8f * scale - countWidth, pillY, layoutToggleBounds.Left - 8f * scale, pillY + pillHeight);

        // 2. Main Work Area Split (Hero on Left ~58%, Shelf on Right ~42%)
        float contentTop = headerBounds.Bottom + 10f * scale;
        float contentBottom = bounds.Bottom - pad;
        float contentHeight = Math.Max(1f, contentBottom - contentTop);
        float totalWidth = Math.Max(1f, bounds.Width - pad * 2f);

        float heroWidth = (totalWidth - pad) * 0.58f;
        SKRect heroCardBounds = new(bounds.Left + pad, contentTop, bounds.Left + pad + heroWidth, contentBottom);

        float shelfLeft = heroCardBounds.Right + pad;
        SKRect shelfBounds = new(shelfLeft, contentTop, bounds.Right - pad, contentBottom);

        // 3. Hero Card Internals
        float heroPad = 14f * scale;
        float artHeight = Math.Max(1f, contentHeight * 0.44f);
        SKRect heroArtBounds = new(heroCardBounds.Left + heroPad, heroCardBounds.Top + heroPad, heroCardBounds.Right - heroPad, heroCardBounds.Top + heroPad + artHeight);

        float statusPillWidth = 110f * scale;
        float statusPillHeight = 26f * scale;
        SKRect heroStatusPillBounds = new(heroArtBounds.Left + 8f * scale, heroArtBounds.Top + 8f * scale, heroArtBounds.Left + 8f * scale + statusPillWidth, heroArtBounds.Top + 8f * scale + statusPillHeight);

        float playtimeWidth = 86f * scale;
        SKRect heroPlaytimePillBounds = new(heroArtBounds.Right - 8f * scale - playtimeWidth, heroArtBounds.Top + 8f * scale, heroArtBounds.Right - 8f * scale, heroArtBounds.Top + 8f * scale + statusPillHeight);

        float titleTop = heroArtBounds.Bottom + 14f * scale;
        float titleHeight = 32f * scale;
        SKRect heroTitleBounds = new(heroCardBounds.Left + heroPad, titleTop, heroCardBounds.Right - heroPad, titleTop + titleHeight);

        float subtitleHeight = 20f * scale;
        SKRect heroSubtitleBounds = new(heroCardBounds.Left + heroPad, heroTitleBounds.Bottom + 4f * scale, heroCardBounds.Right - heroPad, heroTitleBounds.Bottom + 4f * scale + subtitleHeight);

        float actionBtnHeight = 52f * scale;
        float actionBtnWidth = 170f * scale;
        float actionBtnY = heroCardBounds.Bottom - heroPad - actionBtnHeight;
        SKRect heroActionBtnBounds = new(heroCardBounds.Left + heroPad, actionBtnY, heroCardBounds.Left + heroPad + actionBtnWidth, actionBtnY + actionBtnHeight);

        // 4. Shelf Rows
        float rowHeight = 64f * scale;
        float rowSpacing = 6f * scale;
        float totalShelfContentHeight = games.Count * (rowHeight + rowSpacing);
        float maxScrollY = Math.Max(0f, totalShelfContentHeight - shelfBounds.Height);
        float clampedScrollY = Math.Clamp(scrollY, 0f, maxScrollY);

        SteamShelfItemGeometry[] shelfItems = new SteamShelfItemGeometry[games.Count];
        float thumbWidth = 36f * scale;
        float thumbHeight = thumbWidth * PosterAspect;

        for (int i = 0; i < games.Count; i++)
        {
            SteamGameItem game = games[i];
            float itemY = shelfBounds.Top + i * (rowHeight + rowSpacing) - clampedScrollY;
            SKRect itemBounds = new(shelfBounds.Left, itemY, shelfBounds.Right, itemY + rowHeight);

            float thumbY = itemBounds.Top + (rowHeight - thumbHeight) / 2f;
            SKRect thumbBounds = new(itemBounds.Left + 8f * scale, thumbY, itemBounds.Left + 8f * scale + thumbWidth, thumbY + thumbHeight);

            float textLeft = thumbBounds.Right + 10f * scale;
            float textRight = itemBounds.Right - 8f * scale;
            SKRect rowTitleBounds = new(textLeft, itemBounds.Top + 12f * scale, textRight, itemBounds.Top + 32f * scale);
            SKRect rowStatusBounds = new(textLeft, itemBounds.Top + 34f * scale, textRight, itemBounds.Bottom - 10f * scale);

            bool isSelected = game.AppId == selectedAppId || (selectedAppId == 0 && i == 0);
            shelfItems[i] = new SteamShelfItemGeometry(i, game.AppId, itemBounds, thumbBounds, rowTitleBounds, rowStatusBounds, isSelected);
        }

        return new SteamHeroGeometry(
            bounds,
            headerBounds,
            filterAllBounds,
            filterFavBounds,
            filterRecBounds,
            gameCountBounds,
            layoutToggleBounds,
            heroCardBounds,
            heroArtBounds,
            heroStatusPillBounds,
            heroPlaytimePillBounds,
            heroTitleBounds,
            heroSubtitleBounds,
            heroActionBtnBounds,
            shelfBounds,
            shelfItems,
            clampedScrollY,
            maxScrollY);
    }

    /// <summary>
    /// Hit-tests a touch point against the Hero Deck geometry.
    /// </summary>
    public static (SteamHeroHit Hit, int ShelfIndex) HitTest(SteamHeroGeometry geometry, SKPoint point)
    {
        if (geometry.LayoutToggleBounds.Contains(point))
            return (SteamHeroHit.LayoutToggle, -1);

        if (geometry.FilterAllBounds.Contains(point))
            return (SteamHeroHit.FilterAll, -1);

        if (geometry.FilterFavoritesBounds.Contains(point))
            return (SteamHeroHit.FilterFavorites, -1);

        if (geometry.FilterRecentBounds.Contains(point))
            return (SteamHeroHit.FilterRecent, -1);

        if (geometry.HeroActionButtonBounds.Contains(point))
            return (SteamHeroHit.HeroAction, -1);

        if (geometry.ShelfBounds.Contains(point))
        {
            for (int i = 0; i < geometry.ShelfItems.Length; i++)
            {
                if (geometry.ShelfItems[i].Bounds.Contains(point))
                    return (SteamHeroHit.ShelfItem, i);
            }
        }

        if (geometry.HeroCardBounds.Contains(point))
            return (SteamHeroHit.HeroCard, -1);

        return (SteamHeroHit.None, -1);
    }
}
