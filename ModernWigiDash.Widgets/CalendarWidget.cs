using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The Calendar widget: a vertical agenda of upcoming events from one or more
/// calendar feeds (public .ics URLs and CalDAV servers), rendered as a hero
/// band (the active or next event + countdown) over timed rows and an all-day
/// pill. The display rules live in <see cref="CalendarPresentation"/> and the
/// hit geometry in <see cref="CalendarLayout"/>; this class is the thin adapter
/// that reads the shared snapshot store, builds the frame's facts, and draws
/// them. Feeds are authored as a compact JSON array in the single
/// <see cref="FeedsJson"/> property (a [WidgetProperty] row is scalar, so a
/// list rides one value); the producer polls them on the resolved cadence and
/// publishes into <see cref="CalendarEventStore"/>, which this widget reads at
/// render time. Credentials for CalDAV feeds resolve through
/// <see cref="CalendarCredentialStore"/> by feed id (never persisted in the
/// profile).
/// </summary>
[WidgetMetadata("calendar", "Calendar", Category = "Productivity", DefaultGridSize = GridSizePreset.Size2x3)]
public sealed class CalendarWidget : ModernWidgetBase, IWidgetEditorProvider
{
    /// <summary>The feed list as a compact JSON array (each element a
    /// <see cref="CalendarFeed"/>-shaped object). Empty/null means no feeds --
    /// the widget renders the unavailable display.</summary>
    [WidgetProperty("Feeds", WidgetPropertyType.Text, "Calendar feeds as a JSON array (icsUrl or calDav entries).")]
    public string FeedsJson { get; set; } = "";

    /// <summary>The poll cadence in minutes (resolved through
    /// <see cref="CalendarFeedPolicy.ResolvePollInterval"/>).</summary>
    [WidgetProperty("Poll Interval (min)", WidgetPropertyType.Number, "How often to refetch the feeds.", 30)]
    public int PollIntervalMinutes { get; set; } = CalendarFeedPolicy.DefaultPollIntervalMinutes;

    /// <summary>How many timed rows to show below the hero (1-3).</summary>
    [WidgetProperty("Timed Rows", WidgetPropertyType.Number, "Number of upcoming-event rows (1-3).", 2)]
    public int TimedRows { get; set; } = CalendarFeedPolicy.DefaultTimedRows;

