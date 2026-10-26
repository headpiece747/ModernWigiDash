namespace ModernWigiDash.Widgets;

/// <summary>
/// Encapsulates zero-allocation Skia rendering for the hotkey button widget.
/// Hoists all 3 <see cref="SKPaint"/> instances and renders the pressed-state glow,
/// button label typography, secondary description, and vector/SVG icons.
/// </summary>
internal sealed class HotkeyButtonCanvas : IDisposable
{
    // Hoisted paints: the colors mutate per render (property-driven), so the
    // 30 FPS render allocates no SKPaint. DrawLabelOnly shares the text pair.
    private readonly SKPaint _fillPaint = new() { IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _descriptionPaint = new() { IsAntialias = true };

    /// <summary>
    /// Draws the button: pressed-state glow, label, description, and icon (or text-only fallback).
    /// </summary>
    public void Render(
        SKCanvas canvas,
        SKRect bounds,
        bool isPressed,
        string label,
        string description,
        bool hasIconGeometry,
        SKPoint iconCenter,
        float half,
        bool useCustomFile,
        SKPath? resolvedPath,
        string griddyIconName,
        SKColor btnColor,
        SKColor textColor,
        SKColor iconColor)
    {
        if (isPressed)
        {
            _fillPaint.Color = btnColor.WithAlpha(180);
            canvas.DrawRoundRect(bounds, 16f, 16f, _fillPaint);
        }

        if (!hasIconGeometry)
        {
            DrawLabelOnly(canvas, bounds, label, textColor, description);
            return;
        }

        float iconSize = half * 2f;

        // Draw label and description first so the icon can render in front of them
        float labelSize = Math.Min(bounds.Width / 7f, bounds.Height / 7f);
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, labelSize);
        _textPaint.Color = textColor;
        var textBounds = new SKRect();
        font.MeasureText(label, out textBounds, _textPaint);
        canvas.DrawTextWithFallback(label, bounds.MidX - textBounds.Width / 2f,
            bounds.Top + bounds.Height * 0.78f, font, _textPaint);

        if (!string.IsNullOrWhiteSpace(description))
        {
            var descriptionFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Max(10f, labelSize * 0.6f));
            _descriptionPaint.Color = textColor.WithAlpha(180);
            descriptionFont.MeasureText(description, out var descriptionBounds, _descriptionPaint);
            canvas.DrawTextWithFallback(description, bounds.MidX - descriptionBounds.Width / 2f,
                bounds.Bottom - Math.Max(8f, labelSize * 0.4f), descriptionFont, _descriptionPaint);
        }

        // Icon drawn last so it stays in front of the text when overlapped
        if (useCustomFile)
        {
            if (resolvedPath != null)
            {
                SvgIconLoader.Draw(canvas, resolvedPath, iconCenter, iconSize, iconColor, 0, 0);
            }
        }
        else
        {
            GriddyIcons.Draw(canvas, griddyIconName, iconCenter, iconSize, iconColor, 0, 0);
        }
    }

    private void DrawLabelOnly(SKCanvas canvas, SKRect bounds, string label, SKColor textColor, string description)
    {
        float fontSize = Math.Min(bounds.Width / 6f, bounds.Height / 5f);
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontSize);
        _textPaint.Color = textColor;

        var textBounds = new SKRect();
        font.MeasureText(label, out textBounds, _textPaint);
        canvas.DrawTextWithFallback(label, bounds.MidX - textBounds.Width / 2f, bounds.MidY - textBounds.Height / 4f, font, _textPaint);

        if (!string.IsNullOrWhiteSpace(description))
        {
            var descriptionFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Max(10f, fontSize * 0.42f));
            _descriptionPaint.Color = textColor.WithAlpha(180);
            descriptionFont.MeasureText(description, out var descriptionBounds, _descriptionPaint);
            canvas.DrawTextWithFallback(description, bounds.MidX - descriptionBounds.Width / 2f,
                bounds.Bottom - Math.Max(12f, fontSize * 0.65f), descriptionFont, _descriptionPaint);
        }
    }

    /// <summary>Disposes all hoisted Skia paints.</summary>
    public void Dispose()
    {
        _fillPaint.Dispose();
        _textPaint.Dispose();
        _descriptionPaint.Dispose();
    }
}
