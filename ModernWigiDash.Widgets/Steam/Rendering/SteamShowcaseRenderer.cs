using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// The Steam showcase's draw owner: the centered focal card (the focused
/// title's poster, name, and playtime) and the flanking cards (visible only
/// when a neighbor exists), plus the update progress bar and the running
/// badge. The card geometry comes from <see cref="SteamShowcaseLayout"/> —
/// the same record the touch path hit-tests. The poster bitmaps are looked
/// up from the <see cref="SteamPosterCache"/> (the render tick never decodes).
/// </summary>
internal sealed class SteamShowcaseRenderer : IDisposable
{
    /// <summary>The text-memo ceiling: the focused name and playtime labels are
    /// refitted only when the focus changes, so a steady-state frame allocates
    /// no strings.</summary>
    private const int TextCacheLimit = 32;

    private readonly SKPaint _panelPaint = new() { IsAntialias = true };
    private readonly SKPaint _posterPaint = new() { IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _subTextPaint = new() { IsAntialias = true };
    private readonly SKPaint _accentPaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderPaint = new() { IsAntialias = true };
    private readonly Dictionary<(uint AppId, int FontPx, int Width), (string Source, string Fitted)> _nameCache = [];
    private readonly Dictionary<uint, (double Hours, string Text)> _playtimeCache = [];

    /// <summary>
    /// Draws the showcase for the focused game: the background panel, the
    /// flanking cards (a neighbor's poster at reduced size, only when a
    /// neighbor exists), and the focal card (poster, name, playtime, the
    /// update progress bar, and the running badge).
    /// </summary>
    public void Render(
        SKCanvas canvas,
        SKRect bounds,
        SteamGameItem? focus,
        SteamGameItem? previous,
        SteamGameItem? next,
        SteamPosterCache cache,
        SteamPersona? persona,
        SKBitmap? avatar,
        SKColor accent,
        SKColor text)
    {
        _panelPaint.Color = new SKColor(18, 20, 24, 255);
        canvas.DrawRoundRect(bounds, 12f, 12f, _panelPaint);

        if (focus is null)
        {
            DrawEmptyState(canvas, bounds, text);
            return;
        }

        SteamShowcaseGeometry geometry = SteamShowcaseLayout.Compute(bounds, previous is not null, next is not null);

        if (previous is not null)
            DrawFlank(canvas, geometry.LeftCard, previous, cache);

        if (next is not null)
            DrawFlank(canvas, geometry.RightCard, next, cache);

        DrawFocus(canvas, geometry.FocusCard, focus, cache, accent, text);

        if (persona is not null)
            DrawPersonaBanner(canvas, bounds, persona, avatar, text);
    }

    /// <summary>Releases the hoisted paints (call on widget teardown).</summary>
    public void Dispose()
    {
        _panelPaint.Dispose();
        _posterPaint.Dispose();
        _textPaint.Dispose();
        _subTextPaint.Dispose();
        _accentPaint.Dispose();
        _placeholderPaint.Dispose();
    }

    private void DrawFlank(SKCanvas canvas, SKRect card, SteamGameItem game, SteamPosterCache cache)
    {
        if (cache.TryGet(game.AppId, out SKBitmap? poster) && poster is not null)
        {
            _posterPaint.Color = SKColors.White.WithAlpha(160);
            canvas.DrawRoundRect(card, 6f, 6f, _posterPaint);
            canvas.DrawBitmap(poster, card, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), _posterPaint);
        }
        else
        {
            _placeholderPaint.Color = new SKColor(38, 42, 50, 255);
            canvas.DrawRoundRect(card, 6f, 6f, _placeholderPaint);
        }
    }

    /// <summary>
    /// The owner's persona banner (top-right, clear of the centered focal card):
    /// the avatar (or a placeholder disc), the persona name, and the
    /// online-state dot. Drawn last so it stays visible.
    /// </summary>
    private void DrawPersonaBanner(SKCanvas canvas, SKRect bounds, SteamPersona persona, SKBitmap? avatar, SKColor text)
    {
        float pad = 12f;
        float size = 44f;
        SKRect avatarRect = new(bounds.Right - pad - size, bounds.Top + pad, bounds.Right - pad, bounds.Top + pad + size);

        if (avatar is not null)
        {
            _posterPaint.Color = SKColors.White;
            canvas.DrawRoundRect(avatarRect, 8f, 8f, _posterPaint);
            canvas.DrawBitmap(avatar, avatarRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), _posterPaint);
        }
        else
        {
            _placeholderPaint.Color = new SKColor(38, 42, 50, 255);
            canvas.DrawRoundRect(avatarRect, 8f, 8f, _placeholderPaint);
        }