    /// <summary>The accent color (hero highlight, urgency tint base).</summary>
    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "The accent color for the hero and urgency tints.", "#4F8CFF")]
    public string AccentColorHex { get; set; } = "#4F8CFF";

    /// <summary>The primary text color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "The primary text color.", "#FFFFFF")]
    public string TextColorHex { get; set; } = "#FFFFFF";

    private readonly Lock _producerGate = new();
    private CalendarFeedProducer? _producer;
    private CalendarGeometry _layout;
    private CalendarDisplay? _display;
    private readonly WrapCache _wrapCache = new(16);

    /// <summary>The viewed day's offset from today (0 = today, 1 = tomorrow,
    /// -1 = yesterday). Updated by swipes on the widget.</summary>
    private int _viewDateOffset;

    /// <summary>The specific event being shown in detail mode (null = normal
    /// agenda view). Set when the user taps a timed row.</summary>
    private CalendarEvent? _detailEvent;

    /// <summary>The touch-down point for swipe detection (null when no press is
    /// in progress).</summary>
    private SKPoint? _touchDown;

    /// <summary>The minimum drag distance (in design units) that counts as a
    /// swipe rather than a tap.</summary>
    private const float SwipeThresholdDesign = 40f;

    /// <summary>The shell-open seam: opens a meeting link in the default
    /// browser. Production binds <see cref="OpenMeetingLink"/> (the http/https/
    /// mailto gate + Process.Start); tests bind a recorder so a tap is assertable
    /// without spawning a browser.</summary>
    internal Action<string>? OpenUrlSeam;

    /// <summary>The clock seam (test-injectable; production is the system
    /// clock). Used for both the render's "now" read and the producer's poll
    /// timestamps.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    // Test seams: the production bind site constructs the fetchers/credential
    // store here; tests swap these before InitializeAsync to drive the loop
    // without a network or DPAPI.
    internal Func<IFeedFetcher>? FetchFactory;
    internal Func<CalendarCredentialStore>? CredentialFactory;

    /// <summary>Parses the <see cref="FeedsJson"/> property into feed records.
    /// A malformed or absent value yields an empty list (the unavailable
    /// display), never a throw.</summary>
    internal IReadOnlyList<CalendarFeed> ParseFeeds()
    {
        if (string.IsNullOrWhiteSpace(FeedsJson))
            return [];

        try
        {
            List<CalendarFeed> feeds = [];
            using JsonDocument doc = JsonDocument.Parse(FeedsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    continue;

                string kind = ReadString(el, "kind");
                string feedId = ReadString(el, "feedId");
                string label = ReadString(el, "label");
                string color = ReadString(el, "color");
                bool enabled = !el.TryGetProperty("enabled", out var en) || en.GetBoolean();

                CalendarFeed feed = kind switch
                {
                    "caldav" => new CalDavFeed
                    {
                        FeedId = feedId,
                        Label = label,
                        ColorHex = color,
                        Enabled = enabled,
                        Server = ReadString(el, "server"),
                        Port = ReadInt(el, "port", 443),
                        PrincipalPath = ReadString(el, "principalPath"),
                        Username = ReadString(el, "username"),
                        SelectedCalendars = ReadStringList(el, "selectedCalendars"),
                    },
                    _ => new IcsUrlFeed
                    {
                        FeedId = feedId,
                        Label = label,
                        ColorHex = color,
                        Enabled = enabled,
                        Url = ReadString(el, "url"),
                    },
                };
                feeds.Add(feed);
            }
            return feeds;
        }
        catch (JsonException)
        {
            Context?.LogError($"Calendar: feeds JSON is malformed; rendering the unavailable display. Value: {TruncateForLog(FeedsJson)}");
            return [];
        }
    }

    /// <summary>
    /// Binds the context and starts the feed producer over the parsed feeds.
    /// Re-initialization (a rehydrated instance) restarts the loop cleanly.
    /// </summary>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        RestartProducer();
    }

    /// <summary>Stops the running producer (teardown).</summary>
    public override ValueTask DisposeAsync()
    {
        lock (_producerGate)
        {
            _producer?.Dispose();
            _producer = null;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Rebuilds the producer when the feed list or cadence changes
    /// (an inspector edit routes here via <see cref="OnPropertyChanged"/>).</summary>
    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        if (propertyName is nameof(FeedsJson) or nameof(PollIntervalMinutes))
        {
            RestartProducer();
        }
        Context?.RequestRender();
    }

    private void RestartProducer()
    {
        IReadOnlyList<CalendarFeed> feeds = ParseFeeds();
        lock (_producerGate)
        {
            _producer?.Dispose();
            _producer = null;
            if (feeds.Count == 0)
                return;

            int interval = CalendarFeedPolicy.ResolvePollInterval(PollIntervalMinutes);
            IFeedFetcher fetcher = (FetchFactory ?? CreateProductionFetcher)();
            CalendarCredentialStore credentials = (CredentialFactory ?? (() => new CalendarCredentialStore()))();
            var producer = new CalendarFeedProducer(
                () => fetcher,
                credentials,
                () => Clock.GetLocalNow().LocalDateTime,
                TimeSpan.FromMinutes(interval),
                msg => Context.LogError(msg));
            producer.Start(feeds);
            _producer = producer;
        }
    }

    private static IFeedFetcher CreateProductionFetcher()
    {
        // One process-wide HttpClient (the httpclient-factory house rule): a
        // reflection widget cannot reach the manager's shared client, so the
        // widget owns a single static client for its lifetime. The dispatcher
        // routes each feed to its kind's fetcher over that one client.
        return new DispatchingFeedFetcher(() => SharedHttpClient);
    }

    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    /// <summary>
    /// Draws the calendar: either the normal agenda view (date header + month
    /// grid + timed rows + all-day strip) or the detail view (a scrollable list
    /// of all events for the selected day with full details). A no-data snapshot
    /// renders the named unavailable display (ADR-0017).
    /// </summary>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        float scale = Math.Min(bounds.Width / CalendarLayout.DesignWidth, bounds.Height / CalendarLayout.DesignHeight);
        CalendarSnapshot? snapshot = CalendarEventStore.ReadSnapshot();
        DateTime now = Clock.GetLocalNow().LocalDateTime;
        int rows = CalendarFeedPolicy.ResolveTimedRows(TimedRows);

        // Detail mode: render the single event's detail view.
        if (_detailEvent is not null)
        {
            DrawDetailView(canvas, bounds, scale);
            return;
        }

        DateTime viewDate = now.Date.AddDays(_viewDateOffset);
        CalendarDisplay display = CalendarPresentation.Build(snapshot, now, viewDate, rows);
        _display = display;

        _layout = CalendarLayout.Compute(bounds, scale, display.Rows.Count, false);

        DrawBackground(canvas, bounds, scale);

        if (!display.HasData)
        {
            DrawUnavailable(canvas, bounds, scale, display.StalenessHint);
            return;
        }

        DrawHeader(canvas, display, scale);
        DrawMonthGrid(canvas, display, scale);
        DrawRows(canvas, display, scale);
    }

    /// <summary>
    /// Touch handling for the calendar widget:
    /// - In normal view: a vertical swipe changes the viewed day; a tap on a
    ///   month-grid cell jumps to that day; a tap on a timed row opens the
    ///   detail view for that event.
    /// - In detail view: a vertical swipe scrolls the event list; a tap exits
    ///   back to the agenda.
    /// </summary>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchDown)
        {
            _touchDown = localPoint;
            return;
        }

        if (eventType != TouchEventType.TouchUp)
            return;

        SKPoint? down = _touchDown;
        _touchDown = null;
        if (down is null)
            return;

        float dx = localPoint.X - down.Value.X;
        float dy = localPoint.Y - down.Value.Y;

        // Detail mode: tap on the URL hint opens the link; any other tap exits.
        if (_detailEvent is not null)
        {
            float dx2 = localPoint.X - down.Value.X;
            float dy2 = localPoint.Y - down.Value.Y;
            if (Math.Abs(dx2) < 10f && Math.Abs(dy2) < 10f)
            {
                // Check if the tap is in the bottom portion of the card where
                // the URL hint is drawn.
                CalendarEvent ev = _detailEvent.Value;
                if (!string.IsNullOrWhiteSpace(ev.Url))
                {
                    OpenMeetingLink(ev.Url);
                    return;
                }

                _detailEvent = null;
            }
            return;
        }

        // Normal view: swipe to change day.
        if (Math.Abs(dy) > Math.Abs(dx) && Math.Abs(dy) > SwipeThresholdDesign)
        {
            // Swipe down (dy > 0) = next day; swipe up (dy < 0) = previous day.
            _viewDateOffset += dy > 0 ? 1 : -1;
            _viewDateOffset = Math.Clamp(_viewDateOffset, -30, 30);
            return;
        }

        CalendarDisplay? display = _display;
        if (display is null || !display.HasData)
            return;

        // Check if the tap landed on a month-grid cell (jump to that day).
        if (TryHitMonthGridCell(localPoint, out int targetDay))
        {
            DateTime now = Clock.GetLocalNow().LocalDateTime;
            DateTime viewDate = now.Date.AddDays(_viewDateOffset);
            if (targetDay >= 1 && targetDay <= DateTime.DaysInMonth(viewDate.Year, viewDate.Month))
            {
                DateTime targetDate = new(viewDate.Year, viewDate.Month, targetDay, 0, 0, 0, DateTimeKind.Unspecified);
                _viewDateOffset = (targetDate.Date - now.Date).Days;
                _viewDateOffset = Math.Clamp(_viewDateOffset, -30, 30);
            }
            return;
        }

        // Check for a timed-row tap: enter detail mode for that event.
        int rowIndex = CalendarLayout.GetAction(_layout, localPoint.X, localPoint.Y, out _);
        if (rowIndex < 0)
            return;

        if (rowIndex >= display.Rows.Count)
            return;

        // Find the actual CalendarEvent from the store for this row's time/title.
        CalendarSnapshot? snap = CalendarEventStore.ReadSnapshot();
        if (snap is null || !snap.HasData)
            return;

        DateTime nowDt = Clock.GetLocalNow().LocalDateTime;
        DateTime viewDt = nowDt.Date.AddDays(_viewDateOffset);
        CalendarRow row = display.Rows[rowIndex];

        // Match the row to its source event. All-day rows have "All day" as
        // their time text; timed rows have "HH:mm". Use date + all-day flag +
        // a short title prefix to avoid truncation mismatches.
        bool isAllDayRow = string.Equals(row.TimeText, "All day", StringComparison.Ordinal);
        string titlePrefix = row.Title.Length > 10 ? row.Title[..10] : row.Title;
        var matched = snap.Events.FirstOrDefault(e =>
            e.Start.Date == viewDt.Date &&
            e.IsAllDay == isAllDayRow &&
            e.Title.StartsWith(titlePrefix, StringComparison.Ordinal));

        if (matched != default)
        {
            _detailEvent = matched;
        }
    }

    /// <summary>Hit-tests a point against the month grid cells. Returns true
    /// when the point falls within a non-blank cell, with the day number in
    /// <paramref name="day"/>.</summary>
    private bool TryHitMonthGridCell(SKPoint p, out int day)
    {
        day = 0;
        SKRect rect = _layout.MonthGridRect;
        if (rect.IsEmpty || _display?.MonthGrid.Count != 35)
            return false;

        float cellW = rect.Width / 7f;
        float cellH = rect.Height / 5f;

        int col = (int)((p.X - rect.Left) / cellW);
        int row = (int)((p.Y - rect.Top) / cellH);
        if (col < 0 || col >= 7 || row < 0 || row >= 5)
            return false;

        int index = row * 7 + col;
        MonthCell cell = _display.MonthGrid[index];
        if (cell.Day == 0)
            return false;

        day = cell.Day;
        return true;
    }

    /// <summary>Opens a meeting link through the shell-open seam, after the
    /// http/https/mailto gate. A blank or disallowed link is a logged no-op; a
    /// spawn failure is logged, never thrown.</summary>
    private void OpenMeetingLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!HotkeyActionPolicy.IsAllowedUrl(url))
        {
            Context?.LogError($"Calendar: refusing to open a non-http(s)/mailto event link: {TruncateForLog(url)}");
            return;
        }

        Action<string> open = OpenUrlSeam ?? OpenUrlProduction;
        try
        {
            open(url);
        }
        catch (Exception ex)
        {
            Context?.LogError("Calendar: unable to open the event link", ex);
        }
    }

    /// <summary>The production shell-open: hands the link to the OS default
    /// handler. Thread-safe (Process.Start is thread-safe); the touch poll runs
    /// off the dispatcher.</summary>
    private static void OpenUrlProduction(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>
    /// The special inspector editor for this widget's properties: the calendar
    /// feed list editor for <see cref="FeedsJson"/>, or null when the generic
    /// editor suffices. The renderer discovers this through the interface
    /// instead of branching on the widget type.
    /// </summary>
    public EditorKind? GetEditorKind(PropertyInfo property)
        => string.Equals(property.Name, nameof(FeedsJson), StringComparison.Ordinal)
            ? EditorKind.CalendarFeeds
            : null;

    // --- draw helpers --------------------------------------------------------

    private void DrawBackground(SKCanvas canvas, SKRect bounds, float scale)
    {
        var paint = new SKPaint { Color = new SKColor(18, 18, 24), IsAntialias = true };
        canvas.DrawRoundRect(bounds, 16f * scale, 16f * scale, paint);
    }

    private void DrawUnavailable(SKCanvas canvas, SKRect bounds, float scale, string hint)
    {
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 20f * scale);
        var paint = new SKPaint { Color = ColorOf(TextColorHex, SKColors.White).WithAlpha(160), IsAntialias = true };
        var tb = new SKRect();
        font.MeasureText(hint, out tb, paint);
        canvas.DrawTextWithFallback(hint, bounds.MidX - tb.Width / 2f, bounds.MidY - tb.Height / 2f, font, paint);
    }

    /// <summary>Draws the date header: "Sunday, Sep 6" in a large bold font,
    /// with the month title above it.</summary>
    private void DrawHeader(SKCanvas canvas, CalendarDisplay display, float scale)
    {
        SKRect rect = _layout.HeaderRect;
        if (rect.IsEmpty || string.IsNullOrEmpty(display.DateHeaderText))
            return;

        SKColor text = ColorOf(TextColorHex, SKColors.White);

        // Month title at the top of the header area.
        var monthFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 13f * scale);
        var monthPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
        canvas.DrawTextWithFallback(display.MonthTitle, rect.Left + 2f * scale, rect.Top + 2f * scale - monthFont.Metrics.Top, monthFont, monthPaint);

        // Date header below the month title.
        var dateFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 18f * scale);
        var datePaint = new SKPaint { Color = text, IsAntialias = true };
        canvas.DrawTextWithFallback(display.DateHeaderText, rect.Left + 2f * scale, rect.Bottom - 8f * scale - dateFont.Metrics.Top, dateFont, datePaint);
    }

    /// <summary>Draws the mini month grid: a 5×7 grid of day numbers with event
    /// dots, today highlighted, and the viewed day marked.</summary>
    private void DrawMonthGrid(SKCanvas canvas, CalendarDisplay display, float scale)
    {
        SKRect rect = _layout.MonthGridRect;
        if (rect.IsEmpty || display.MonthGrid.Count != 35)
            return;

        SKColor text = ColorOf(TextColorHex, SKColors.White);
        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);

        float cellW = rect.Width / 7f;
        float cellH = rect.Height / 5f;
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 9f * scale);

        for (int i = 0; i < 35; i++)
        {
            int col = i % 7;
            int row = i / 7;
            float cx = rect.Left + col * cellW + cellW / 2f;
            float cy = rect.Top + row * cellH + cellH / 2f;

            MonthCell cell = display.MonthGrid[i];
            if (cell.Day == 0)
                continue;

            // Background: viewed day gets an accent circle, today gets a ring.
            if (cell.IsViewed)
            {
                var bgPaint = new SKPaint { Color = accent.WithAlpha(60), IsAntialias = true };
                canvas.DrawCircle(cx, cy, cellW * 0.42f, bgPaint);
            }
            else if (cell.IsToday)
            {
                var ringPaint = new SKPaint { Color = accent, StrokeWidth = 1.5f * scale, Style = SKPaintStyle.Stroke, IsAntialias = true };
                canvas.DrawCircle(cx, cy, cellW * 0.38f, ringPaint);
            }

            // Day number.
            SKColor numColor;
            if (cell.IsViewed)
                numColor = SKColors.White;
            else if (cell.IsToday)
                numColor = accent;
            else
                numColor = text.WithAlpha(180);
            var numPaint = new SKPaint { Color = numColor, IsAntialias = true };
            string dayStr = cell.Day.ToString(System.Globalization.CultureInfo.InvariantCulture);
            float dayW = FontHelper.MeasureTextWithFallback(dayStr, font);
            canvas.DrawTextWithFallback(dayStr, cx - dayW / 2f, cy - font.Metrics.Top * 0.5f, font, numPaint);

            // Event dot below the day number.
            if (cell.HasEvents)
            {
                var dotPaint = new SKPaint { Color = cell.IsViewed ? SKColors.White : new SKColor(245, 158, 11), IsAntialias = true };
                canvas.DrawCircle(cx, cy + cellH * 0.3f, 1.5f * scale, dotPaint);
            }
        }
    }

    /// <summary>Draws the timed event rows: each row has a fixed time gutter on
    /// the left and the title area to its right. The live event gets a left
    /// accent bar; approaching events get an amber time.</summary>
    private void DrawRows(SKCanvas canvas, CalendarDisplay display, float scale)
    {
        SKColor text = ColorOf(TextColorHex, SKColors.White);
        float gutterW = _layout.TimeGutterWidth;

        for (int i = 0; i < display.Rows.Count && i < _layout.RowRects.Count; i++)
        {
            SKRect rect = _layout.RowRects[i];
            if (rect.IsEmpty)
                continue;

            CalendarRow row = display.Rows[i];

            // Live event: a left accent bar (4px wide, full row height).
            if (row.IsLive)
            {
                var barPaint = new SKPaint { Color = new SKColor(239, 68, 68), IsAntialias = true };
                canvas.DrawRoundRect(new SKRect(rect.Left, rect.Top, rect.Left + 4f * scale, rect.Bottom), 2f * scale, 2f * scale, barPaint);
            }

            // Time column (fixed gutter width): "HH:mm" centered vertically.
            SKColor timeColor;
            if (row.IsLive)
                timeColor = new SKColor(239, 68, 68);
            else if (row.IsUrgent)
                timeColor = new SKColor(245, 158, 11);
            else
                timeColor = text;
            var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 16f * scale);
            var timePaint = new SKPaint { Color = timeColor, IsAntialias = true };
            float timeX = rect.Left + 10f * scale;
            float timeY = rect.MidY - timeFont.Metrics.Top * 0.5f;
            canvas.DrawTextWithFallback(row.TimeText, timeX, timeY, timeFont, timePaint);

            // Title column: starts after the time gutter.
            float titleX = rect.Left + gutterW;
            float titleMaxW = rect.Width - gutterW - 10f * scale;
            var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 16f * scale);
            var titlePaint = new SKPaint { Color = text.WithAlpha(230), IsAntialias = true };
            float titleY = rect.MidY - titleFont.Metrics.Top * 0.5f;

            // Feed label: a small prefix tag before the title (when present).
            if (!string.IsNullOrEmpty(row.FeedLabel))
            {
                var feedFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f * scale);
                var feedPaint = new SKPaint { Color = new SKColor(120, 160, 255), IsAntialias = true };
                string feedTag = $"[{row.FeedLabel}] ";
                float feedW = FontHelper.MeasureTextWithFallback(feedTag, feedFont);
                canvas.DrawTextWithFallback(feedTag, titleX, titleY, feedFont, feedPaint);
                titleX += feedW;
                titleMaxW -= feedW;
            }

            canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(row.Title, titleFont, Math.Max(20f, titleMaxW)),
                titleX, titleY, titleFont, titlePaint);

            // Subtle row divider (except after the last row).
            if (i < display.Rows.Count - 1)
            {
                var divPaint = new SKPaint { Color = text.WithAlpha(25), StrokeWidth = 1f * scale, IsAntialias = true };
                canvas.DrawLine(rect.Left + 4f * scale, rect.Bottom + 4f * scale, rect.Right, rect.Bottom + 4f * scale, divPaint);
            }
        }
    }

    // --- detail view ---------------------------------------------------------

    /// <summary>Draws the single-event detail view: a centered card showing all
    /// available information for one event (time, title, feed label, location,
    /// duration, meeting link). Tap anywhere to exit back to the agenda.</summary>
    private void DrawDetailView(SKCanvas canvas, SKRect bounds, float scale)
    {
        CalendarEvent ev = _detailEvent!.Value;

        // Background.
        var bgPaint = new SKPaint { Color = new SKColor(18, 18, 24), IsAntialias = true };
        canvas.DrawRoundRect(bounds, 16f * scale, 16f * scale, bgPaint);

        SKColor text = ColorOf(TextColorHex, SKColors.White);
        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);
        float pad = 20f * scale;

        // "Tap to go back" hint at the top.
        var backFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 13f * scale);
        var backPaint = new SKPaint { Color = text.WithAlpha(120), IsAntialias = true };
        canvas.DrawTextWithFallback("Tap to go back", bounds.Left + pad, bounds.Top + pad - backFont.Metrics.Top, backFont, backPaint);

        // Centered card.
        float cardLeft = bounds.Left + pad;
        float cardRight = bounds.Right - pad;
        float cardTop = bounds.Top + pad + 30f * scale;
        float cardBottom = bounds.Bottom - pad;
        var cardBg = new SKPaint { Color = accent.WithAlpha(20), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(cardLeft, cardTop, cardRight, cardBottom), 12f * scale, 12f * scale, cardBg);

        // Left accent bar.
        bool isLive = ev.Start <= Clock.GetLocalNow().LocalDateTime && Clock.GetLocalNow().LocalDateTime < ev.End;
        var barPaint = new SKPaint { Color = isLive ? new SKColor(239, 68, 68) : accent, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(cardLeft, cardTop, cardLeft + 5f * scale, cardBottom), 2.5f * scale, 2.5f * scale, barPaint);

        float x = cardLeft + 16f * scale;
        float y = cardTop + 16f * scale;
        float maxW = cardRight - x - 12f * scale;

        // Time range.
        string timeStr = ev.IsAllDay ? "All day" : $"{ev.Start:HH:mm} – {ev.End:HH:mm}";
        var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 18f * scale);
        var timePaint = new SKPaint { Color = isLive ? new SKColor(239, 68, 68) : accent, IsAntialias = true };
        canvas.DrawTextWithFallback(timeStr, x, y, timeFont, timePaint);
        y += 30f * scale;

        // Title (word-wrapped).
        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 20f * scale);
        var titlePaint = new SKPaint { Color = text, IsAntialias = true };
        string title = string.IsNullOrWhiteSpace(ev.Title) ? "Untitled" : ev.Title;
        IReadOnlyList<string> titleLines = _wrapCache.GetOrWrap(title, titleFont, 20f * scale, maxW);
        foreach (string line in titleLines)
        {
            canvas.DrawTextWithFallback(line, x, y, titleFont, titlePaint);
            y += 26f * scale;
        }
        y += 6f * scale;

        // Feed label.
        if (!string.IsNullOrWhiteSpace(ev.FeedLabel))
        {
            var feedFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 14f * scale);
            var feedPaint = new SKPaint { Color = new SKColor(120, 160, 255), IsAntialias = true };
            canvas.DrawTextWithFallback($"[{ev.FeedLabel}]", x, y, feedFont, feedPaint);
            y += 24f * scale;
        }

        // Location (word-wrapped).
        if (!string.IsNullOrWhiteSpace(ev.Location))
        {
            var locFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 15f * scale);
            var locPaint = new SKPaint { Color = text.WithAlpha(200), IsAntialias = true };
            IReadOnlyList<string> locLines = _wrapCache.GetOrWrap(ev.Location, locFont, 15f * scale, maxW);
            foreach (string line in locLines)
            {
                canvas.DrawTextWithFallback(line, x, y, locFont, locPaint);
                y += 20f * scale;
            }
            y += 6f * scale;
        }

        // Description (word-wrapped, the main body text).
        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            var descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 14f * scale);
            var descPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            IReadOnlyList<string> descLines = _wrapCache.GetOrWrap(ev.Description, descFont, 14f * scale, maxW);
            foreach (string line in descLines)
            {
                canvas.DrawTextWithFallback(line, x, y, descFont, descPaint);
                y += 18f * scale;
            }
            y += 6f * scale;
        }

        // Duration.
        if (!ev.IsAllDay)
        {
            TimeSpan dur = ev.End - ev.Start;
            string durStr = dur.TotalHours >= 1
                ? $"{(int)dur.TotalHours}h {dur.Minutes:D2}m"
                : $"{dur.Minutes}m";
            var durFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 14f * scale);
            var durPaint = new SKPaint { Color = text.WithAlpha(160), IsAntialias = true };
            canvas.DrawTextWithFallback($"Duration: {durStr}", x, y, durFont, durPaint);
            y += 24f * scale;
        }

        // Meeting link.
        if (!string.IsNullOrWhiteSpace(ev.Url))
        {
            var urlFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 14f * scale);
            var urlPaint = new SKPaint { Color = new SKColor(120, 160, 255), IsAntialias = true };
            canvas.DrawTextWithFallback("🔗 Tap link to open", x, y, urlFont, urlPaint);
        }
    }

    // --- JSON read helpers ---------------------------------------------------

    private static string ReadString(JsonElement obj, string name, string fallback = "")
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? fallback : fallback;

    private static int ReadInt(JsonElement obj, string name, int fallback)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : fallback;

    private static IReadOnlyList<string> ReadStringList(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        var parts = new List<string>();
        foreach (JsonElement item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                parts.Add(item.GetString() ?? "");
        }
        return parts;
    }

    private static string TruncateForLog(string value) => value.Length <= 80 ? value : value[..80] + "...";
}
