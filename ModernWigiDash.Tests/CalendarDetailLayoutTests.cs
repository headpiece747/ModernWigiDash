namespace ModernWigiDash.Tests;

/// <summary>
/// The detail view's geometry owner (<see cref="CalendarLayout.ComputeDetail"/>,
/// 2026-09-27). Pure over its inputs - no canvas, no fonts - so the scroll
/// extent, the section baselines and the link tap target are asserted directly
/// rather than inferred from a rendered frame.
///
/// The arithmetic used to live in the render path, which computed the rects while
/// drawing and handed three of them back for the touch path. That made display
/// and input two consumers of a geometry produced as a side effect of drawing,
/// and it is why a touch arriving before the first detail draw found nothing.
/// These pins are on the one place the numbers now live.
/// </summary>
[TestClass]
public class CalendarDetailLayoutTests
{
    private static readonly SKRect Bounds = new(0, 0, 320, 240);

    /// <summary>Content that fits: one title line, no location/description, a
    /// duration line and a one-line link.</summary>
    private static DetailLineCounts Fitting => new(1, null, null, 1, HasFeedLabel: false, HasDuration: true);

    /// <summary>Content that overflows: every section, several lines deep.</summary>
    private static DetailLineCounts Overflowing => new(6, 2, 3, 1, HasFeedLabel: true, HasDuration: true);

    [TestMethod]
    public void ComputeDetail_FittingContent_NeedsNoScroll()
    {
        var geo = CalendarLayout.ComputeDetail(Bounds, 1f, Fitting, scrollY: 0f);

        Assert.AreEqual(0f, geo.MaxScrollY, "content that fits leaves nothing to scroll");
        Assert.AreEqual(0f, geo.ClampedScrollY);
        Assert.IsTrue(geo.TotalContentHeight > 0f, "the content height is still measured - it is what the scrollbar thumb divides by");
    }

    [TestMethod]
    public void ComputeDetail_OverflowingContent_ScrollsAndClamps()
    {
        var geo = CalendarLayout.ComputeDetail(Bounds, 1f, Overflowing, scrollY: 0f);

        Assert.IsTrue(geo.MaxScrollY > 0f, "overflowing content must produce a scroll extent");
        Assert.IsTrue(
            geo.MaxScrollY < geo.TotalContentHeight,
            "the extent is the overflow, not the whole content - otherwise the scrollbar thumb is wrong");

        // A scroll request past the end lands on the end, not beyond it: the draw
        // pass and the touch state must agree about where the content is.
        Assert.AreEqual(geo.MaxScrollY, CalendarLayout.ComputeDetail(Bounds, 1f, Overflowing, 10_000f).ClampedScrollY);
        Assert.AreEqual(0f, CalendarLayout.ComputeDetail(Bounds, 1f, Overflowing, -500f).ClampedScrollY);
    }

    [TestMethod]
    public void ComputeDetail_SectionBaselines_AreInDrawOrder()
    {
        // THE invariant that makes "draw from the record" safe: the running
        // cursor advances in the order the draw pass walks, so no section can
        // land on a previous one. A reordered or dropped `y +=` in ComputeDetail
        // would still compile and still draw - it would just overprint.
        var geo = CalendarLayout.ComputeDetail(Bounds, 1f, Overflowing, scrollY: 0f);

        Assert.IsTrue(geo.TimeY < geo.TitleY, "the time line precedes the title");
        Assert.IsTrue(geo.TitleY < geo.FeedY, "the feed label follows the title");
        Assert.IsTrue(geo.FeedY < geo.LocationY, "the location follows the feed label");
        Assert.IsTrue(geo.LocationY < geo.DescriptionY, "the description follows the location");
        Assert.IsTrue(geo.DescriptionY < geo.DurationY, "the duration follows the description");
        Assert.IsTrue(geo.DurationY < geo.UrlY, "the link follows the duration");
    }

