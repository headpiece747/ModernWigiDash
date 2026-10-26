namespace ModernWigiDash.Widgets;

/// <summary>
/// The picture widget's draw owner: the hoisted placeholder paints, the
/// rounded-clip cache, and the image/placeholder draw paths, so the
/// widget's draw paths and their disposal live in one place (the
/// one-draw-owner rule, CONTEXT.md).
/// </summary>
internal sealed class PictureCanvas : IDisposable
{
    // Hoisted placeholder paints (the 30 FPS render allocates no SKPaint).
    private readonly SKPaint _placeholderIconPaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderLabelPaint = new() { IsAntialias = true };

    // The rounded clip path is cached per (bounds, radius): the media draw
    // clips every frame, but the geometry changes only on a resize or a
    // corner-radius edit.
    private SKPath? _clipPath;
    private SKRect _clipBounds;
    private float _clipRadius = -1f;

    private static readonly SKSamplingOptions LinearSampling = new(SKFilterMode.Linear);

    /// <summary>
    /// Draws the bitmap into the bounds, clipped to the rounded corner and
    /// scaled by the fit mode.
    /// </summary>
    public void RenderImage(SKCanvas canvas, SKRect bounds, SKBitmap bitmap, string fitMode, float cornerRadius)
    {
        if (bitmap == null) return;

        canvas.Save();
        float radius = Math.Clamp(cornerRadius, 0f, Math.Min(bounds.Width, bounds.Height) / 2f);
        if (_clipPath is null || _clipBounds != bounds
            || BitConverter.SingleToInt32Bits(_clipRadius) != BitConverter.SingleToInt32Bits(radius))
        {
            _clipBounds = bounds;
            _clipRadius = radius;
            _clipPath ??= new SKPath();
#pragma warning disable CS0618 // SKPath.Rewind/AddRoundRect are obsolete in favor of SKPathBuilder, whose Snapshot() allocates a new SKPath per call — the clip path object is reused and rebuilt instead (zero-alloc hot path).
            _clipPath.Rewind();
            _clipPath.AddRoundRect(bounds, radius, radius);
#pragma warning restore CS0618
        }
        canvas.ClipPath(_clipPath);
        canvas.DrawBitmap(bitmap, GetDrawRect(bounds, fitMode, bitmap.Width, bitmap.Height), LinearSampling);
        canvas.Restore();
    }

    /// <summary>
    /// The placeholder: the picture glyph and the source-appropriate hint.
    /// The cycle hint only applies when the source actually cycles (the
    /// policy verdict) — a single-image widget or a missing folder must not
    /// promise a tap-to-cycle behavior it cannot keep.
    /// </summary>
    public void RenderPlaceholder(SKCanvas canvas, SKRect bounds, string textColorHex, bool canCycle)
    {
        SKColor textColor = SkColorCache.ColorOf(textColorHex, SKColors.White);
        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, 36f);
        _placeholderIconPaint.Color = textColor;
        TextRenderHelper.DrawCenteredText(canvas, "🖼️", bounds.MidX, bounds.MidY - 10f, iconFont, _placeholderIconPaint);

        var labelFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 12f);
        _placeholderLabelPaint.Color = textColor;
        string hint = PictureSourcePolicy.PlaceholderHint(canCycle);
        TextRenderHelper.DrawCenteredText(canvas, hint, bounds.MidX, bounds.MidY + 25f, labelFont, _placeholderLabelPaint);
    }

    /// <summary>Drops the cached clip path (the media reset rebuilt its inputs).</summary>
    public void InvalidateClip()
    {
        _clipPath?.Dispose();
        _clipPath = null;
    }

    private SKRect GetDrawRect(SKRect bounds, string fitMode, int imgW, int imgH)
    {
        if (string.Equals(fitMode, "Stretch", StringComparison.Ordinal))
        {
            return bounds;
        }

        float scale = string.Equals(fitMode, "Contain", StringComparison.Ordinal)
            ? Math.Min(bounds.Width / imgW, bounds.Height / imgH)
            : Math.Max(bounds.Width / imgW, bounds.Height / imgH);

        float w = imgW * scale;
        float h = imgH * scale;
        return new SKRect(bounds.MidX - w / 2f, bounds.MidY - h / 2f, bounds.MidX + w / 2f, bounds.MidY + h / 2f);
    }

    /// <summary>Disposes the placeholder paints and the cached clip path.</summary>
    public void Dispose()
    {
        _clipPath?.Dispose();
        _clipPath = null;
        _placeholderIconPaint.Dispose();
        _placeholderLabelPaint.Dispose();
    }
}
