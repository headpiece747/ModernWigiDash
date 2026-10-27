namespace ModernWigiDash.Widgets;

using SkiaSharp;
using ModernWigiDash.Sdk;

/// <summary>
/// Deep presentation and interaction canvas for the Calendar widget.
/// Encapsulates layout computation, day selection, drag physics,
/// word-wrap caching, display facts memoization, and Skia rendering behind a cohesive interface.
/// </summary>
internal sealed class CalendarCanvas : IDisposable
{
    private readonly CalendarWidgetRenderer _renderer = new();
    private readonly CalendarGestureState _gesture = new();
    private readonly MemoSlot<CalendarDisplayKey, CalendarDisplay> _displayMemo = new();
    private CalendarGeometry _layout;
    private bool _layoutValid;
    private SKRect _layoutBounds;
    private int _layoutRowCount;
    private string _layoutMode = "";

    private readonly record struct CalendarDisplayKey(
        int SnapshotIdentity, DateTime ViewDate, int MinuteOfDay, int Rows);

    /// <summary>The gesture module's current detail-mode event (null = agenda view).</summary>
    public CalendarEvent? DetailEvent => _gesture.DetailEvent;

    /// <summary>Widget context for logging and render requests.</summary>
    internal IModernWigiDashContext? Context
    {
        get => _gesture.Context;
        set => _gesture.Context = value;
    }

    /// <summary>The last computed display facts (test seam).</summary>
    public CalendarDisplay? LastDisplay => _displayMemo.LastValue;

    /// <summary>How many times the display-facts compute actually ran.</summary>
    public int DisplayRecomputeCount => _displayMemo.RecomputeCount;

    /// <summary>The shell-open seam: opens a meeting link in the default browser.</summary>
    public Action<string>? OpenUrlSeam
    {
        get => _gesture.OpenUrlSeam;
        set => _gesture.OpenUrlSeam = value;
    }

    /// <summary>Exits event detail mode back to the normal agenda view.</summary>
    public void ExitDetail() => _gesture.ExitDetail();

    /// <summary>Dispatches touch events to the internal gesture state machine.</summary>
    public void OnTouch(SKPoint localPoint, TouchEventType eventType)
        => _gesture.Feed(localPoint, eventType);

    /// <summary>
    /// The frame geometry, recomputed only when the bounds, the row count,
    /// or the layout mode changes (a steady-state tick reuses the record, so
    /// the render tick allocates no geometry; the CozyTodoCanvas gate model).
    /// </summary>
    private CalendarGeometry FrameLayout(SKRect bounds, int rowCount, string layoutMode)
    {
        if (_layoutValid
            && _layoutBounds == bounds
            && _layoutRowCount == rowCount
            && string.Equals(_layoutMode, layoutMode, StringComparison.Ordinal))
        {
            return _layout;
        }

        float scale = Math.Min(bounds.Width / CalendarLayout.DesignWidth, bounds.Height / CalendarLayout.DesignHeight);
        _layout = CalendarLayout.Compute(bounds, scale, rowCount, false, layoutMode);
        _layoutBounds = bounds;
        _layoutRowCount = rowCount;
        _layoutMode = layoutMode;
        _layoutValid = true;
        return _layout;
    }

    /// <summary>
    /// Renders the calendar: minimal date card, empty feeds state, event detail view,
    /// or adaptive/classic agenda view.
    /// </summary>
    public void Render(
        SKCanvas canvas,
        SKRect bounds,
        string layoutMode,
        string themeMode,
        string accentColorHex,
        string textColorHex,
        int timedRows,
        int feedCount,
        CalendarSnapshot? snapshot,
        DateTime now)
    {
        float scale = Math.Min(bounds.Width / CalendarLayout.DesignWidth, bounds.Height / CalendarLayout.DesignHeight);
        DateTime viewDate = now.Date.AddDays(_gesture.ViewDateOffset);
        CalendarSeasonalPalette palette = CalendarSeasonalPalettes.Resolve(viewDate, themeMode, accentColorHex, textColorHex);

        var mode = CalendarLayout.ResolveViewMode(bounds.Width, bounds.Height, layoutMode);
        if (mode == CalendarViewMode.MinimalDateCard1x1)
        {
            _layout = FrameLayout(bounds, 0, layoutMode);
            _gesture.SetFrameFacts(_layout, null, now);
            _renderer.RenderMinimalDateCard(canvas, bounds, palette, scale, viewDate);
            return;
        }

        if (feedCount == 0)
        {
            _layout = FrameLayout(bounds, 0, layoutMode);
            _gesture.SetFrameFacts(_layout, null, now);
            var emptyDisplay = CalendarPresentation.Build(null, now, viewDate, 0);
            _renderer.RenderAdaptiveView(canvas, bounds, _layout, emptyDisplay, palette, scale, now, viewDate, 0f, 0f);
            return;
        }

        if (_gesture.DetailEvent is not null)
        {
            _gesture.SetFrameFacts(_layout, null, now);
            CalendarDetailFrame detail = _renderer.MeasureDetail(
                bounds, scale, _gesture.DetailEvent.Value, _gesture.DetailScrollY);
            _gesture.SetDetailGeometry(detail.Geometry);
            _renderer.RenderDetailView(
                detail, canvas, bounds, scale, _gesture.DetailEvent.Value,
                SkColorCache.ColorOf(textColorHex, SKColors.White),
                SkColorCache.ColorOf(accentColorHex, WidgetPalette.Accent),
                now);
            return;
        }

        int rows = (mode == CalendarViewMode.FullEditorial5x4 || mode == CalendarViewMode.SplitBanner4x2)
            ? 100
            : CalendarFeedPolicy.ResolveTimedRows(timedRows);

        var displayKey = new CalendarDisplayKey(
            snapshot is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(snapshot),
            viewDate.Date,
            now.Hour * 60 + now.Minute,
            rows);
        CalendarDisplay display;
        if (!_displayMemo.TryGet(displayKey, out display))
        {
            display = CalendarPresentation.Build(snapshot, now, viewDate, rows, hasFeeds: true);
            _displayMemo.Set(displayKey, display);
        }

        _layout = FrameLayout(bounds, display.Rows.Count, layoutMode);
        _gesture.SetFrameFacts(_layout, display, now, snapshot?.Events);
        _gesture.UpdateScrollExtent();

        if (_layout.Mode == CalendarViewMode.CompactPoster2x3 && bounds.Height < 280f)
        {
            _renderer.RenderClassicView(canvas, bounds, _layout, display, scale, SkColorCache.ColorOf(textColorHex, SKColors.White), SkColorCache.ColorOf(accentColorHex, WidgetPalette.Accent));
            return;
        }

        _renderer.RenderAdaptiveView(canvas, bounds, _layout, display, palette, scale, now, viewDate, _gesture.AgendaScrollY, _gesture.MaxAgendaScrollY);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _renderer.Dispose();
    }
}
