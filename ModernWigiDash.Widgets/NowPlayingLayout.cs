namespace ModernWigiDash.Widgets;

/// <summary>The touch zones of the Now Playing widget, in precedence order.</summary>
public enum NowPlayingHitAction
{
    /// <summary>The point is not on any control.</summary>
    None,

    /// <summary>The shuffle button — tap toggles shuffle.</summary>
    Shuffle,

    /// <summary>The previous-track button.</summary>
    Previous,

    /// <summary>The play/pause hero button.</summary>
    PlayPause,

    /// <summary>The next-track button.</summary>
    Next,

    /// <summary>The repeat button — tap cycles the repeat mode.</summary>
    Repeat,

    /// <summary>The source badge — tap switches the media source.</summary>
    SourceBadge,

    /// <summary>The progress band — tap seeks.</summary>
    Seek,
}

/// <summary>
/// The Now Playing widget's hit geometry, computed once per frame from the
/// placement bounds, the uniform scale, the source-badge visibility, and the
/// measured badge label width — the same inputs the render path uses, so the
/// drawn controls and the touch targets can never drift apart. Render draws
/// from this record and OnTouch hit-tests the same record. The record also
/// carries the scaled <see cref="Pad"/> and <see cref="ArtGap"/> the render
/// path draws with — the 24/30 design constants live only in
/// <see cref="NowPlayingLayout.Compute"/>, never re-derived at a draw site.
/// </summary>
public readonly record struct NowPlayingGeometry(
    SKRect ShuffleButton,
    SKRect PreviousButton,
    SKRect PlayPauseButton,
    SKRect NextButton,
    SKRect RepeatButton,
    SKRect SourceBadgeRect,
    bool SourceBadgeVisible,
    float ProgressLeft,
    float ProgressWidth,
    float ProgressY,
    float SeekTolerance,
    float ArtSide,
    float Pad,
    float ArtGap);

/// <summary>
/// Pure layout rules for the Now Playing widget: the design-space scale base,
/// the art-side split, the source badge, the progress band, the centered
/// control row, the touch-zone hit test, and the background color blend.
/// Moved out of the widget's render and touch paths so the drawn geometry and
/// the tap targets share one source of truth.
/// </summary>
public static class NowPlayingLayout
{
    /// <summary>The widget's design-space width — the scale base for both render and touch.
    /// Aliased from <see cref="DisplayGeometry"/> — the shared single source for
    /// the framebuffer geometry, so the design base can never drift from the
    /// display's pixel area.</summary>
    public const float DesignWidth = DisplayGeometry.FramebufferWidth;

    /// <summary>The widget's design-space height — the scale base for both render and touch.
    /// Aliased from <see cref="DisplayGeometry"/> — the shared single source for
    /// the framebuffer geometry, so the design base can never drift from the
    /// display's pixel area.</summary>
    public const float DesignHeight = DisplayGeometry.FramebufferHeight;

    /// <summary>The vertical distance from the progress bar line a seek tap may land.</summary>
    public const float SeekTolerance = 24f;

