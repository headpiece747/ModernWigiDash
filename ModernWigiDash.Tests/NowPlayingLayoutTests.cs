namespace ModernWigiDash.Tests;

/// <summary>
/// NowPlayingLayout — the Now Playing widget's geometry module. The control
/// row, source badge, progress band, and art-side split are computed once per
/// frame and shared by the render and touch paths, so the drawn controls and
/// the tap targets can never drift apart. The rect math is pinned here; the
/// widget's live-session touch tests (NowPlayingWidgetLiveSessionTests) assert
/// the behavior those rects produce.
/// </summary>
[TestClass]
public class NowPlayingLayoutTests
{
    private static void AssertRect(SKRect actual, float left, float top, float right, float bottom)
    {
        Assert.AreEqual(left, actual.Left, 0.01f, "Left");
        Assert.AreEqual(top, actual.Top, 0.01f, "Top");
        Assert.AreEqual(right, actual.Right, 0.01f, "Right");
        Assert.AreEqual(bottom, actual.Bottom, 0.01f, "Bottom");
    }

    [TestMethod]
    public void Compute_DesignSizeScale1_PinsEveryRect()
    {
        var layout = NowPlayingLayout.Compute(new SKRect(0, 0, 1016, 592), 1f, showSourceBadge: true, badgeTextWidth: 100f);

        Assert.AreEqual(544f, layout.ArtSide, 0.01f, "art column ends at the 24px pad, bounded by the control row width");
        AssertRect(layout.ShuffleButton, 614, 512, 662, 560);
        AssertRect(layout.PreviousButton, 690, 512, 738, 560);
        AssertRect(layout.PlayPauseButton, 766, 507, 824, 565);
        AssertRect(layout.NextButton, 852, 512, 900, 560);
        AssertRect(layout.RepeatButton, 928, 512, 976, 560);
        AssertRect(layout.SourceBadgeRect, 868, 26, 992, 52);
        Assert.IsTrue(layout.SourceBadgeVisible);
        Assert.AreEqual(598f, layout.ProgressLeft, 0.01f);
        Assert.AreEqual(394f, layout.ProgressWidth, 0.01f);
        Assert.AreEqual(476f, layout.ProgressY, 0.01f);
        Assert.AreEqual(24f, layout.SeekTolerance, 0.01f);
        Assert.AreEqual(24f, layout.Pad, 0.01f, "the render path draws with the layout's pad — one owner of the 24 design constant");
        Assert.AreEqual(30f, layout.ArtGap, 0.01f, "one owner of the 30 design constant");
    }

    [TestMethod]
    public void Compute_HalfScale_ScalesEveryRectProportionally()
    {
        var layout = NowPlayingLayout.Compute(new SKRect(0, 0, 508, 296), 0.5f, showSourceBadge: true, badgeTextWidth: 100f);

        Assert.AreEqual(272f, layout.ArtSide, 0.01f);
        AssertRect(layout.ShuffleButton, 307, 256, 331, 280);
        Assert.AreEqual(299f, layout.ProgressLeft, 0.01f);
        Assert.AreEqual(197f, layout.ProgressWidth, 0.01f);
        Assert.AreEqual(238f, layout.ProgressY, 0.01f);
        Assert.AreEqual(12f, layout.Pad, 0.01f, "the pad scales with the placement");
        Assert.AreEqual(15f, layout.ArtGap, 0.01f);
    }

    [TestMethod]
    public void GetAction_ButtonCenters_MapToTheirControls()
    {
        var layout = NowPlayingLayout.Compute(new SKRect(0, 0, 1016, 592), 1f, showSourceBadge: true, badgeTextWidth: 100f);

        Assert.AreEqual(NowPlayingHitAction.Shuffle, NowPlayingLayout.GetAction(layout, new SKPoint(638, 536)));
        Assert.AreEqual(NowPlayingHitAction.Previous, NowPlayingLayout.GetAction(layout, new SKPoint(714, 536)));
        Assert.AreEqual(NowPlayingHitAction.PlayPause, NowPlayingLayout.GetAction(layout, new SKPoint(795, 536)));
        Assert.AreEqual(NowPlayingHitAction.Next, NowPlayingLayout.GetAction(layout, new SKPoint(876, 536)));
        Assert.AreEqual(NowPlayingHitAction.Repeat, NowPlayingLayout.GetAction(layout, new SKPoint(952, 536)));
        Assert.AreEqual(NowPlayingHitAction.SourceBadge, NowPlayingLayout.GetAction(layout, new SKPoint(930, 39)));
    }

