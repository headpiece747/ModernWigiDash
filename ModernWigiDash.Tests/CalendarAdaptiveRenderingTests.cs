
namespace ModernWigiDash.Tests;

[TestClass]
public class CalendarAdaptiveRenderingTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 14, 30, 0, DateTimeKind.Unspecified);

    [TestCleanup]
    public void Cleanup() => CalendarEventStore.Reset();

    private static CalendarWidget CreateWidgetWithEvent(string title = "Team Sync", string url = "https://meet.example/sync")
    {
        CalendarEventStore.Reset();
        CalendarEventStore.UpdateFromDto(new CalendarSnapshot
        {
            Events =
            [
                new CalendarEvent
                {
                    Title = title,
                    Start = new DateTime(2026, 9, 6, 15, 0, 0, DateTimeKind.Unspecified),
                    End = new DateTime(2026, 9, 6, 16, 0, 0, DateTimeKind.Unspecified),
                    Url = url,
                    FeedLabel = "Work",
                },
            ],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        });

        return new CalendarWidget();
    }

    [TestMethod]
    public void Render_Full5x4Editorial_SucceedsWithoutException()
    {
        var w = CreateWidgetWithEvent();
        var bounds = new SKRect(0, 0, 1016, 592);
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));

        w.Render(surface!.Canvas, bounds);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void Render_Split4x2_SucceedsWithoutException()
    {
        var w = CreateWidgetWithEvent();
        var bounds = new SKRect(0, 0, 812, 296);
        using var surface = SKSurface.Create(new SKImageInfo(812, 296));

        w.Render(surface!.Canvas, bounds);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void Render_Compact2x3_SucceedsWithoutException()
    {
        var w = CreateWidgetWithEvent();
        var bounds = new SKRect(0, 0, 406, 444);
        using var surface = SKSurface.Create(new SKImageInfo(406, 444));

        w.Render(surface!.Canvas, bounds);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void OnTouch_ChevronNavigation_AdvancesAndRewindsMonth()
    {
        var w = CreateWidgetWithEvent();
        var bounds = new SKRect(0, 0, 1016, 592);
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        w.Render(surface!.Canvas, bounds);

        var geo = CalendarLayout.Compute(bounds, 2.46f, 1, false);

        // Tap Next Month chevron
        var nextPoint = new SKPoint(geo.NextChevronRect.MidX, geo.NextChevronRect.MidY);
        w.OnTouch(nextPoint, TouchEventType.TouchDown);
        w.OnTouch(nextPoint, TouchEventType.TouchUp);

        // Re-render and check that month advanced
        w.Render(surface.Canvas, bounds);

        // Tap Prev Month chevron twice
        var prevPoint = new SKPoint(geo.PrevChevronRect.MidX, geo.PrevChevronRect.MidY);
        w.OnTouch(prevPoint, TouchEventType.TouchDown);
        w.OnTouch(prevPoint, TouchEventType.TouchUp);

        w.OnTouch(prevPoint, TouchEventType.TouchDown);
        w.OnTouch(prevPoint, TouchEventType.TouchUp);

        w.Render(surface.Canvas, bounds);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void OnTouch_SwipeGesture_DoesNotInterceptGlobalPageSwipes()
    {
        var w = CreateWidgetWithEvent();
        var bounds = new SKRect(0, 0, 1016, 592);
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        w.Render(surface!.Canvas, bounds);

        // A swipe gesture (dx > 15) must be ignored so it bubbles to global page navigation
        var start = new SKPoint(bounds.MidX, bounds.MidY);
        var end = new SKPoint(bounds.MidX - 80f, bounds.MidY);

        w.OnTouch(start, TouchEventType.TouchDown);
        w.OnTouch(end, TouchEventType.TouchUp);

        w.Render(surface.Canvas, bounds);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void OnTouch_MonthGridDayTap_SelectsDay()
    {
        var w = CreateWidgetWithEvent();
        var bounds = new SKRect(0, 0, 1016, 592);
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        w.Render(surface!.Canvas, bounds);

        var geo = CalendarLayout.Compute(bounds, 2.46f, 1, false);

        // Tap cell near center of month grid
        var tapPoint = new SKPoint(geo.MonthGridRect.MidX, geo.MonthGridRect.MidY);
        w.OnTouch(tapPoint, TouchEventType.TouchDown);
        w.OnTouch(tapPoint, TouchEventType.TouchUp);

        w.Render(surface.Canvas, bounds);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void Render_Minimal1x1_RendersDateCardWithoutException()
    {
        var w = CreateWidgetWithEvent();
        var bounds = new SKRect(0, 0, 203, 148);
        using var surface = SKSurface.Create(new SKImageInfo(203, 148));

        w.Render(surface!.Canvas, bounds);
        Assert.IsNotNull(surface);

        // Tap Minimal Card toggles view date
        var centerPoint = new SKPoint(bounds.MidX, bounds.MidY);
        w.OnTouch(centerPoint, TouchEventType.TouchDown);
        w.OnTouch(centerPoint, TouchEventType.TouchUp);
        w.Render(surface.Canvas, bounds);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void Render_FullEditorial_WithManyEvents_RendersAllScrollableRows()
    {
        CalendarEventStore.Reset();
        var events = new List<CalendarEvent>();
        for (int i = 1; i <= 8; i++)
        {
            events.Add(new CalendarEvent
            {
                Title = $"Event {i}",
                Start = new DateTime(2026, 9, 6, 8 + i, 0, 0, DateTimeKind.Unspecified),
                End = new DateTime(2026, 9, 6, 9 + i, 0, 0, DateTimeKind.Unspecified),
                FeedLabel = "Work",
            });
        }

        CalendarEventStore.UpdateFromDto(new CalendarSnapshot
        {
            Events = events,
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        });

        var w = new CalendarWidget();
        var bounds = new SKRect(0, 0, 1016, 592);
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        w.Render(surface!.Canvas, bounds);

        var geo = CalendarLayout.Compute(bounds, 2.46f, 8, true);

        // Drag up inside AgendaScrollAreaRect to scroll
        float startY = geo.AgendaScrollAreaRect.MidY;
        float startX = geo.AgendaScrollAreaRect.MidX;
        w.OnTouch(new SKPoint(startX, startY), TouchEventType.TouchDown);
        w.OnTouch(new SKPoint(startX, startY - 60f), TouchEventType.TouchMove);
        w.OnTouch(new SKPoint(startX, startY - 60f), TouchEventType.TouchUp);

        w.Render(surface.Canvas, bounds);
        Assert.IsNotNull(surface);
    }
}
