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
/// from this record and OnTouch hit-tests the same record.
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
    float ArtSide);

/// <summary>
/// Pure layout rules for the Now Playing widget: the design-space scale base,
/// the art-side split, the source badge, the progress band, the centered
/// control row, the touch-zone hit test, and the background color blend.
/// Moved out of the widget's render and touch paths so the drawn geometry and
/// the tap targets share one source of truth.
/// </summary>
public static class NowPlayingLayout
{
    /// <summary>The widget's design-space width — the scale base for both render and touch.</summary>
    public const float DesignWidth = 1016f;

    /// <summary>The widget's design-space height — the scale base for both render and touch.</summary>
    public const float DesignHeight = 592f;

    /// <summary>The vertical distance from the progress bar line a seek tap may land.</summary>
    public const float SeekTolerance = 24f;

    /// <summary>
    /// The frame's geometry for a placement: the five control buttons, the
    /// source badge (computed unconditionally so the drawn and hit-tested
    /// rects never drift; <see cref="NowPlayingGeometry.SourceBadgeVisible"/>
    /// gates the hit test), the progress band with its seek tolerance, and the
    /// art-side split. <paramref name="badgeTextWidth"/> is the measured badge
    /// label width — the one font-dependent input, supplied by the render path.
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
            left, barW, barY, SeekTolerance, artSide);
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
}
