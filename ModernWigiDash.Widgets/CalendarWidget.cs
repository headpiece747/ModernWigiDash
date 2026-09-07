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

    // The per-mode draw paths live in the renderer (CalendarWidgetRenderer),
    // which owns the hoisted paints and the detail view's word-wrap cache.
    private readonly CalendarWidgetRenderer _renderer = new();

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
        _renderer.Dispose();
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
            _renderer.RenderMinimalDateCard(canvas, bounds, palette, scale, viewDate);
            return;
        }

        CalendarSnapshot? snapshot = CalendarEventStore.ReadSnapshot();

        // Detail mode: render the single event's detail view.
        if (_detailEvent is not null)
        {
            _renderer.RenderDetailView(canvas, bounds, scale, _detailEvent.Value, ColorOf(TextColorHex, SKColors.White), ColorOf(AccentColorHex, WidgetPalette.Accent), now);
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
            _renderer.RenderClassicView(canvas, bounds, _layout, display, scale, ColorOf(TextColorHex, SKColors.White), ColorOf(AccentColorHex, WidgetPalette.Accent));
            return;
        }

        _renderer.RenderAdaptiveView(canvas, bounds, _layout, display, palette, scale, viewDate, _agendaScrollY, _maxAgendaScrollY);
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
            CalendarEvent? matched = CalendarEventMatcher.Match(CalendarEventStore.ReadSnapshot(), display.NextUpcomingEvent);
            if (matched is not null)
            {
                _detailEvent = matched;
                Context?.RequestRender();
                return;
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

        CalendarEvent? foundEvent = CalendarEventMatcher.Match(CalendarEventStore.ReadSnapshot(), selectedRow);
        if (foundEvent is not null)
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