    [TestMethod]
    public void ComputeDetail_AbsentSections_StillAdvanceTheCursor()
    {
        // With no feed label, no location, no description and no duration, the
        // remaining sections must still be laid out in order rather than stacked
        // on the title.
        var counts = new DetailLineCounts(1, null, null, 1, HasFeedLabel: false, HasDuration: false);
        var geo = CalendarLayout.ComputeDetail(Bounds, 1f, counts, scrollY: 0f);

        Assert.IsTrue(geo.TimeY < geo.TitleY);
        Assert.IsTrue(geo.TitleY < geo.UrlY, "the link must sit below the title even with every optional section absent");
        Assert.IsTrue(geo.FeedY >= geo.TitleY, "an absent section's y is where the cursor was - it is never drawn, so it must not go backwards");
    }

    [TestMethod]
    public void ComputeDetail_LinkTarget_SpansTheCardAndReachesAboveTheFirstGlyph()
    {
        var geo = CalendarLayout.ComputeDetail(Bounds, 1f, Fitting, scrollY: 0f);

        Assert.IsFalse(geo.UrlRect.IsEmpty, "a URL-bearing event must get a tap target");
        Assert.AreEqual(geo.CardRect.Left, geo.UrlRect.Left, "the target spans the card's width, so a short wrapped line is still easy to hit");
        Assert.AreEqual(geo.CardRect.Right, geo.UrlRect.Right);
        Assert.AreEqual(
            geo.UrlY - CalendarLayout.DetailUrlTapSlack * 1f,
            geo.UrlRect.Top,
            "the target reaches ~one line ABOVE the first glyph's baseline, because a tap lands above the baseline");
        Assert.IsTrue(geo.UrlRect.Top < geo.UrlRect.Bottom, "the target must have height");
    }

    [TestMethod]
    public void ComputeDetail_NoLink_NoTarget()
    {
        var counts = new DetailLineCounts(1, null, null, null, HasFeedLabel: false, HasDuration: true);
        var geo = CalendarLayout.ComputeDetail(Bounds, 1f, counts, scrollY: 0f);

        Assert.IsTrue(geo.UrlRect.IsEmpty, "an event with no link must not carry a tap target - SKRect.Contains on an empty rect is false, which is what makes the touch branch a no-op");
    }

    [TestMethod]
    public void ComputeDetail_CardSitsInsideTheBounds_WithPadding()
    {
        var geo = CalendarLayout.ComputeDetail(Bounds, 1f, Fitting, scrollY: 0f);

        Assert.IsTrue(geo.CardRect.Left > Bounds.Left, "the card is inset from the widget bounds");
        Assert.IsTrue(geo.CardRect.Right < Bounds.Right);
        Assert.IsTrue(geo.CardRect.Top > Bounds.Top, "the back row's height is reserved above the card");
        Assert.IsTrue(geo.CardRect.Bottom < Bounds.Bottom);
        Assert.IsTrue(geo.ClipRect.Left > geo.CardRect.Left, "the clip is strictly inside the card so scrolled text cannot bleed over its edge");
        Assert.IsTrue(geo.ClipRect.Right < geo.CardRect.Right);
        Assert.IsTrue(geo.TextX > geo.CardRect.Left, "the text column is inset past the live accent bar");
    }

    [TestMethod]
    public void ComputeDetail_ScalesWithTheFrame()
    {
        var small = CalendarLayout.ComputeDetail(Bounds, 1f, Fitting, scrollY: 0f);
        var large = CalendarLayout.ComputeDetail(Bounds, 2f, Fitting, scrollY: 0f);

        // Every pad and pitch is a design unit times the frame scale, so the
        // INSETS and the content height double. The card's absolute width does
        // not: it is the bounds minus a doubled inset, so a larger frame spends
        // more of a fixed bounds on padding. Asserting the width would have been
        // asserting a falsehood that happens to look plausible.
        Assert.AreEqual(
            (small.CardRect.Left - Bounds.Left) * 2f,
            large.CardRect.Left - Bounds.Left,
            "the card inset is a scaled design unit");
        Assert.AreEqual(
            (Bounds.Right - small.CardRect.Right) * 2f,
            Bounds.Right - large.CardRect.Right);
        Assert.AreEqual(
            (small.CardRect.Top - Bounds.Top) * 2f,
            large.CardRect.Top - Bounds.Top,
            "the back row's reserved height scales too");
        Assert.AreEqual(
            small.TotalContentHeight * 2f,
            large.TotalContentHeight,
            "every line pitch is a scaled design unit, so the content height doubles");
    }
}