    /// <summary>
    /// The frame's geometry for a placement: the five control buttons, the
    /// source badge (computed unconditionally so the drawn and hit-tested
    /// rects never drift; <see cref="NowPlayingGeometry.SourceBadgeVisible"/>
    /// gates the hit test), the progress band with its seek tolerance, the
    /// art-side split, and the scaled pad / art-gap the render path draws
    /// with. <paramref name="badgeTextWidth"/> is the measured badge label
    /// width — the one font-dependent input, supplied by the render path.
    /// </summary>
    public static NowPlayingGeometry Compute(SKRect bounds, float scale, bool showSourceBadge, float badgeTextWidth)
    {
        float pad = 24f * scale;
        float artGap = 30f * scale;
        float controlRowWidth = (48f * 4f + 58f + 28f * 4f) * scale;
        float widthLimit = bounds.Width - pad * 2f - artGap - controlRowWidth;
        float artSide = Math.Max(0f, Math.Min(bounds.Height - pad * 2f, widthLimit));

        float badgeH = 26f * scale;
        float badgeW = badgeTextWidth + 24f * scale;
        float badgeX = bounds.Right - pad - badgeW;
        float badgeY = bounds.Top + pad + 2f * scale;
        var badge = new SKRect(badgeX, badgeY, badgeX + badgeW, badgeY + badgeH);

        float left = bounds.Left + pad + artSide + artGap;
        float right = bounds.Right - pad;
        float barY = bounds.Bottom - pad - 92f * scale;
        float barW = right - left;

        float btnY = bounds.Bottom - pad - 32f * scale;
        float btnSize = 48f * scale;
        float ppSize = 58f * scale;
        float btnGap = 28f * scale;
        float totalW = btnSize * 4f + ppSize + btnGap * 4f;
        float startX = left + Math.Max(0f, (right - left - totalW) / 2f);

        float shuffleX = startX;
        float prevX = shuffleX + btnSize + btnGap;
        float ppX = prevX + btnSize + btnGap;
        float nextX = ppX + ppSize + btnGap;
        float repeatX = nextX + btnSize + btnGap;

        var shuffle = new SKRect(shuffleX, btnY - btnSize / 2f, shuffleX + btnSize, btnY + btnSize / 2f);
        var prev = new SKRect(prevX, btnY - btnSize / 2f, prevX + btnSize, btnY + btnSize / 2f);
        var pp = new SKRect(ppX, btnY - ppSize / 2f, ppX + ppSize, btnY + ppSize / 2f);
        var next = new SKRect(nextX, btnY - btnSize / 2f, nextX + btnSize, btnY + btnSize / 2f);
        var repeat = new SKRect(repeatX, btnY - btnSize / 2f, repeatX + btnSize, btnY + btnSize / 2f);

        return new NowPlayingGeometry(
            shuffle, prev, pp, next, repeat,
            badge, showSourceBadge,
            left, barW, barY, SeekTolerance, artSide,
            pad, artGap);
    }

    /// <summary>
    /// Hit-tests a touch point against the control row, the source badge (only
    /// when visible), and the progress band, in the widget's precedence order.
    /// The capability gates (CanShuffle, CanSeek, …) are the widget's policy —
    /// this is pure geometry.
    /// </summary>
    public static NowPlayingHitAction GetAction(NowPlayingGeometry layout, SKPoint point)
    {
        if (layout.ShuffleButton.Contains(point)) return NowPlayingHitAction.Shuffle;
        if (layout.PreviousButton.Contains(point)) return NowPlayingHitAction.Previous;
        if (layout.PlayPauseButton.Contains(point)) return NowPlayingHitAction.PlayPause;
        if (layout.NextButton.Contains(point)) return NowPlayingHitAction.Next;
        if (layout.RepeatButton.Contains(point)) return NowPlayingHitAction.Repeat;
        if (layout.SourceBadgeVisible && layout.SourceBadgeRect.Contains(point)) return NowPlayingHitAction.SourceBadge;
        if (layout.ProgressWidth > 0f
            && Math.Abs(point.Y - layout.ProgressY) <= layout.SeekTolerance
            && point.X >= layout.ProgressLeft
            && point.X <= layout.ProgressLeft + layout.ProgressWidth)
        {
            return NowPlayingHitAction.Seek;
        }
        return NowPlayingHitAction.None;
    }

    /// <summary>
    /// Blends <paramref name="from"/> toward <paramref name="to"/> by the
    /// clamped amount, preserving the source alpha. Used for the background
    /// panel tint derived from the artwork color.
    /// </summary>
    internal static SKColor BlendToward(SKColor from, SKColor to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new SKColor(
            (byte)(from.Red + (to.Red - from.Red) * amount),
            (byte)(from.Green + (to.Green - from.Green) * amount),
            (byte)(from.Blue + (to.Blue - from.Blue) * amount),
            from.Alpha);
    }

    // The control-icon path geometry: each icon's SKPath is a pure function of
    // its button rect, so the drawing math lives with the layout (the widget
    // owns only the native-handle lifecycle — when to rebuild and dispose).
    // The shuffle rect alone keys a rebuild, so SameRect compares bit-exact
    // (a float drift of one ulp must not force a needless rebuild).

    /// <summary>Bit-exact rect equality (the icon-path rebuild key).</summary>
    internal static bool SameRect(SKRect a, SKRect b)
        => BitConverter.SingleToInt32Bits(a.Left) == BitConverter.SingleToInt32Bits(b.Left)
        && BitConverter.SingleToInt32Bits(a.Top) == BitConverter.SingleToInt32Bits(b.Top)
        && BitConverter.SingleToInt32Bits(a.Right) == BitConverter.SingleToInt32Bits(b.Right)
        && BitConverter.SingleToInt32Bits(a.Bottom) == BitConverter.SingleToInt32Bits(b.Bottom);