    [TestMethod]
    public void GetAction_ProgressBandTap_ReturnsSeek()
    {
        var layout = NowPlayingLayout.Compute(new SKRect(0, 0, 1016, 592), 1f, showSourceBadge: true, badgeTextWidth: 100f);

        Assert.AreEqual(NowPlayingHitAction.Seek, NowPlayingLayout.GetAction(layout, new SKPoint(792, 476)));
    }

    [TestMethod]
    public void GetAction_SeekTolerance_IsInclusiveAt24PxOnly()
    {
        var layout = NowPlayingLayout.Compute(new SKRect(0, 0, 1016, 592), 1f, showSourceBadge: true, badgeTextWidth: 100f);

        Assert.AreEqual(NowPlayingHitAction.Seek, NowPlayingLayout.GetAction(layout, new SKPoint(598, 452)), "y exactly 24px above the bar is still a seek");
        Assert.AreEqual(NowPlayingHitAction.None, NowPlayingLayout.GetAction(layout, new SKPoint(598, 451)), "y 25px above the bar is not a seek");
        Assert.AreEqual(NowPlayingHitAction.Seek, NowPlayingLayout.GetAction(layout, new SKPoint(992, 476)), "the right edge of the bar is inclusive");
        Assert.AreEqual(NowPlayingHitAction.None, NowPlayingLayout.GetAction(layout, new SKPoint(993, 476)), "right of the bar is not a seek");
    }

    [TestMethod]
    public void GetAction_OffAllControls_ReturnsNone()
    {
        var layout = NowPlayingLayout.Compute(new SKRect(0, 0, 1016, 592), 1f, showSourceBadge: true, badgeTextWidth: 100f);

        Assert.AreEqual(NowPlayingHitAction.None, NowPlayingLayout.GetAction(layout, new SKPoint(200, 200)));
    }

    [TestMethod]
    public void GetAction_HiddenBadge_NeverHitTestsEvenOnItsOwnRect()
    {
        // The stale-rect hole: with the badge hidden, a tap on the (computed,
        // undrawn) badge rect must not cycle the media source. The visibility
        // flag gates the hit test, so a hidden badge is never a tap target.
        var hidden = NowPlayingLayout.Compute(new SKRect(0, 0, 1016, 592), 1f, showSourceBadge: false, badgeTextWidth: 100f);
        var visible = NowPlayingLayout.Compute(new SKRect(0, 0, 1016, 592), 1f, showSourceBadge: true, badgeTextWidth: 100f);

        Assert.IsFalse(hidden.SourceBadgeVisible);
        Assert.AreEqual(hidden.SourceBadgeRect, visible.SourceBadgeRect, "the badge rect is computed regardless of visibility");
        Assert.AreEqual(NowPlayingHitAction.None, NowPlayingLayout.GetAction(hidden, new SKPoint(930, 39)), "hidden badge reads None");
        Assert.AreEqual(NowPlayingHitAction.SourceBadge, NowPlayingLayout.GetAction(visible, new SKPoint(930, 39)), "visible badge reads SourceBadge");
    }

    [TestMethod]
    public void BlendToward_AmountPinsTheBlendEndpoints()
    {
        var from = new SKColor(10, 20, 30, 200);
        var to = new SKColor(20, 40, 60, 255);

        Assert.AreEqual(from, NowPlayingLayout.BlendToward(from, to, 0f), "amount 0 is the source color");
        Assert.AreEqual(new SKColor(20, 40, 60, 200), NowPlayingLayout.BlendToward(from, to, 1f), "amount 1 reaches the target RGB but keeps the source alpha");
        Assert.AreEqual(new SKColor(15, 30, 45, 200), NowPlayingLayout.BlendToward(from, to, 0.5f), "amount 0.5 is the midpoint, source alpha kept");
        Assert.AreEqual(new SKColor(20, 40, 60, 200), NowPlayingLayout.BlendToward(from, to, 1.5f), "amounts above 1 clamp to the target RGB");
        Assert.AreEqual(from, NowPlayingLayout.BlendToward(from, to, -0.5f), "amounts below 0 clamp to the source");
    }
}
