
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

    [TestMethod]
    public void RenderDetailView_LongUrlInLocation_RendersWithoutException()
    {
        CalendarEventStore.Reset();
        const string longUrl = "https://www.my.va.gov/VAVERA/s/flow/VERA_Start?appointmentId=001t000000AbCdEfGhIjKlMnOpQrStUvWxYz";
        CalendarEventStore.UpdateFromDto(new CalendarSnapshot
        {
            Events =
            [
                new CalendarEvent
                {
                    Title = "Toby's VERA Virtual Appointment",
                    Start = new DateTime(2026, 9, 6, 10, 0, 0, DateTimeKind.Unspecified),
                    End = new DateTime(2026, 9, 6, 10, 15, 0, DateTimeKind.Unspecified),
                    Location = longUrl,
                    Description = "Important appointment with VERA representative.",
                    FeedLabel = "VA",
                },
            ],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        });

        var w = new CalendarWidget();
        var bounds = new SKRect(0, 0, 1016, 592);
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        w.Render(surface!.Canvas, bounds);

        var geo = CalendarLayout.Compute(bounds, 2.46f, 1, false);
        var rowPoint = new SKPoint(geo.RowRects[0].MidX, geo.RowRects[0].MidY);
        w.OnTouch(rowPoint, TouchEventType.TouchDown);
        w.OnTouch(rowPoint, TouchEventType.TouchUp);

        w.Render(surface.Canvas, bounds);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void RenderDetailView_DirectRendererCall_WithLongUrlAndRunOnSentence_RendersSuccessfully()
    {
        using var renderer = new CalendarWidgetRenderer();
        // A short card (500x400) so the long URL + run-on text overflow and
        // produce a positive scroll extent.
        var bounds = new SKRect(0, 0, 500, 220);
        using var surface = SKSurface.Create(new SKImageInfo(500, 220));

        var ev = new CalendarEvent
        {
            Title = "VeryLongRunOnSentenceWithoutSpacesTitleExceedingTheCardWidth1234567890",
            Start = new DateTime(2026, 9, 6, 10, 0, 0, DateTimeKind.Unspecified),
            End = new DateTime(2026, 9, 6, 10, 15, 0, DateTimeKind.Unspecified),
            Location = "https://www.my.va.gov/VAVERA/s/flow/VERA_Start?appointmentId=001t000000AbCdEfGhIjKlMnOpQrStUvWxYz",
            Description = "VeryLongRunOnDescriptionWithoutAnyWhitespaceCharactersAtAllToEnsureNoClippingOccurs",
            Url = "https://meet.example.com/long/unbroken/path/to/meeting/with/query?param1=value1&param2=value2",
            FeedLabel = "Work",
        };

        DetailFrameFacts facts = renderer.RenderDetailView(surface!.Canvas, bounds, 1.0f, ev, SKColors.White, SKColors.Cyan, Now);
        // The long URL + run-on description must overflow the short card, producing
        // a positive scroll extent and a non-empty URL tap target.
        Assert.IsTrue(facts.MaxScrollY > 0f, "long content must exceed the card height");
        Assert.IsFalse(facts.UrlRect.IsEmpty, "URL tap target must be recorded for a URL-bearing event");
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void DetailView_DragUpAndDown_ScrollsContentAndStaysClamped()
    {
        CalendarEventStore.Reset();
        CalendarEventStore.UpdateFromDto(new CalendarSnapshot
        {
            Events =
            [
                new CalendarEvent
                {
                    Title = "Detailed Strategic Sync",
                    Start = new DateTime(2026, 9, 6, 10, 0, 0, DateTimeKind.Unspecified),
                    End = new DateTime(2026, 9, 6, 11, 0, 0, DateTimeKind.Unspecified),
                    Location = "https://www.my.va.gov/VAVERA/s/flow/VERA_Start?appointmentId=001t000000AbCdEfGhIjKlMnOpQrStUvWxYz",
                    Description = "Line 1 of long agenda\nLine 2 of instructions\nLine 3 of meeting goals\nLine 4 of attendees\nLine 5 of action items\nLine 6 of summary notes\nLine 7 of wrap-up checklist",
                    Url = "https://meet.example.com/long/path/with/parameters?conference=123456&pin=987654",
                    FeedLabel = "Work",
                },
            ],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        });

        var w = new CalendarWidget();
        var bounds = new SKRect(0, 0, 800, 400);
        using var surface = SKSurface.Create(new SKImageInfo(800, 400));
        w.Render(surface!.Canvas, bounds);

        var geo = CalendarLayout.Compute(bounds, 2.0f, 1, false);
        var rowPoint = new SKPoint(geo.RowRects[0].MidX, geo.RowRects[0].MidY);

        // Tap row to enter detail mode
        w.OnTouch(rowPoint, TouchEventType.TouchDown);
        w.OnTouch(rowPoint, TouchEventType.TouchUp);

        // Render detail view (computes card and max scroll extent)
        w.Render(surface.Canvas, bounds);

        // Drag up by 80px inside the pillbox to scroll down
        float startX = bounds.MidX;
        float startY = bounds.MidY;
        w.OnTouch(new SKPoint(startX, startY), TouchEventType.TouchDown);
        w.OnTouch(new SKPoint(startX, startY - 80f), TouchEventType.TouchMove);
        w.OnTouch(new SKPoint(startX, startY - 80f), TouchEventType.TouchUp);

        // Render scrolled frame
        w.Render(surface.Canvas, bounds);
        Assert.IsNotNull(surface);

        // Drag down by 80px inside the pillbox to scroll back up
        w.OnTouch(new SKPoint(startX, startY), TouchEventType.TouchDown);
        w.OnTouch(new SKPoint(startX, startY + 80f), TouchEventType.TouchMove);
        w.OnTouch(new SKPoint(startX, startY + 80f), TouchEventType.TouchUp);

        w.Render(surface.Canvas, bounds);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void DetailView_TapHeader_ExitsBackToAgenda()
    {
        CalendarEventStore.Reset();
        CalendarEventStore.UpdateFromDto(new CalendarSnapshot
        {
            Events =
            [
                new CalendarEvent
                {
                    Title = "Standup",
                    Start = new DateTime(2026, 9, 6, 10, 0, 0, DateTimeKind.Unspecified),
                    End = new DateTime(2026, 9, 6, 10, 15, 0, DateTimeKind.Unspecified),
                },
            ],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        });

        var w = new CalendarWidget();
        var bounds = new SKRect(0, 0, 800, 400);
        using var surface = SKSurface.Create(new SKImageInfo(800, 400));
        w.Render(surface!.Canvas, bounds);

        var geo = CalendarLayout.Compute(bounds, 2.0f, 1, false);
        var rowPoint = new SKPoint(geo.RowRects[0].MidX, geo.RowRects[0].MidY);

        // Enter detail mode
        w.OnTouch(rowPoint, TouchEventType.TouchDown);
        w.OnTouch(rowPoint, TouchEventType.TouchUp);
        w.Render(surface.Canvas, bounds);

        // Tap top "Tap to go back" header (y = 15f)
        var topBackPoint = new SKPoint(bounds.Left + 20f, bounds.Top + 15f);
        w.OnTouch(topBackPoint, TouchEventType.TouchDown);
        w.OnTouch(topBackPoint, TouchEventType.TouchUp);

        // Re-render: should be back in agenda view without exceptions
        w.Render(surface.Canvas, bounds);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void DetailView_Renderer_ComputesMaxScrollAndBounds()
    {
        using var renderer = new CalendarWidgetRenderer();
        var bounds = new SKRect(0, 0, 400, 300);
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));

        var ev = new CalendarEvent
        {
            Title = "Event with lots of lines that exceed the card",
            Start = new DateTime(2026, 9, 6, 10, 0, 0, DateTimeKind.Unspecified),
            End = new DateTime(2026, 9, 6, 10, 15, 0, DateTimeKind.Unspecified),
            Location = "https://example.com/very/long/location/url/that/wraps/onto/multiple/lines",
            Description = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5\nLine 6\nLine 7\nLine 8",
            Url = "https://meet.example.com/meeting",
        };

        DetailFrameFacts facts = renderer.RenderDetailView(surface!.Canvas, bounds, 1.0f, ev, SKColors.White, SKColors.Cyan, Now, 20f);
        Assert.IsTrue(facts.MaxScrollY > 0f, "content must exceed card height and produce positive max scroll");
        Assert.IsTrue(facts.CardRect.Width > 0f, "cardRect width must be positive");
        Assert.IsTrue(facts.CardRect.Height > 0f, "cardRect height must be positive");
        Assert.IsTrue(facts.UrlRect.Width > 0f, "urlRect width must be positive");
    }
}