        SKFont nameFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 16f);
        _textPaint.Color = text;
        canvas.DrawText(persona.PersonaName, avatarRect.Left - 8f, avatarRect.MidY, SKTextAlign.Right, nameFont, _textPaint);

        _accentPaint.Color = PersonaStateColor(persona.PersonState);
        canvas.DrawCircle(avatarRect.Right - 8f, avatarRect.Bottom - 8f, 5f, _accentPaint);
    }

    /// <summary>The online-state color for a community-API <c>personastate</c> ordinal.</summary>
    private static SKColor PersonaStateColor(int personState)
    {
        return personState switch
        {
            1 => new SKColor(86, 199, 115, 255),   // Online
            2 => new SKColor(95, 177, 255, 255),    // InGame
            3 => new SKColor(255, 107, 107, 255),   // Busy
            4 or 5 => new SKColor(255, 197, 91, 255), // Away / Snoozing
            _ => new SKColor(144, 148, 153, 255),   // Offline / unknown
        };
    }

    private void DrawFocus(SKCanvas canvas, SKRect card, SteamGameItem game, SteamPosterCache cache, SKColor accent, SKColor text)
    {
        if (cache.TryGet(game.AppId, out SKBitmap? poster) && poster is not null)
        {
            _posterPaint.Color = SKColors.White;
            canvas.DrawRoundRect(card, 10f, 10f, _posterPaint);
            canvas.DrawBitmap(poster, card, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), _posterPaint);
        }
        else
        {
            _placeholderPaint.Color = new SKColor(38, 42, 50, 255);
            canvas.DrawRoundRect(card, 10f, 10f, _placeholderPaint);
        }

        float fontPx = Math.Max(10f, card.Width * 0.13f);
        SKFont nameFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontPx);
        _textPaint.Color = text;
        string name = FitName(game, card.Width - 16f, fontPx);
        canvas.DrawText(name, card.Left + 8f, card.Bottom - 8f - fontPx, SKTextAlign.Left, nameFont, _textPaint);

        if (game.HoursOnRecord > 0)
        {
            string playtime = PlaytimeText(game);
            SKFont subFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, fontPx * 0.7f);
            _subTextPaint.Color = text.WithAlpha(180);
            canvas.DrawText(playtime, card.Left + 8f, card.Bottom - 8f - fontPx * 0.7f, SKTextAlign.Left, subFont, _subTextPaint);
        }

        if (game.State == SteamGameState.Updating)
        {
            float barY = card.Bottom - 3f;
            _placeholderPaint.Color = new SKColor(48, 52, 60, 255);
            canvas.DrawRoundRect(new SKRect(card.Left + 6f, barY - 3f, card.Right - 6f, barY), 2f, 2f, _placeholderPaint);
            _accentPaint.Color = accent;
            float progressWidth = (card.Width - 12f) * game.DownloadProgress;
            if (progressWidth > 0f)
                canvas.DrawRoundRect(new SKRect(card.Left + 6f, barY - 3f, card.Left + 6f + progressWidth, barY), 2f, 2f, _accentPaint);
        }

        if (game.State == SteamGameState.Running)
        {
            _accentPaint.Color = accent;
            canvas.DrawCircle(card.Right - 12f, card.Top + 12f, 5f, _accentPaint);
        }
    }

    private void DrawEmptyState(SKCanvas canvas, SKRect bounds, SKColor text)
    {
        SKFont font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 20f);
        _textPaint.Color = text.WithAlpha(160);
        string message = "No Steam games found";
        float width = font.MeasureText(message);
        canvas.DrawText(message, bounds.MidX - width / 2f, bounds.MidY, SKTextAlign.Left, font, _textPaint);
    }

    /// <summary>
    /// The fitted (ellipsized) name for a card, memoized per app id, font
    /// size, and available width. A steady-state frame measures and allocates
    /// nothing; the source name is compared so a renamed title refits.
    /// </summary>
    private string FitName(SteamGameItem game, float maxWidth, float fontPx)
    {
        var key = (game.AppId, (int)fontPx, (int)maxWidth);
        if (_nameCache.TryGetValue(key, out (string Source, string Fitted) entry)
            && string.Equals(entry.Source, game.Name, StringComparison.Ordinal))
        {
            return entry.Fitted;
        }

        string fitted = FitText(game.Name, maxWidth, FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontPx));
        if (_nameCache.Count >= TextCacheLimit)
            _nameCache.Clear();
        _nameCache[key] = (game.Name, fitted);
        return fitted;
    }

    /// <summary>The playtime label, memoized per app id (the hours only change
    /// when a library refresh re-reads the playtime file).</summary>
    private string PlaytimeText(SteamGameItem game)
    {
        if (_playtimeCache.TryGetValue(game.AppId, out (double Hours, string Text) entry)
            && Math.Abs(entry.Hours - game.HoursOnRecord) < 1e-9)
        {
            return entry.Text;
        }

        string text = $"{game.HoursOnRecord:0}h";
        if (_playtimeCache.Count >= TextCacheLimit)
            _playtimeCache.Clear();
        _playtimeCache[game.AppId] = (game.HoursOnRecord, text);
        return text;
    }

    /// <summary>
    /// The zero-allocation fit: the full string when it measures inside
    /// <paramref name="maxWidth"/>, else a binary-search prefix plus an
    /// ellipsis. Measuring runs through a span, so the search allocates only
    /// the one result string.
    /// </summary>
    private string FitText(string text, float maxWidth, SKFont font)
    {
        if (font.MeasureText(text, _textPaint) <= maxWidth)
            return text;

        const string Ellipsis = "…";
        float ellipsisWidth = font.MeasureText(Ellipsis, _textPaint);
        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (font.MeasureText(text.AsSpan(0, mid), _textPaint) + ellipsisWidth <= maxWidth)
                low = mid;
            else
                high = mid - 1;
        }

        return low <= 0 ? Ellipsis : string.Concat(text.AsSpan(0, low), Ellipsis.AsSpan());
    }
}
