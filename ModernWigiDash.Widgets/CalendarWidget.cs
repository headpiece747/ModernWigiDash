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

    // The per-mode draw paths live in the renderer (CalendarWidgetRenderer),
    // which owns the hoisted paints and the detail view's word-wrap cache.
    private readonly CalendarWidgetRenderer _renderer = new();

    // The touch-state module owns the viewed-day offset, the detail-view
    // selection, the press point, and the agenda scroll position, and interprets
    // each Down/Move/Up sample. The widget keeps only Render and the forward
    // into Feed (the C1 extraction: "what does a tap do" has one owner).
    private readonly CalendarGestureState _gesture = new();

    /// <summary>The shell-open seam: opens a meeting link in the default
    /// browser. Production uses the OS default handler (the http/https/mailto
    /// gate + Process.Start live in the gesture module); tests bind a recorder so
    /// a tap is assertable without spawning a browser. The gesture module reads
    /// this live at open time, so a test's post-construction swap is honored on
    /// the next tap.</summary>
    internal Action<string>? OpenUrlSeam
    {
        get => _gesture.OpenUrlSeam;
        set => _gesture.OpenUrlSeam = value;
    }

    /// <summary>The clock seam (test-injectable; production is the system
    /// clock). Used for both the render's "now" read and the producer's poll
    /// timestamps.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    // Test seams: the production bind site constructs the fetchers/credential
    // store here; tests swap these before InitializeAsync to drive the loop
    // without a network or DPAPI.
    internal Func<IFeedFetcher>? FetchFactory;
    internal Func<CalendarCredentialStore>? CredentialFactory;

    /// <summary>Parses the <see cref="FeedsJson"/> property into feed records
    /// through the shared <see cref="CalendarFeedsCodec"/> owner (the one
    /// spelling of the persisted shape). A malformed or absent value yields an
    /// empty list (the unavailable display), never a throw; a malformed value
    /// logs one line so the user knows why the feeds did not load.</summary>
    internal IReadOnlyList<CalendarFeed> ParseFeeds()
    {
        IReadOnlyList<CalendarFeed> feeds = CalendarFeedsCodec.Parse(FeedsJson);
        // The codec returns [] for both "no feeds" and "malformed"; only the
        // latter deserves a log line (a blank or well-formed-empty value is the
        // silent default state).
        if (feeds.Count == 0 && !string.IsNullOrWhiteSpace(FeedsJson) && !IsWellFormedEmpty(FeedsJson))
            Context?.LogError($"Calendar: feeds JSON is malformed; rendering the unavailable display. Value: {TruncateForLog(FeedsJson)}");
        return feeds;
    }

    /// <summary>Whether the value is well-formed JSON that simply holds no feeds
    /// (an empty array or a non-array root) as opposed to malformed JSON. Used
    /// to decide whether a zero-feed parse is the silent default or a logged
    /// error.</summary>
    private static bool IsWellFormedEmpty(string feedsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(feedsJson);
            return doc.RootElement.ValueKind != JsonValueKind.Array
                   || !doc.RootElement.EnumerateArray().Any();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Binds the context and starts the feed producer over the parsed feeds.
    /// Re-initialization (a rehydrated instance) restarts the loop cleanly.
    /// </summary>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        _gesture.Context = context;
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
        DateTime viewDate = now.Date.AddDays(_gesture.ViewDateOffset);
        CalendarSeasonalPalette palette = CalendarSeasonalPalettes.Resolve(viewDate, ThemeMode, AccentColorHex, TextColorHex);

        var mode = CalendarLayout.ResolveViewMode(bounds.Width, bounds.Height, LayoutMode);
        if (mode == CalendarViewMode.MinimalDateCard1x1)
        {
            _layout = CalendarLayout.Compute(bounds, scale, 0, false, LayoutMode);
            _gesture.SetFrameFacts(_layout, null, now);
            _renderer.RenderMinimalDateCard(canvas, bounds, palette, scale, viewDate);
            return;
        }

        CalendarSnapshot? snapshot = CalendarEventStore.ReadSnapshot();

        // Detail mode: render the single event's detail view. The gesture module
        // keeps its own layout (the one drawn last frame) so a tap can still hit
        // the URL hint or exit; only the display and clock read are refreshed.
        if (_gesture.DetailEvent is not null)
        {
            _gesture.SetFrameFacts(_layout, null, now);
            _renderer.RenderDetailView(canvas, bounds, scale, _gesture.DetailEvent.Value, ColorOf(TextColorHex, SKColors.White), ColorOf(AccentColorHex, WidgetPalette.Accent), now);
            return;
        }

        int rows = (mode == CalendarViewMode.FullEditorial5x4 || mode == CalendarViewMode.SplitBanner4x2)
            ? 100
            : CalendarFeedPolicy.ResolveTimedRows(TimedRows);

        CalendarDisplay display = CalendarPresentation.Build(snapshot, now, viewDate, rows);

        _layout = CalendarLayout.Compute(bounds, scale, display.Rows.Count, false, LayoutMode);

        // The gesture module owns the agenda scroll state: it computes the max
        // extent from the frame's row rects and clamps the current offset, then
        // hands back the values the renderer draws with.
        _gesture.SetFrameFacts(_layout, display, now);
        _gesture.UpdateScrollExtent(scale);

        if (_layout.Mode == CalendarViewMode.CompactPoster2x3 && bounds.Height < 280f)
        {
            _renderer.RenderClassicView(canvas, bounds, _layout, display, scale, ColorOf(TextColorHex, SKColors.White), ColorOf(AccentColorHex, WidgetPalette.Accent));
            return;
        }

        _renderer.RenderAdaptiveView(canvas, bounds, _layout, display, palette, scale, viewDate, _gesture.AgendaScrollY, _gesture.MaxAgendaScrollY);
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
        => _gesture.Feed(localPoint, eventType);

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

    private static string TruncateForLog(string value) => value.Length <= 80 ? value : value[..80] + "...";
}
