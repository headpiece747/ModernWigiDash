using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// The Steam poster grid's draw owner: the hoisted paints (created once,
/// disposed once — never per frame), the cached-font text, the poster
/// bitmaps looked up from the <see cref="SteamPosterCache"/> (the render tick
/// only ever LOOKS UP a published texture, never decodes), and the per-cell
/// name, playtime, progress, state, and favorite marks. The cell geometry
/// comes from <see cref="SteamGridLayout"/> — the same record the touch path
/// hit-tests, so the drawn cells and the tap targets never drift.
/// </summary>
internal sealed class SteamGridRenderer : IDisposable
{
    private readonly SKPaint _panelPaint = new() { IsAntialias = true };
    private readonly SKPaint _posterPaint = new() { IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _subTextPaint = new() { IsAntialias = true };
    private readonly SKPaint _accentPaint = new() { IsAntialias = true };
    private readonly SKPaint _strokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _placeholderPaint = new() { IsAntialias = true };

    /// <summary>
    /// Draws the grid: the background panel, then every visible cell (poster
    /// or placeholder, name, playtime, the update progress bar, the running
    /// badge, and the favorite mark). A cell with no cached poster draws its
    /// placeholder — the background fetch lands the texture for a later tick.
    /// </summary>
    public void Render(
        SKCanvas canvas,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SteamPosterCache cache,
        float scrollX,
        SKColor accent,
        SKColor text)
    {
        _panelPaint.Color = new SKColor(18, 20, 24, 255);
        canvas.DrawRoundRect(bounds, 12f, 12f, _panelPaint);

        if (games.Count == 0)
        {
            DrawEmptyState(canvas, bounds, text);
            return;
        }

        int columns = SteamGridLayout.ColumnsForWidth(bounds.Width);
        SteamGridGeometry geometry = SteamGridLayout.Compute(bounds, columns, scrollX);

        for (int i = 0; i < geometry.CellCount && i < games.Count; i++)
            DrawCell(canvas, geometry.Cells[i], games[i], cache, accent, text);
    }

    /// <summary>Releases the hoisted paints (call on widget teardown).</summary>
    public void Dispose()
    {
        _panelPaint.Dispose();
        _posterPaint.Dispose();
        _textPaint.Dispose();
        _subTextPaint.Dispose();
        _accentPaint.Dispose();
        _strokePaint.Dispose();
        _placeholderPaint.Dispose();
    }

    private void DrawCell(SKCanvas canvas, SKRect cell, SteamGameItem game, SteamPosterCache cache, SKColor accent, SKColor text)
    {
        if (cache.TryGet(game.AppId, out SKBitmap? poster) && poster is not null)
        {
            _posterPaint.Color = SKColors.White;
            canvas.DrawRoundRect(cell, 8f, 8f, _posterPaint);
            canvas.DrawBitmap(poster, cell, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), _posterPaint);
        }
        else
        {
            _placeholderPaint.Color = new SKColor(38, 42, 50, 255);
            canvas.DrawRoundRect(cell, 8f, 8f, _placeholderPaint);
        }

        float fontPx = Math.Max(8f, cell.Width * 0.11f);
        SKFont nameFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontPx);
        _textPaint.Color = text;
        DrawFittedText(canvas, game.Name, cell.Left + 6f, cell.Bottom - 6f - fontPx * 0.8f, cell.Width - 12f, nameFont, _textPaint);

        if (game.State == SteamGameState.Updating)
        {
            float barY = cell.Bottom - 4f;
            _placeholderPaint.Color = new SKColor(48, 52, 60, 255);
            canvas.DrawRoundRect(new SKRect(cell.Left + 4f, barY - 4f, cell.Right - 4f, barY), 2f, 2f, _placeholderPaint);
            _accentPaint.Color = accent;
            float progressWidth = (cell.Width - 8f) * game.DownloadProgress;
            if (progressWidth > 0f)
                canvas.DrawRoundRect(new SKRect(cell.Left + 4f, barY - 4f, cell.Left + 4f + progressWidth, barY), 2f, 2f, _accentPaint);
        }

        if (game.State == SteamGameState.Running)
        {
            _accentPaint.Color = accent;
            canvas.DrawCircle(cell.Right - 10f, cell.Top + 10f, 4f, _accentPaint);
        }

        if (game.IsFavorite)
        {
            _accentPaint.Color = accent;
            canvas.DrawCircle(cell.Left + 10f, cell.Top + 10f, 4f, _accentPaint);
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
