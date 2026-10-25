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
    private readonly SKPaint _panelPaint = new() { IsAntialias = true };
    private readonly SKPaint _posterPaint = new() { IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _subTextPaint = new() { IsAntialias = true };
    private readonly SKPaint _accentPaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderPaint = new() { IsAntialias = true };

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
        DrawFittedText(canvas, game.Name, card.Left + 8f, card.Bottom - 8f - fontPx, card.Width - 16f, nameFont, _textPaint);

        if (game.HoursOnRecord > 0)
        {
            string playtime = $"{game.HoursOnRecord:0}h";
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

    private static void DrawFittedText(SKCanvas canvas, string text, float x, float y, float maxWidth, SKFont font, SKPaint paint)
    {
        if (font.MeasureText(text) <= maxWidth)
        {
            canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
            return;
        }

        int length = text.Length;
        while (length > 0 && font.MeasureText(text[..length] + "…") > maxWidth)
            length--;

        string fitted = length <= 0 ? "…" : text[..Math.Max(0, length - 1)] + "…";
        canvas.DrawText(fitted, x, y, SKTextAlign.Left, font, paint);
    }
}
