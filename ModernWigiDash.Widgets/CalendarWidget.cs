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
    // The feed count is a cached derived fact, never a per-frame parse: the
    // render tick reads it without touching the JSON (a parse on the 30 FPS
    // tick would allocate and log every frame). It is refreshed on the
    // write-through path (RestartProducer, when the feeds config changes)
    // and lazily on first render (the ??= in Render).
    private int? _feedCount;
    private readonly CalendarCanvas _canvas = new();

    /// <summary>The last computed display facts (test seam): retained for test compatibility.</summary>
    internal CalendarDisplay? LastDisplay => _canvas.LastDisplay;

    /// <summary>How many times the display-facts compute actually ran (test seam).</summary>
    internal int DisplayRecomputeCount => _canvas.DisplayRecomputeCount;

    /// <summary>The gesture module's current detail-mode event (null = agenda view). Test seam.</summary>
    internal CalendarEvent? GestureDetailEventForTest => _canvas.DetailEvent;

    /// <summary>The shell-open seam: opens a meeting link in the default browser.</summary>
    internal Action<string>? OpenUrlSeam
    {
        get => _canvas.OpenUrlSeam;
        set => _canvas.OpenUrlSeam = value;
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
        FeedsResult result = CalendarFeedsCodec.Parse(FeedsJson);
        // Only a malformed value deserves a log line (a blank or well-formed
        // empty value is the silent default state).
        if (result.Malformed)
            Context?.LogError($"Calendar: feeds JSON is malformed; rendering the unavailable display. Value: {LogSanitizer.Sanitize(FeedsJson)}");
        return result.Feeds;
    }

    /// <summary>
    /// Binds the context and starts the feed producer over the parsed feeds.
    /// Re-initialization (a rehydrated instance) restarts the loop cleanly.
    /// </summary>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        _canvas.Context = context;
        RestartProducer();
    }

    /// <summary>Stops the running producer and releases canvas resources (teardown).</summary>
    public override ValueTask DisposeAsync()
    {
        _canvas.Dispose();
        lock (_producerGate)
        {
            _producer?.Dispose();
            _producer = null;
        }
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
        _feedCount = feeds.Count;
        lock (_producerGate)
        {
            _producer?.Dispose();
            _producer = null;
            if (feeds.Count == 0)
            {
                // No feeds: this instance has no producer and renders its own
                // unavailable state (see Render). It must NOT write the shared
                // process-wide store -- another calendar instance may own it, and
                // clearing it here would clobber that instance's live snapshot.
                // Also exit detail mode if one is active: the feed-less render
                // draws the agenda/unavailable view, so the gesture module must
                // not keep interpreting taps through the now-invisible detail
                // branch (the canvas and touch state would disagree).
                _canvas.ExitDetail();
                return;
            }

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

    /// <inheritdoc/>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        int feedCount = _feedCount ??= ParseFeeds().Count;
        DateTime now = Clock.GetLocalNow().LocalDateTime;
        CalendarSnapshot? snapshot = feedCount > 0 ? CalendarEventStore.ReadSnapshot() : null;

        _canvas.Render(
            canvas,
            bounds,
            LayoutMode,
            ThemeMode,
            AccentColorHex,
            TextColorHex,
            TimedRows,
            feedCount,
            snapshot,
            now);
    }

    /// <inheritdoc/>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
        => _canvas.OnTouch(localPoint, eventType);

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
}
