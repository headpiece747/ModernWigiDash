using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// The Steam poster grid's draw owner: the hoisted paints (created once,
/// disposed once — never per frame), the cached-font text, the poster
/// bitmaps looked up through the <see cref="ISteamPosterLookup"/> seam (the
/// render tick only ever LOOKS UP a published texture, never decodes), and the per-cell
/// name, playtime, progress, state, and favorite marks. The cell geometry
/// comes from <see cref="SteamGridLayout"/> — the same record the touch path
/// hit-tests — and its <c>Indexes</c> map says which game each cell draws, so
/// a scrolled grid renders later titles instead of the first page shifted
/// off-screen.
/// </summary>
internal sealed class SteamGridRenderer : IDisposable
{
    /// <summary>The fitted-name cache ceiling: a bounded memo of the per-cell
    /// truncation (a steady-state frame reuses every entry, so the render tick
    /// allocates no strings).</summary>
    private const int NameCacheLimit = 512;

    private static readonly SKSamplingOptions LinearSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private readonly SKPaint _panelPaint = new() { IsAntialias = true };
    private readonly SKPaint _posterPaint = new() { IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _subTextPaint = new() { IsAntialias = true };
    private readonly SKPaint _accentPaint = new() { IsAntialias = true };
    private readonly SKPaint _strokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _placeholderPaint = new() { IsAntialias = true };
    private readonly SteamTextFitter _textFitter = new(NameCacheLimit);

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
        SteamGridGeometry geometry,
        ISteamPosterLookup cache,
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

        for (int i = 0; i < geometry.CellCount; i++)
        {
            int index = geometry.Indexes[i];
            if (index >= 0 && index < games.Count)
                DrawCell(canvas, geometry.Cells[i], games[index], cache, accent, text);
        }
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

    private void DrawCell(SKCanvas canvas, SKRect cell, SteamGameItem game, ISteamPosterLookup cache, SKColor accent, SKColor text)
    {
        if (cache.TryGet(game.AppId, out SKBitmap? poster) && poster is not null)
        {
            _posterPaint.Color = SKColors.White;
            canvas.DrawRoundRect(cell, 8f, 8f, _posterPaint);
            canvas.DrawBitmap(poster, cell, LinearSampling, _posterPaint);
        }
        else
        {
            _placeholderPaint.Color = new SKColor(38, 42, 50, 255);
            canvas.DrawRoundRect(cell, 8f, 8f, _placeholderPaint);
        }

        float fontPx = Math.Max(8f, cell.Width * 0.11f);
        SKFont nameFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontPx);
        _textPaint.Color = text;
        string name = _textFitter.FitName(game, nameFont, _textPaint, fontPx, cell.Width - 12f);
        canvas.DrawText(name, cell.Left + 6f, cell.Bottom - 6f - fontPx * 0.8f, SKTextAlign.Left, nameFont, _textPaint);

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
        => _textFitter.DrawEmptyState(canvas, bounds, text, _textPaint);
}
