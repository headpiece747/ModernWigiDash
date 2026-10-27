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
        SteamHeaderGeometry header = SteamHeaderLayout.Compute(bounds);

        // 2. Main Work Area Split (Hero on Left ~58%, Shelf on Right ~42%)
        float contentTop = header.HeaderBounds.Bottom + 10f * scale;
        float contentBottom = bounds.Bottom - pad;
        float totalWidth = Math.Max(1f, bounds.Width - pad * 2f);

        float heroWidth = (totalWidth - pad) * 0.58f;
        SKRect heroCardBounds = new(bounds.Left + pad, contentTop, bounds.Left + pad + heroWidth, contentBottom);

        float shelfLeft = heroCardBounds.Right + pad;
        SKRect shelfBounds = new(shelfLeft, contentTop, bounds.Right - pad, contentBottom);

        // 3. Hero Card Internals: Proportional 2:3 vertical cover poster on left, game details + action CTA on right
        float heroPad = 14f * scale;
        float availHeight = heroCardBounds.Height - heroPad * 2f;
        float availWidth = heroCardBounds.Width - heroPad * 2f;

        float posterWidth = Math.Min(250f * scale, availWidth * 0.46f);
        float posterHeight = posterWidth * PosterAspect;
        if (posterHeight > availHeight)
        {
            posterHeight = availHeight;
            posterWidth = posterHeight / PosterAspect;
        }

        float posterTop = heroCardBounds.Top + heroPad + (availHeight - posterHeight) / 2f;
        SKRect heroArtBounds = new(heroCardBounds.Left + heroPad, posterTop, heroCardBounds.Left + heroPad + posterWidth, posterTop + posterHeight);

        float infoLeft = heroArtBounds.Right + 14f * scale;
        float infoRight = heroCardBounds.Right - heroPad;

        float pillHeight = 28f * scale;
        float statusPillWidth = 104f * scale;
        SKRect heroStatusPillBounds = new(infoLeft, heroArtBounds.Top, infoLeft + statusPillWidth, heroArtBounds.Top + pillHeight);

        float playtimeWidth = 82f * scale;
        SKRect heroPlaytimePillBounds = new(heroStatusPillBounds.Right + 8f * scale, heroArtBounds.Top, Math.Min(infoRight, heroStatusPillBounds.Right + 8f * scale + playtimeWidth), heroArtBounds.Top + pillHeight);

        float titleTop = heroStatusPillBounds.Bottom + 16f * scale;
        float titleHeight = 52f * scale;
        SKRect heroTitleBounds = new(infoLeft, titleTop, infoRight, titleTop + titleHeight);

        float subTop = heroTitleBounds.Bottom + 6f * scale;
        float subHeight = 22f * scale;
        SKRect heroSubtitleBounds = new(infoLeft, subTop, infoRight, subTop + subHeight);

        float actionBtnHeight = 52f * scale;
        float actionBtnY = heroArtBounds.Bottom - actionBtnHeight;
        SKRect heroActionBtnBounds = new(infoLeft, actionBtnY, infoRight, actionBtnY + actionBtnHeight);

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
            header.HeaderBounds,
            header.FilterAllBounds,
            header.FilterFavoritesBounds,
            header.FilterRecentBounds,
            header.GameCountBounds,
            header.LayoutToggleBounds,
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