    internal static SKPath BuildPrevTriangle(SKRect r)
    {
        float cx = r.MidX, cy = r.MidY;
        float h = r.Height * 0.32f;
        float barW = r.Width * 0.08f;
        float gap = r.Width * 0.06f;

        using var tri = new SKPathBuilder();
        tri.MoveTo(cx + r.Width * 0.20f, cy - h);
        tri.LineTo(cx - r.Width * 0.22f + barW + gap, cy);
        tri.LineTo(cx + r.Width * 0.20f, cy + h);
        tri.Close();
        return tri.Detach();
    }

    internal static SKPath BuildPlayTriangle(SKRect r)
    {
        float cx = r.MidX + r.Width * 0.03f, cy = r.MidY;
        float h = r.Height * 0.32f;
        float w = r.Width * 0.28f;

        using var path = new SKPathBuilder();
        path.MoveTo(cx - w * 0.7f, cy - h);
        path.LineTo(cx + w, cy);
        path.LineTo(cx - w * 0.7f, cy + h);
        path.Close();
        return path.Detach();
    }

    internal static SKPath BuildNextTriangle(SKRect r)
    {
        float cx = r.MidX, cy = r.MidY;
        float h = r.Height * 0.32f;
        float barW = r.Width * 0.08f;
        float gap = r.Width * 0.06f;

        using var tri = new SKPathBuilder();
        tri.MoveTo(cx - r.Width * 0.20f, cy - h);
        tri.LineTo(cx + r.Width * 0.22f - barW - gap, cy);
        tri.LineTo(cx - r.Width * 0.20f, cy + h);
        tri.Close();
        return tri.Detach();
    }

    internal static SKPath BuildShuffleCurves(SKRect r)
    {
        float cx = r.MidX, cy = r.MidY;
        float w = r.Width * 0.20f;
        float h = r.Height * 0.20f;

        using var p = new SKPathBuilder();
        p.MoveTo(cx - w, cy - h);
        p.CubicTo(cx - w * 0.2f, cy - h, cx + w * 0.2f, cy + h, cx + w, cy + h);
        p.MoveTo(cx - w, cy + h);
        p.CubicTo(cx - w * 0.2f, cy + h, cx + w * 0.2f, cy - h, cx + w, cy - h);
        return p.Detach();
    }

    internal static SKPath BuildShuffleArrow(SKRect r, bool top)
    {
        float cx = r.MidX, cy = r.MidY;
        float w = r.Width * 0.20f;
        float h = r.Height * 0.20f;
        float ah = r.Height * 0.12f;

        using var arr = new SKPathBuilder();
        if (top)
        {
            arr.MoveTo(cx + w, cy - h);
            arr.LineTo(cx + w - ah, cy - h - ah * 0.7f);
            arr.LineTo(cx + w - ah, cy - h + ah * 0.7f);
        }
        else
        {
            arr.MoveTo(cx + w, cy + h);
            arr.LineTo(cx + w - ah, cy + h - ah * 0.7f);
            arr.LineTo(cx + w - ah, cy + h + ah * 0.7f);
        }
        arr.Close();
        return arr.Detach();
    }

    internal static SKPath BuildRepeatArrow(SKRect r)
    {
        float cx = r.MidX, cy = r.MidY;
        float outer = r.Width * 0.22f;
        float endDeg = 305f * MathF.PI / 180f;
        float tipX = cx + outer * MathF.Cos(endDeg);
        float tipY = cy + outer * MathF.Sin(endDeg);
        float tx = -MathF.Sin(endDeg);
        float ty = MathF.Cos(endDeg);
        float s = r.Width * 0.09f;

        using var tri = new SKPathBuilder();
        tri.MoveTo(tipX + tx * s, tipY + ty * s);
        tri.LineTo(tipX - tx * s * 0.35f - ty * s * 0.6f, tipY - ty * s * 0.35f + tx * s * 0.6f);
        tri.LineTo(tipX - tx * s * 0.35f + ty * s * 0.6f, tipY - ty * s * 0.35f - tx * s * 0.6f);
        tri.Close();
        return tri.Detach();
    }
}
