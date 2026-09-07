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

    /// <summary>The color theme mode: "Seasonal" (rotates 12 curated palettes from the reference poster) or "Custom" (uses AccentColorHex / TextColorHex).</summary>
    [WidgetProperty("Theme Mode", WidgetPropertyType.Text, "Color theme mode: 'Seasonal' (rotates per month) or 'Custom'.", "Seasonal")]
    public string ThemeMode { get; set; } = "Seasonal";

    /// <summary>The layout mode: "Auto" (scales adaptively by size: 5x4 full editorial, 4x2 split banner, 2x3 compact poster), "Poster Month", or "Agenda".</summary>
    [WidgetProperty("Layout Mode", WidgetPropertyType.Text, "Layout mode: 'Auto' (adapts to widget size), 'Poster Month', or 'Agenda'.", "Auto")]
    public string LayoutMode { get; set; } = "Auto";

    private readonly Lock _producerGate = new();
    private CalendarFeedProducer? _producer;
    private CalendarGeometry _layout;
    private CalendarDisplay? _display;
    private readonly WrapCache _wrapCache = new(16);

    // Hoisted SKPaint instances for zero-allocation rendering (30 FPS tick path invariant)
    private readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _strokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _cardPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    private static readonly string[] StatLabels = ["12M", "52W", "365D"];
    private static readonly string[] WeekdayLabels = ["M", "T", "W", "T", "F", "S", "S"];

    /// <summary>The viewed day's offset from today (0 = today, 1 = tomorrow,
    /// -1 = yesterday). Updated by swipes on the widget.</summary>
    private int _viewDateOffset;

    /// <summary>The specific event being shown in detail mode (null = normal
    /// agenda view). Set when the user taps a timed row.</summary>
    private CalendarEvent? _detailEvent;

    /// <summary>The touch-down point for press detection (null when no press is
    /// in progress).</summary>
    private SKPoint? _touchDown;

    /// <summary>The maximum drag distance (in design units) that still counts as a
    /// tap rather than an intentional swipe.</summary>
    private const float TapDragTolerance = 15f;

    /// <summary>Vertical scroll offset in pixels for the agenda rows viewport.</summary>
    private float _agendaScrollY;

    /// <summary>The scroll position at the start of a drag.</summary>
    private float _touchStartScrollY;

    /// <summary>True when a touch drag started inside the scrollable agenda viewport.</summary>
    private bool _isDraggingAgenda;

    /// <summary>True if the agenda was dragged beyond the tap threshold.</summary>
    private bool _isAgendaScrolled;

    /// <summary>Maximum vertical scroll extent for the current agenda rows.</summary>
    private float _maxAgendaScrollY;

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

    /// <summary>Stops the running producer and releases hoisted paints (teardown).</summary>
    public override ValueTask DisposeAsync()
    {
        lock (_producerGate)
        {
            _producer?.Dispose();
            _producer = null;
        }
        _fillPaint.Dispose();
        _strokePaint.Dispose();
        _textPaint.Dispose();
        _cardPaint.Dispose();
        return base.DisposeAsync();
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
        DateTime now = Clock.GetLocalNow().LocalDateTime;
        DateTime viewDate = now.Date.AddDays(_viewDateOffset);
        CalendarSeasonalPalette palette = CalendarSeasonalPalettes.Resolve(viewDate, ThemeMode, AccentColorHex, TextColorHex);

        var mode = CalendarLayout.ResolveViewMode(bounds.Width, bounds.Height, LayoutMode);
        if (mode == CalendarViewMode.MinimalDateCard1x1)
        {
            _layout = CalendarLayout.Compute(bounds, scale, 0, false, LayoutMode);
            DrawMinimalDateCard(canvas, bounds, palette, scale, viewDate);
            return;
        }

        CalendarSnapshot? snapshot = CalendarEventStore.ReadSnapshot();

        // Detail mode: render the single event's detail view.
        if (_detailEvent is not null)
        {
            DrawDetailView(canvas, bounds, scale);
            return;
        }

        int rows = (mode == CalendarViewMode.FullEditorial5x4 || mode == CalendarViewMode.SplitBanner4x2)
            ? 100
            : CalendarFeedPolicy.ResolveTimedRows(TimedRows);

        CalendarDisplay display = CalendarPresentation.Build(snapshot, now, viewDate, rows);
        _display = display;

        _layout = CalendarLayout.Compute(bounds, scale, display.Rows.Count, false, LayoutMode);

        // Compute max vertical scroll extent
        if (!_layout.AgendaScrollAreaRect.IsEmpty && _layout.RowRects.Count > 0)
        {
            float totalH = _layout.RowRects.Count * (24f * scale) + Math.Max(0, _layout.RowRects.Count - 1) * (5f * scale);
            _maxAgendaScrollY = Math.Max(0f, totalH - _layout.AgendaScrollAreaRect.Height);
            _agendaScrollY = Math.Clamp(_agendaScrollY, 0f, _maxAgendaScrollY);
        }
        else
        {
            _maxAgendaScrollY = 0f;
            _agendaScrollY = 0f;
        }

        if (_layout.Mode == CalendarViewMode.CompactPoster2x3 && bounds.Height < 280f)
        {
            DrawClassicView(canvas, bounds, display, scale);
            return;
        }

        DrawAdaptiveView(canvas, bounds, display, palette, scale, viewDate);
    }

    /// <summary>
    /// Touch handling for the calendar widget:
    /// - In normal view: chevron tap changes viewed month;
    ///   tapping a month-grid cell jumps to that day;
    ///   vertical drag inside the agenda section scrolls upcoming events;
    ///   tapping a row or hero event opens the detail view for that event.
    /// - In detail view: tap on URL opens meeting link; tap elsewhere exits back.
    /// </summary>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchDown)
        {
            _touchDown = localPoint;
            _touchStartScrollY = _agendaScrollY;
            _isDraggingAgenda = !_layout.AgendaScrollAreaRect.IsEmpty && _layout.AgendaScrollAreaRect.Contains(localPoint.X, localPoint.Y);
            _isAgendaScrolled = false;
            return;
        }

        if (eventType == TouchEventType.TouchMove)
        {
            if (_isDraggingAgenda && _touchDown.HasValue && _maxAgendaScrollY > 0f)
            {
                float moveDy = localPoint.Y - _touchDown.Value.Y;
                if (Math.Abs(moveDy) > 4f)
                {
                    _isAgendaScrolled = true;
                    _agendaScrollY = Math.Clamp(_touchStartScrollY - moveDy, 0f, _maxAgendaScrollY);
                    Context?.RequestRender();
                }
            }
            return;
        }

        if (eventType != TouchEventType.TouchUp)
            return;

        SKPoint? down = _touchDown;
        _touchDown = null;
        bool wasDragging = _isDraggingAgenda;
        bool wasScrolled = _isAgendaScrolled;
        _isDraggingAgenda = false;
        _isAgendaScrolled = false;

        if (down is null)
            return;

        if (wasDragging && wasScrolled)
        {
            // Drag-to-scroll gesture completed; do not trigger row selection
            return;
        }

        float dx = localPoint.X - down.Value.X;
        float dy = localPoint.Y - down.Value.Y;

        // Detail mode: tap on the URL hint opens the link; any other tap exits.
        if (_detailEvent is not null)
        {
            if (Math.Abs(dx) < 10f && Math.Abs(dy) < 10f)
            {
                CalendarEvent ev = _detailEvent.Value;
                if (!string.IsNullOrWhiteSpace(ev.Url))
                {
                    OpenMeetingLink(ev.Url);
                    return;
                }

                _detailEvent = null;
                Context?.RequestRender();
            }
            return;
        }

        // Minimal date card tap: toggle between today and tomorrow
        if (_layout.Mode == CalendarViewMode.MinimalDateCard1x1)
        {
            if (Math.Abs(dx) <= TapDragTolerance && Math.Abs(dy) <= TapDragTolerance)
            {
                _viewDateOffset = _viewDateOffset == 0 ? 1 : 0;
                Context?.RequestRender();
            }
            return;
        }

        // Ignore intentional horizontal swipes/drags so global page navigation operates cleanly
        if (Math.Abs(dx) > TapDragTolerance || Math.Abs(dy) > TapDragTolerance)
        {
            return;
        }

        // Check for chevron hit (< or >)
        if (CalendarLayout.IsPrevChevronHit(_layout, localPoint.X, localPoint.Y))
        {
            _agendaScrollY = 0f;
            DateTime nowDt = Clock.GetLocalNow().LocalDateTime;
            DateTime currentView = nowDt.Date.AddDays(_viewDateOffset);
            DateTime targetMonth = currentView.AddMonths(-1);
            _viewDateOffset = (targetMonth.Date - nowDt.Date).Days;
            _viewDateOffset = Math.Clamp(_viewDateOffset, -365, 365);
            Context?.RequestRender();
            return;
        }

        if (CalendarLayout.IsNextChevronHit(_layout, localPoint.X, localPoint.Y))
        {
            _agendaScrollY = 0f;
            DateTime nowDt = Clock.GetLocalNow().LocalDateTime;
            DateTime currentView = nowDt.Date.AddDays(_viewDateOffset);
            DateTime targetMonth = currentView.AddMonths(1);
            _viewDateOffset = (targetMonth.Date - nowDt.Date).Days;
            _viewDateOffset = Math.Clamp(_viewDateOffset, -365, 365);
            Context?.RequestRender();
            return;
        }

        CalendarDisplay? display = _display;
        if (display is null)
            return;

        // Check if the tap landed on a month-grid cell (jump to that day)
        if (TryHitMonthGridCell(localPoint, out int targetDay))
        {
            _agendaScrollY = 0f;
            DateTime nowDt = Clock.GetLocalNow().LocalDateTime;
            DateTime viewDt = nowDt.Date.AddDays(_viewDateOffset);
            if (targetDay >= 1 && targetDay <= DateTime.DaysInMonth(viewDt.Year, viewDt.Month))
            {
                DateTime targetDate = new(viewDt.Year, viewDt.Month, targetDay, 0, 0, 0, DateTimeKind.Unspecified);
                _viewDateOffset = (targetDate.Date - nowDt.Date).Days;
                _viewDateOffset = Math.Clamp(_viewDateOffset, -365, 365);
                Context?.RequestRender();
            }
            return;
        }

        // Check hero event tap (in 5x4 layout)
        if (!_layout.AllDayRect.IsEmpty && _layout.AllDayRect.Contains(localPoint.X, localPoint.Y) && display.NextUpcomingEvent != null)
        {
            CalendarSnapshot? snap = CalendarEventStore.ReadSnapshot();
            if (snap is not null && snap.HasData)
            {
                var row = display.NextUpcomingEvent;
                var matched = snap.Events.FirstOrDefault(e =>
                    (row.Start != default && e.Start == row.Start && string.Equals(e.Title, row.Title, StringComparison.Ordinal)) ||
                    string.Equals(e.Title, row.Title, StringComparison.Ordinal) ||
                    (row.Title.Length > 10 && e.Title.StartsWith(row.Title[..10], StringComparison.Ordinal)));
                if (matched != default)
                {
                    _detailEvent = matched;
                    Context?.RequestRender();
                    return;
                }
            }
        }

        // Check for a timed-row tap: enter detail mode for that event.
        // In CompactPoster2x3 mode, the bottom row displays NextUpcomingEvent ?? Rows[0].
        int rowIndex = CalendarLayout.GetAction(_layout, localPoint.X, localPoint.Y, _agendaScrollY, out _);
        CalendarRow? selectedRow = null;
        if (_layout.Mode == CalendarViewMode.CompactPoster2x3 && rowIndex == 0)
        {
            selectedRow = display.NextUpcomingEvent ?? (display.Rows.Count > 0 ? display.Rows[0] : null);
        }
        else if (rowIndex >= 0 && rowIndex < display.Rows.Count)
        {
            selectedRow = display.Rows[rowIndex];
        }

        if (selectedRow is null)
            return;

        CalendarSnapshot? snapStore = CalendarEventStore.ReadSnapshot();
        if (snapStore is null || !snapStore.HasData)
            return;

        DateTime nowEventDt = Clock.GetLocalNow().LocalDateTime;
        DateTime viewEventDt = nowEventDt.Date.AddDays(_viewDateOffset);
        DateTime eventTargetDate = selectedRow.Start != default ? selectedRow.Start.Date : viewEventDt.Date;

        bool isAllDayRow = string.Equals(selectedRow.TimeText, "All day", StringComparison.Ordinal);
        string titlePrefix = selectedRow.Title.Length > 10 ? selectedRow.Title[..10] : selectedRow.Title;

        var foundEvent = snapStore.Events.FirstOrDefault(e =>
            (selectedRow.Start != default && e.Start == selectedRow.Start && string.Equals(e.Title, selectedRow.Title, StringComparison.Ordinal)) ||
            (e.Start.Date == eventTargetDate && e.IsAllDay == isAllDayRow && e.Title.StartsWith(titlePrefix, StringComparison.Ordinal)));

        if (foundEvent == default)
        {
            foundEvent = snapStore.Events.FirstOrDefault(e =>
                string.Equals(e.Title, selectedRow.Title, StringComparison.Ordinal) ||
                (selectedRow.Title.Length > 10 && e.Title.StartsWith(selectedRow.Title[..10], StringComparison.Ordinal)));
        }

        if (foundEvent != default)
        {
            _detailEvent = foundEvent;
            Context?.RequestRender();
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

        bool hasWeekdayHeader = !_layout.MonthCardRect.IsEmpty;
        float weekdayH = hasWeekdayHeader ? 14f * (_layout.Pad / CalendarLayout.PadDesign) : 0f;
        float gridTop = rect.Top + weekdayH;
        float gridH = rect.Height - weekdayH;

        if (p.Y < gridTop || p.Y > rect.Bottom || p.X < rect.Left || p.X > rect.Right)
            return false;

        float cellW = rect.Width / 7f;
        float cellH = gridH / 5f;

        int col = (int)((p.X - rect.Left) / cellW);
        int row = (int)((p.Y - gridTop) / cellH);
        if (col < 0 || col >= 7 || row < 0 || row >= 5)
            return false;

        int index = row * 7 + col;
        MonthCell cell = _display.MonthGrid[index];
        if (!cell.IsCurrentMonth || cell.Day <= 0)
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

    private void DrawAdaptiveView(SKCanvas canvas, SKRect bounds, CalendarDisplay display, CalendarSeasonalPalette palette, float scale, DateTime viewDate)
    {
        // Dark outer container canvas - sharp rect fills 1016x592 physical LCD edge-to-edge
        _cardPaint.Color = new SKColor(12, 13, 18);
        canvas.DrawRect(bounds, _cardPaint);

        if (_layout.Mode == CalendarViewMode.MinimalDateCard1x1)
        {
            DrawMinimalDateCard(canvas, _layout.MonthCardRect.IsEmpty ? bounds : _layout.MonthCardRect, palette, scale, viewDate);
            return;
        }

        if (_layout.Mode == CalendarViewMode.FullEditorial5x4)
        {
            DrawLeftEditorialStrip(canvas, _layout.LeftStripRect, palette, scale, viewDate);
            DrawPosterMonthCard(canvas, _layout.MonthCardRect, display, palette, scale, viewDate);
            DrawAgendaPanel(canvas, _layout.AgendaRect, display, palette, scale);
        }
        else if (_layout.Mode == CalendarViewMode.SplitBanner4x2)
        {
            DrawPosterMonthCard(canvas, _layout.MonthCardRect, display, palette, scale, viewDate);
            DrawAgendaPanel(canvas, _layout.AgendaRect, display, palette, scale);
        }
        else
        {
            DrawCompactPoster(canvas, _layout, display, palette, scale, viewDate);
        }
    }

    private void DrawMinimalDateCard(SKCanvas canvas, SKRect rect, CalendarSeasonalPalette palette, float scale, DateTime viewDate)
    {
        if (rect.IsEmpty)
            return;

        // Color box filling the 1x1 card bounds
        _cardPaint.Color = palette.Background;
        canvas.DrawRoundRect(rect, 12f * scale, 12f * scale, _cardPaint);

        _strokePaint.Color = SKColors.White.WithAlpha(20);
        _strokePaint.StrokeWidth = 1f * scale;
        canvas.DrawRoundRect(rect, 12f * scale, 12f * scale, _strokePaint);

        float midX = rect.MidX;

        // Year at top
        string yearStr = viewDate.Year.ToString(CultureInfo.InvariantCulture);
        var yearFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f * scale);
        _textPaint.Color = palette.Text.WithAlpha(170);
        float yearW = FontHelper.MeasureTextWithFallback(yearStr, yearFont);
        canvas.DrawTextWithFallback(yearStr, midX - yearW / 2f, rect.Top + 18f * scale, yearFont, _textPaint);

        // Month name
        string monthStr = viewDate.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant();
        var monthFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 13f * scale);
        _textPaint.Color = palette.Text;
        float monthW = FontHelper.MeasureTextWithFallback(monthStr, monthFont);
        canvas.DrawTextWithFallback(monthStr, midX - monthW / 2f, rect.Top + 35f * scale, monthFont, _textPaint);

        // Day number
        string dayStr = viewDate.Day.ToString(CultureInfo.InvariantCulture);
        var dayFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 44f * scale);
        _textPaint.Color = palette.Text;
        float dayW = FontHelper.MeasureTextWithFallback(dayStr, dayFont);
        canvas.DrawTextWithFallback(dayStr, midX - dayW / 2f, rect.Top + 82f * scale, dayFont, _textPaint);

        // Weekday at bottom
        string dowStr = viewDate.ToString("dddd", CultureInfo.InvariantCulture).ToUpperInvariant();
        var dowFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 9.5f * scale);
        _textPaint.Color = palette.Text.WithAlpha(180);
        float dowW = FontHelper.MeasureTextWithFallback(dowStr, dowFont);
        canvas.DrawTextWithFallback(dowStr, midX - dowW / 2f, rect.Bottom - 12f * scale, dowFont, _textPaint);
    }

    private void DrawLeftEditorialStrip(SKCanvas canvas, SKRect rect, CalendarSeasonalPalette palette, float scale, DateTime viewDate)
    {
        if (rect.IsEmpty)
            return;

        // Container
        _cardPaint.Color = new SKColor(18, 19, 26);
        canvas.DrawRoundRect(rect, 12f * scale, 12f * scale, _cardPaint);
        _strokePaint.Color = SKColors.White.WithAlpha(15);
        _strokePaint.StrokeWidth = 1f * scale;
        canvas.DrawRoundRect(rect, 12f * scale, 12f * scale, _strokePaint);

        // Week badge at top (2x smaller: 5.5f * scale)
        int weekNum = System.Globalization.ISOWeek.GetWeekOfYear(viewDate);
        var weekFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 5.5f * scale);
        _textPaint.Color = palette.Accent;
        string weekStr = $"W{weekNum:D2}";
        float ww = FontHelper.MeasureTextWithFallback(weekStr, weekFont);
        canvas.DrawTextWithFallback(weekStr, rect.MidX - ww / 2f, rect.Top + 14f * scale, weekFont, _textPaint);

        // Rotated typography branding "CALENDAR" - centered in the strip
        canvas.Save();
        canvas.Translate(rect.MidX, rect.MidY);
        canvas.RotateDegrees(-90);
        var brandFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 15f * scale);
        _textPaint.Color = SKColors.White.WithAlpha(175);
        const string brandStr = "CALENDAR";
        float brandW = FontHelper.MeasureTextWithFallback(brandStr, brandFont);
        canvas.DrawTextWithFallback(brandStr, -brandW / 2f, brandFont.Metrics.CapHeight / 2f, brandFont, _textPaint);
        canvas.Restore();

        // Stacked indicators at bottom: 12M / 52W / 365D (2x smaller: 4.5f * scale)
        var statFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 4.5f * scale);
        _textPaint.Color = SKColors.White.WithAlpha(140);
        float statY = rect.Bottom - 30f * scale;
        for (int i = 0; i < StatLabels.Length; i++)
        {
            float sw = FontHelper.MeasureTextWithFallback(StatLabels[i], statFont);
            canvas.DrawTextWithFallback(StatLabels[i], rect.MidX - sw / 2f, statY, statFont, _textPaint);
            statY += 9f * scale;
        }
    }

    private void DrawPosterMonthCard(SKCanvas canvas, SKRect rect, CalendarDisplay display, CalendarSeasonalPalette palette, float scale, DateTime viewDate)
    {
        if (rect.IsEmpty)
            return;

        // Poster month card background with seasonal color
        _cardPaint.Color = palette.Background;
        canvas.DrawRoundRect(rect, 14f * scale, 14f * scale, _cardPaint);

        // Header
        SKRect headerRect = _layout.HeaderRect;
        if (!headerRect.IsEmpty)
        {
            var yearFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f * scale);
            _textPaint.Color = palette.Text.WithAlpha(170);
            canvas.DrawTextWithFallback($"/{viewDate.Year}", headerRect.Left + 2f * scale, headerRect.Top + 13f * scale, yearFont, _textPaint);

            var monthFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 26f * scale);
            _textPaint.Color = palette.Text;
            canvas.DrawTextWithFallback(palette.MonthCode, headerRect.Left + 2f * scale, headerRect.Top + 36f * scale, monthFont, _textPaint);

            DrawChevron(canvas, _layout.PrevChevronRect, "<", palette.Text, scale);
            DrawChevron(canvas, _layout.NextChevronRect, ">", palette.Text, scale);
        }

        // Weekday header & Month grid
        SKRect gridRect = _layout.MonthGridRect;
        if (!gridRect.IsEmpty && display.MonthGrid.Count == 35)
        {
            float weekdayH = 14f * scale;
            float cellW = gridRect.Width / 7f;
            var wdFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 9f * scale);
            _textPaint.Color = palette.Text.WithAlpha(160);
            for (int c = 0; c < 7; c++)
            {
                float cx = gridRect.Left + c * cellW + cellW / 2f;
                float tw = FontHelper.MeasureTextWithFallback(WeekdayLabels[c], wdFont);
                canvas.DrawTextWithFallback(WeekdayLabels[c], cx - tw / 2f, gridRect.Top + 10f * scale, wdFont, _textPaint);
            }

            float gridY = gridRect.Top + weekdayH;
            float cellH = (gridRect.Height - weekdayH) / 5f;
            var dayFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 10f * scale);

            for (int i = 0; i < 35; i++)
            {
                int col = i % 7;
                int row = i / 7;
                float cx = gridRect.Left + col * cellW + cellW / 2f;
                float cy = gridY + row * cellH + cellH / 2f;
                MonthCell cell = display.MonthGrid[i];

                if (!cell.IsCurrentMonth)
                {
                    // Overflow days from adjacent months rendered at muted contrast
                    string numStr = cell.Day.ToString(CultureInfo.InvariantCulture);
                    _textPaint.Color = palette.Text.WithAlpha(55);
                    float nw = FontHelper.MeasureTextWithFallback(numStr, dayFont);
                    canvas.DrawTextWithFallback(numStr, cx - nw / 2f, cy - dayFont.Metrics.Top * 0.4f, dayFont, _textPaint);
                    continue;
                }

                if (cell.IsToday)
                {
                    _fillPaint.Color = palette.Accent;
                    canvas.DrawCircle(cx, cy, Math.Min(cellW, cellH) * 0.42f, _fillPaint);
                    _textPaint.Color = (palette.Accent.Red * 0.299 + palette.Accent.Green * 0.587 + palette.Accent.Blue * 0.114) > 160 ? new SKColor(20, 20, 25) : SKColors.White;
                }
                else if (cell.IsViewed)
                {
                    _strokePaint.Color = palette.Accent;
                    _strokePaint.StrokeWidth = 1.5f * scale;
                    canvas.DrawCircle(cx, cy, Math.Min(cellW, cellH) * 0.40f, _strokePaint);
                    _textPaint.Color = palette.Text;
                }
                else if (cell.HasEvents)
                {
                    _fillPaint.Color = palette.Text.WithAlpha(35);
                    canvas.DrawCircle(cx, cy, Math.Min(cellW, cellH) * 0.38f, _fillPaint);
                    _textPaint.Color = palette.Text;
                }
                else
                {
                    _textPaint.Color = palette.Text.WithAlpha(210);
                }

                string curNumStr = cell.Day.ToString(CultureInfo.InvariantCulture);
                float curNw = FontHelper.MeasureTextWithFallback(curNumStr, dayFont);
                canvas.DrawTextWithFallback(curNumStr, cx - curNw / 2f, cy - dayFont.Metrics.Top * 0.4f, dayFont, _textPaint);

                if (cell.HasEvents)
                {
                    _fillPaint.Color = cell.IsToday ? _textPaint.Color : palette.Accent;
                    canvas.DrawCircle(cx, cy + cellH * 0.32f, 1.5f * scale, _fillPaint);
                }
                else if (cell.IsNotable)
                {
                    _fillPaint.Color = palette.Text.WithAlpha(140);
                    canvas.DrawCircle(cx, cy + cellH * 0.32f, 1.2f * scale, _fillPaint);
                }
            }
        }

        DrawNotableDatesFooter(canvas, _layout.NotableDatesRect, display, palette, scale);
    }

    private void DrawChevron(SKCanvas canvas, SKRect rect, string text, SKColor color, float scale)
    {
        if (rect.IsEmpty)
            return;
        _fillPaint.Color = color.WithAlpha(30);
        canvas.DrawRoundRect(rect, 5f * scale, 5f * scale, _fillPaint);
        _strokePaint.Color = color.WithAlpha(60);
        _strokePaint.StrokeWidth = 1f * scale;
        canvas.DrawRoundRect(rect, 5f * scale, 5f * scale, _strokePaint);
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 12f * scale);
        _textPaint.Color = color;
        float tw = FontHelper.MeasureTextWithFallback(text, font);
        canvas.DrawTextWithFallback(text, rect.MidX - tw / 2f, rect.MidY - font.Metrics.Top * 0.42f, font, _textPaint);
    }

    private void DrawNotableDatesFooter(SKCanvas canvas, SKRect rect, CalendarDisplay display, CalendarSeasonalPalette palette, float scale)
    {
        if (rect.IsEmpty || rect.Height < 14f * scale)
            return;

        _strokePaint.Color = palette.Text.WithAlpha(30);
        _strokePaint.StrokeWidth = 1f * scale;
        canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Top, _strokePaint);

        IReadOnlyList<NotableDateItem> items = display.SafeNotableDates;
        if (items.Count == 0)
            return;

        var labelFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 9f * scale);
        float y = rect.Top + 13f * scale;
        float curX = rect.Left;

        for (int i = 0; i < items.Count && i < 3; i++)
        {
            NotableDateItem item = items[i];
            string entry = $"{item.Tag}  {item.Label}";
            if (i < items.Count - 1 && i < 2) entry += "   ·   ";
            float entryW = FontHelper.MeasureTextWithFallback(entry, labelFont);
            if (curX + entryW > rect.Right && i > 0)
                break;

            _textPaint.Color = item.IsFeedEvent ? palette.Accent : palette.Text.WithAlpha(200);
            canvas.DrawTextWithFallback(entry, curX, y, labelFont, _textPaint);
            curX += entryW;
        }
    }

    private void DrawAgendaPanel(SKCanvas canvas, SKRect rect, CalendarDisplay display, CalendarSeasonalPalette palette, float scale)
    {
        if (rect.IsEmpty)
            return;

        _cardPaint.Color = new SKColor(18, 20, 28);
        canvas.DrawRoundRect(rect, 14f * scale, 14f * scale, _cardPaint);

        _strokePaint.Color = SKColors.White.WithAlpha(15);
        _strokePaint.StrokeWidth = 1f * scale;
        canvas.DrawRoundRect(rect, 14f * scale, 14f * scale, _strokePaint);

        // Header
        var hFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f * scale);
        _textPaint.Color = new SKColor(120, 160, 255);
        canvas.DrawTextWithFallback("UPCOMING AGENDA", rect.Left + 14f * scale, rect.Top + 16f * scale, hFont, _textPaint);

        var dFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 13f * scale);
        _textPaint.Color = SKColors.White;
        canvas.DrawTextWithFallback(display.DateHeaderText, rect.Left + 14f * scale, rect.Top + 33f * scale, dFont, _textPaint);

        // Hero Card (Next Event)
        SKRect heroRect = _layout.AllDayRect;
        if (!heroRect.IsEmpty && heroRect.Height > 20f * scale)
        {
            CalendarRow? nextEv = display.NextUpcomingEvent;
            if (nextEv != null)
            {
                _fillPaint.Color = new SKColor(26, 30, 42);
                canvas.DrawRoundRect(heroRect, 8f * scale, 8f * scale, _fillPaint);

                _fillPaint.Color = nextEv.IsLive ? new SKColor(239, 68, 68) : palette.Accent;
                canvas.DrawRoundRect(new SKRect(heroRect.Left, heroRect.Top, heroRect.Left + 4f * scale, heroRect.Bottom), 2f * scale, 2f * scale, _fillPaint);

                if (!string.IsNullOrEmpty(display.NextUpcomingCountdown))
                {
                    string cd = display.NextUpcomingCountdown;
                    var cdFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 9f * scale);
                    float cdw = FontHelper.MeasureTextWithFallback(cd, cdFont);
                    var badgeRect = new SKRect(heroRect.Right - cdw - 16f * scale, heroRect.Top + 6f * scale, heroRect.Right - 8f * scale, heroRect.Top + 20f * scale);
                    _fillPaint.Color = nextEv.IsLive ? new SKColor(239, 68, 68).WithAlpha(50) : palette.Accent.WithAlpha(50);
                    canvas.DrawRoundRect(badgeRect, 4f * scale, 4f * scale, _fillPaint);
                    _textPaint.Color = nextEv.IsLive ? new SKColor(254, 202, 202) : palette.Accent;
                    canvas.DrawTextWithFallback(cd, badgeRect.Left + 4f * scale, badgeRect.MidY - cdFont.Metrics.Top * 0.4f, cdFont, _textPaint);
                }

                var tFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 13f * scale);
                _textPaint.Color = SKColors.White;
                string title = TextRenderHelper.TruncateText(nextEv.Title, tFont, Math.Max(20f, heroRect.Width - 90f * scale));
                canvas.DrawTextWithFallback(title, heroRect.Left + 12f * scale, heroRect.Top + 16f * scale, tFont, _textPaint);

                var subFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 10f * scale);
                _textPaint.Color = SKColors.White.WithAlpha(170);
                string sub = string.IsNullOrEmpty(nextEv.FeedLabel) ? nextEv.TimeText : $"{nextEv.TimeText} · {nextEv.FeedLabel}";
                canvas.DrawTextWithFallback(sub, heroRect.Left + 12f * scale, heroRect.Top + 29f * scale, subFont, _textPaint);
            }
            else
            {
                _fillPaint.Color = new SKColor(24, 26, 36);
                canvas.DrawRoundRect(heroRect, 8f * scale, 8f * scale, _fillPaint);
                var emptyFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 11f * scale);
                _textPaint.Color = SKColors.White.WithAlpha(120);
                string hint = display.HasData ? "No upcoming events scheduled" : display.StalenessHint;
                canvas.DrawTextWithFallback(hint, heroRect.Left + 12f * scale, heroRect.MidY - emptyFont.Metrics.Top * 0.4f, emptyFont, _textPaint);
            }
        }

        SKRect scrollArea = _layout.AgendaScrollAreaRect;
        if (!scrollArea.IsEmpty)
        {
            canvas.Save();
            canvas.ClipRect(scrollArea);
            DrawRows(canvas, display, scale, _agendaScrollY);
            canvas.Restore();

            // Subtle vertical scrollbar indicator when events exceed viewport
            if (_maxAgendaScrollY > 0f)
            {
                float scrollTrackH = scrollArea.Height - 8f * scale;
                float thumbH = Math.Max(20f * scale, scrollTrackH * (scrollArea.Height / (scrollArea.Height + _maxAgendaScrollY)));
                float scrollRatio = _agendaScrollY / _maxAgendaScrollY;
                float thumbY = scrollArea.Top + 4f * scale + scrollRatio * (scrollTrackH - thumbH);
                float thumbX = scrollArea.Right - 5f * scale;

                _fillPaint.Color = SKColors.White.WithAlpha(60);
                canvas.DrawRoundRect(new SKRect(thumbX, thumbY, thumbX + 3f * scale, thumbY + thumbH), 1.5f * scale, 1.5f * scale, _fillPaint);
            }
        }
        else
        {
            DrawRows(canvas, display, scale, 0f);
        }
    }

    private void DrawCompactPoster(SKCanvas canvas, CalendarGeometry layout, CalendarDisplay display, CalendarSeasonalPalette palette, float scale, DateTime viewDate)
    {
        DrawPosterMonthCard(canvas, layout.MonthCardRect, display, palette, scale, viewDate);

        if (layout.RowRects.Count > 0 && !layout.RowRects[0].IsEmpty)
        {
            SKRect rowRect = layout.RowRects[0];
            _fillPaint.Color = new SKColor(18, 20, 28).WithAlpha(230);
            canvas.DrawRoundRect(rowRect, 6f * scale, 6f * scale, _fillPaint);

            CalendarRow? ev = display.NextUpcomingEvent ?? (display.Rows.Count > 0 ? display.Rows[0] : null);
            if (ev != null)
            {
                var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f * scale);
                _textPaint.Color = palette.Accent;
                canvas.DrawTextWithFallback(ev.TimeText, rowRect.Left + 8f * scale, rowRect.MidY - font.Metrics.Top * 0.4f, font, _textPaint);

                var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 11f * scale);
                _textPaint.Color = SKColors.White;
                string t = TextRenderHelper.TruncateText(ev.Title, titleFont, Math.Max(20f, rowRect.Width - 65f * scale));
                canvas.DrawTextWithFallback(t, rowRect.Left + 52f * scale, rowRect.MidY - titleFont.Metrics.Top * 0.4f, titleFont, _textPaint);
            }
        }
    }

    private void DrawClassicView(SKCanvas canvas, SKRect bounds, CalendarDisplay display, float scale)
    {
        DrawBackground(canvas, bounds, scale);
        if (!display.HasData)
        {
            DrawUnavailable(canvas, bounds, scale, display.StalenessHint);
            return;
        }
        DrawHeader(canvas, display, scale);
        DrawClassicMonthGrid(canvas, display, scale);
        DrawRows(canvas, display, scale);
    }

    private void DrawBackground(SKCanvas canvas, SKRect bounds, float scale)
    {
        _fillPaint.Color = new SKColor(18, 18, 24);
        canvas.DrawRoundRect(bounds, 16f * scale, 16f * scale, _fillPaint);
    }

    private void DrawUnavailable(SKCanvas canvas, SKRect bounds, float scale, string hint)
    {
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 20f * scale);
        _textPaint.Color = ColorOf(TextColorHex, SKColors.White).WithAlpha(160);
        var tb = new SKRect();
        font.MeasureText(hint, out tb, _textPaint);
        canvas.DrawTextWithFallback(hint, bounds.MidX - tb.Width / 2f, bounds.MidY - tb.Height / 2f, font, _textPaint);
    }

    private void DrawHeader(SKCanvas canvas, CalendarDisplay display, float scale)
    {
        SKRect rect = _layout.HeaderRect;
        if (rect.IsEmpty || string.IsNullOrEmpty(display.DateHeaderText))
            return;

        SKColor text = ColorOf(TextColorHex, SKColors.White);

        var monthFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 13f * scale);
        _textPaint.Color = text.WithAlpha(180);
        canvas.DrawTextWithFallback(display.MonthTitle, rect.Left + 2f * scale, rect.Top + 2f * scale - monthFont.Metrics.Top, monthFont, _textPaint);

        var dateFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 18f * scale);
        _textPaint.Color = text;
        canvas.DrawTextWithFallback(display.DateHeaderText, rect.Left + 2f * scale, rect.Bottom - 8f * scale - dateFont.Metrics.Top, dateFont, _textPaint);
    }

    private void DrawClassicMonthGrid(SKCanvas canvas, CalendarDisplay display, float scale)
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

            if (cell.IsViewed)
            {
                _fillPaint.Color = accent.WithAlpha(60);
                canvas.DrawCircle(cx, cy, cellW * 0.42f, _fillPaint);
            }
            else if (cell.IsToday)
            {
                _strokePaint.Color = accent;
                _strokePaint.StrokeWidth = 1.5f * scale;
                canvas.DrawCircle(cx, cy, cellW * 0.38f, _strokePaint);
            }

            SKColor numColor;
            if (cell.IsViewed)
                numColor = SKColors.White;
            else if (cell.IsToday)
                numColor = accent;
            else
                numColor = text.WithAlpha(180);

            _textPaint.Color = numColor;
            string dayStr = cell.Day.ToString(CultureInfo.InvariantCulture);
            float dayW = FontHelper.MeasureTextWithFallback(dayStr, font);
            canvas.DrawTextWithFallback(dayStr, cx - dayW / 2f, cy - font.Metrics.Top * 0.5f, font, _textPaint);

            if (cell.HasEvents)
            {
                _fillPaint.Color = cell.IsViewed ? SKColors.White : new SKColor(245, 158, 11);
                canvas.DrawCircle(cx, cy + cellH * 0.3f, 1.5f * scale, _fillPaint);
            }
        }
    }

    private void DrawRows(SKCanvas canvas, CalendarDisplay display, float scale, float scrollY = 0f)
    {
        SKColor text = ColorOf(TextColorHex, SKColors.White);
        float gutterW = _layout.TimeGutterWidth;

        for (int i = 0; i < display.Rows.Count && i < _layout.RowRects.Count; i++)
        {
            SKRect baseRect = _layout.RowRects[i];
            if (baseRect.IsEmpty)
                continue;

            SKRect rect = Math.Abs(scrollY) > 0.001f
                ? new SKRect(baseRect.Left, baseRect.Top - scrollY, baseRect.Right, baseRect.Bottom - scrollY)
                : baseRect;

            if (!_layout.AgendaScrollAreaRect.IsEmpty &&
                (rect.Bottom < _layout.AgendaScrollAreaRect.Top || rect.Top > _layout.AgendaScrollAreaRect.Bottom))
            {
                continue;
            }

            CalendarRow row = display.Rows[i];

            if (row.IsLive)
            {
                _fillPaint.Color = new SKColor(239, 68, 68);
                canvas.DrawRoundRect(new SKRect(rect.Left, rect.Top, rect.Left + 4f * scale, rect.Bottom), 2f * scale, 2f * scale, _fillPaint);
            }

            SKColor timeColor;
            if (row.IsLive)
                timeColor = new SKColor(239, 68, 68);
            else if (row.IsUrgent)
                timeColor = new SKColor(245, 158, 11);
            else
                timeColor = text;

            float fontSize = Math.Min(13f * scale, Math.Max(10f, rect.Height * 0.48f));
            var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontSize);
            _textPaint.Color = timeColor;
            float timeX = rect.Left + 10f * scale;
            float timeY = rect.MidY - timeFont.Metrics.Top * 0.45f;
            canvas.DrawTextWithFallback(row.TimeText, timeX, timeY, timeFont, _textPaint);

            float titleX = rect.Left + gutterW;
            float titleMaxW = rect.Width - gutterW - 10f * scale;
            var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, fontSize);
            _textPaint.Color = text.WithAlpha(230);
            float titleY = rect.MidY - titleFont.Metrics.Top * 0.45f;

            if (!string.IsNullOrEmpty(row.FeedLabel))
            {
                var feedFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(9f, fontSize * 0.85f));
                _textPaint.Color = new SKColor(120, 160, 255);
                string feedTag = $"[{row.FeedLabel}] ";
                float feedW = FontHelper.MeasureTextWithFallback(feedTag, feedFont);
                canvas.DrawTextWithFallback(feedTag, titleX, titleY, feedFont, _textPaint);
                titleX += feedW;
                titleMaxW -= feedW;
            }

            canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(row.Title, titleFont, Math.Max(20f, titleMaxW)),
                titleX, titleY, titleFont, _textPaint);

            if (i < display.Rows.Count - 1)
            {
                _strokePaint.Color = text.WithAlpha(25);
                _strokePaint.StrokeWidth = 1f * scale;
                canvas.DrawLine(rect.Left + 4f * scale, rect.Bottom + 2f * scale, rect.Right, rect.Bottom + 2f * scale, _strokePaint);
            }
        }
    }

    private void DrawDetailView(SKCanvas canvas, SKRect bounds, float scale)
    {
        CalendarEvent ev = _detailEvent!.Value;

        _cardPaint.Color = new SKColor(18, 18, 24);
        canvas.DrawRoundRect(bounds, 16f * scale, 16f * scale, _cardPaint);

        SKColor text = ColorOf(TextColorHex, SKColors.White);
        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);
        float pad = 20f * scale;

        var backFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 13f * scale);
        _textPaint.Color = text.WithAlpha(120);
        canvas.DrawTextWithFallback("Tap to go back", bounds.Left + pad, bounds.Top + pad - backFont.Metrics.Top, backFont, _textPaint);

        float cardLeft = bounds.Left + pad;
        float cardRight = bounds.Right - pad;
        float cardTop = bounds.Top + pad + 30f * scale;
        float cardBottom = bounds.Bottom - pad;
        _cardPaint.Color = accent.WithAlpha(20);
        canvas.DrawRoundRect(new SKRect(cardLeft, cardTop, cardRight, cardBottom), 12f * scale, 12f * scale, _cardPaint);

        bool isLive = ev.Start <= Clock.GetLocalNow().LocalDateTime && Clock.GetLocalNow().LocalDateTime < ev.End;
        _fillPaint.Color = isLive ? new SKColor(239, 68, 68) : accent;
        canvas.DrawRoundRect(new SKRect(cardLeft, cardTop, cardLeft + 5f * scale, cardBottom), 2.5f * scale, 2.5f * scale, _fillPaint);

        float x = cardLeft + 16f * scale;
        float y = cardTop + 16f * scale;
        float maxW = cardRight - x - 12f * scale;

        string timeStr = ev.IsAllDay ? "All day" : $"{ev.Start:HH:mm} \u2013 {ev.End:HH:mm}";
        var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 18f * scale);
        _textPaint.Color = isLive ? new SKColor(239, 68, 68) : accent;
        canvas.DrawTextWithFallback(timeStr, x, y, timeFont, _textPaint);
        y += 30f * scale;

        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 20f * scale);
        _textPaint.Color = text;
        string title = string.IsNullOrWhiteSpace(ev.Title) ? "Untitled" : ev.Title;
        IReadOnlyList<string> titleLines = _wrapCache.GetOrWrap(title, titleFont, 20f * scale, maxW);
        foreach (string line in titleLines)
        {
            canvas.DrawTextWithFallback(line, x, y, titleFont, _textPaint);
            y += 26f * scale;
        }
        y += 6f * scale;

        if (!string.IsNullOrWhiteSpace(ev.FeedLabel))
        {
            var feedFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 14f * scale);
            _textPaint.Color = new SKColor(120, 160, 255);
            canvas.DrawTextWithFallback($"[{ev.FeedLabel}]", x, y, feedFont, _textPaint);
            y += 24f * scale;
        }

        if (!string.IsNullOrWhiteSpace(ev.Location))
        {
            var locFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 15f * scale);
            _textPaint.Color = text.WithAlpha(200);
            IReadOnlyList<string> locLines = _wrapCache.GetOrWrap(ev.Location, locFont, 15f * scale, maxW);
            foreach (string line in locLines)
            {
                canvas.DrawTextWithFallback(line, x, y, locFont, _textPaint);
                y += 20f * scale;
            }
            y += 6f * scale;
        }

        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            var descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 14f * scale);
            _textPaint.Color = text.WithAlpha(180);
            IReadOnlyList<string> descLines = _wrapCache.GetOrWrap(ev.Description, descFont, 14f * scale, maxW);
            foreach (string line in descLines)
            {
                canvas.DrawTextWithFallback(line, x, y, descFont, _textPaint);
                y += 18f * scale;
            }
            y += 6f * scale;
        }

        if (!ev.IsAllDay)
        {
            TimeSpan dur = ev.End - ev.Start;
            string durStr = dur.TotalHours >= 1
                ? $"{(int)dur.TotalHours}h {dur.Minutes:D2}m"
                : $"{dur.Minutes}m";
            var durFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 14f * scale);
            _textPaint.Color = text.WithAlpha(160);
            canvas.DrawTextWithFallback($"Duration: {durStr}", x, y, durFont, _textPaint);
            y += 24f * scale;
        }

        if (!string.IsNullOrWhiteSpace(ev.Url))
        {
            var urlFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 14f * scale);
            _textPaint.Color = new SKColor(120, 160, 255);
            canvas.DrawTextWithFallback("\U0001F517 Tap link to open", x, y, urlFont, _textPaint);
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
