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

    /// <summary>Show the all-day pill for date-only items.</summary>
    [WidgetProperty("Show All-Day Pill", WidgetPropertyType.Boolean, "Aggregate date-only events into a pill.", true)]
    public bool ShowAllDayPill { get; set; } = true;

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
    /// Draws the calendar: the hero band, the timed rows, and the all-day pill
    /// from the frame's display facts and layout record. A no-data snapshot
    /// renders the named unavailable display (ADR-0017).
    /// </summary>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        float scale = Math.Min(bounds.Width / CalendarLayout.DesignWidth, bounds.Height / CalendarLayout.DesignHeight);
        CalendarSnapshot? snapshot = CalendarEventStore.ReadSnapshot();
        DateTime now = Clock.GetLocalNow().LocalDateTime;
        int rows = CalendarFeedPolicy.ResolveTimedRows(TimedRows);
        CalendarDisplay display = CalendarPresentation.Build(snapshot, now, rows);
        _display = display;

        bool hasHero = display.HasData && !string.IsNullOrEmpty(display.HeroTitle);
        bool hasAllDay = ShowAllDayPill && display.AllDayPill.Count > 0;
        _layout = CalendarLayout.Compute(bounds, scale, display.Rows.Count, hasHero, hasAllDay);

        DrawBackground(canvas, bounds, scale);

        if (!display.HasData)
        {
            DrawUnavailable(canvas, bounds, scale, display.StalenessHint);
            return;
        }

        DrawHero(canvas, display, scale);
        DrawRows(canvas, display, scale);
        if (hasAllDay)
            DrawAllDayPill(canvas, display, scale);
    }

    /// <summary>
    /// A tap opens the tapped event's meeting link (the hero band is row 0, a
    /// timed row is its own index). The all-day pill carries no per-event link,
    /// so it is a no-op. The URL routes through the http/https/mailto gate and
    /// the shell-open seam; an absent or disallowed link is a logged no-op,
    /// never a throw.
    /// </summary>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType != TouchEventType.TouchUp)
            return;

        CalendarDisplay? display = _display;
        if (display is null || !display.HasData)
            return;

        int rowIndex = CalendarLayout.GetAction(_layout, localPoint.X, localPoint.Y, out bool onHero, out _);
        if (!onHero && rowIndex < 0)
            return;

        // The hero band is the first row in the display's row list; a timed-row
        // hit returns its own index.
        int target = onHero ? 0 : rowIndex;
        if (target >= display.Rows.Count)
            return;

        string url = display.Rows[target].Url;
        OpenMeetingLink(url);
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

    private void DrawHero(SKCanvas canvas, CalendarDisplay display, float scale)
    {
        SKRect rect = _layout.HeroRect;
        if (rect.IsEmpty)
            return;

        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);
        SKColor text = ColorOf(TextColorHex, SKColors.White);

        // A live event tints red; otherwise the accent carries the hero.
        SKColor heroTint = display.HeroIsLive ? new SKColor(239, 68, 68) : accent;
        var bg = new SKPaint { Color = heroTint.WithAlpha(40), IsAntialias = true };
        canvas.DrawRoundRect(rect, 12f * scale, 12f * scale, bg);

        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 24f * scale);
        var titlePaint = new SKPaint { Color = text, IsAntialias = true };
        canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(display.HeroTitle, titleFont, rect.Width - 24f * scale),
            rect.Left + 12f * scale, rect.Top + 14f * scale - titleFont.Metrics.Top, titleFont, titlePaint);

        if (!string.IsNullOrEmpty(display.HeroCountdown))
        {
            var cdFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 18f * scale);
            var cdPaint = new SKPaint { Color = heroTint, IsAntialias = true };
            canvas.DrawTextWithFallback(display.HeroCountdown, rect.Left + 12f * scale, rect.Bottom - 10f * scale - cdFont.Metrics.Top, cdFont, cdPaint);
        }
    }

    private void DrawRows(SKCanvas canvas, CalendarDisplay display, float scale)
    {
        SKColor text = ColorOf(TextColorHex, SKColors.White);
        for (int i = 0; i < display.Rows.Count && i < _layout.RowRects.Count; i++)
        {
            SKRect rect = _layout.RowRects[i];
            if (rect.IsEmpty)
                continue;

            CalendarRow row = display.Rows[i];
            SKColor rowColor = row.IsUrgent ? new SKColor(245, 158, 11) : text;

            var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 18f * scale);
            var timePaint = new SKPaint { Color = rowColor, IsAntialias = true };
            canvas.DrawTextWithFallback(row.TimeText, rect.Left + 10f * scale, rect.MidY - timeFont.Metrics.Top * 0.5f, timeFont, timePaint);

            var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 18f * scale);
            var titlePaint = new SKPaint { Color = text.WithAlpha(220), IsAntialias = true };
            float timeW = FontHelper.MeasureTextWithFallback(row.TimeText, timeFont) + 14f * scale;
            canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(row.Title, titleFont, rect.Width - timeW - 10f * scale),
                rect.Left + timeW, rect.MidY - titleFont.Metrics.Top * 0.5f, titleFont, titlePaint);
        }
    }

    private void DrawAllDayPill(SKCanvas canvas, CalendarDisplay display, float scale)
    {
        SKRect rect = _layout.AllDayPillRect;
        if (rect.IsEmpty)
            return;

        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);
        var bg = new SKPaint { Color = accent.WithAlpha(30), IsAntialias = true };
        canvas.DrawRoundRect(rect, rect.Height / 2f, rect.Height / 2f, bg);

        string label = $"All day: {display.AllDayPill.Label}" + (display.AllDayPill.Count > 1 ? $" (+{display.AllDayPill.Count - 1})" : "");
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 16f * scale);
        var paint = new SKPaint { Color = ColorOf(TextColorHex, SKColors.White).WithAlpha(200), IsAntialias = true };
        canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(label, font, rect.Width - 20f * scale),
            rect.Left + 10f * scale, rect.MidY - font.Metrics.Top * 0.5f, font, paint);
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
