namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// The not-ready panel the Steam library widget draws while its service is
/// not bound: one hoisted paint and the rounded background, so the widget's
/// draw paths all live in owned draw-owners (the one-draw-owner rule,
/// CONTEXT.md).
/// </summary>
internal sealed class SteamNotReadyRenderer : IDisposable
{
    private readonly SKPaint _bgPaint = new()
    {
        Color = new SKColor(18, 20, 24, 255),
        IsAntialias = true,
    };

    /// <summary>Draws the rounded dark background into the bounds.</summary>
    public void Draw(SKCanvas canvas, SKRect bounds)
    {
        canvas.DrawRoundRect(bounds, 12f, 12f, _bgPaint);
    }

    /// <summary>Disposes the hoisted paint.</summary>
    public void Dispose() => _bgPaint.Dispose();
}
