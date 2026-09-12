using System.Diagnostics;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The calendar widget's touch-state module: owns the viewed-day offset, the
/// detail-view selection, the press point, and the agenda scroll position, and
/// interprets each Down/Move/Up sample into one of the named outcomes. The
/// widget keeps only Render and the forward into <see cref="Feed"/>; the layout
/// hit rules (<see cref="CalendarLayout"/>) and the row-to-event match
/// (<see cref="CalendarEventMatcher"/>) sit behind this module, so "what does a
/// tap do" has one owner instead of being spread across the widget's fields and
/// a long handler. Tests drive <see cref="Feed"/> directly against a rendered
/// frame's facts without re-reading the store per tap.
/// </summary>
/// <remarks>
/// Thread-safety contract: <see cref="Feed"/> (touch samples) and
/// <see cref="SetFrameFacts"/> / <see cref="SetDetailFrameFacts"/> (render-time
/// updates) must be called from the same thread. In production both run on the
/// WPF dispatcher (device touch is marshaled via <c>BeginInvoke</c>; the render
/// tick is a <c>DispatcherTimer</c>), so they are serialized. The mutable state
/// here is NOT protected by a lock; calling these methods concurrently from
/// different threads would race. Tests call them sequentially on one thread.
/// </remarks>
internal sealed class CalendarGestureState
{
    /// <summary>The maximum drag distance (in design units) that still counts as
    /// a tap rather than an intentional swipe.</summary>
    private const float TapDragTolerance = 15f;

    // The frame's facts, handed in by the widget at render time: the geometry
    // the draw path used, the display it drew, the clock read for the frame, and
    // the event list the display was built from. A touch sample arriving before
    // any render (or after a mode change) sees nulls and degrades to a no-op,
    // exactly as the old field defaults did. Matching a tapped row against the
    // handed-in event list (instead of re-reading the global store mid-tap) keeps
    // the module's claim that a tap interprets the same record the canvas drew.
    private CalendarGeometry _layout;
    private CalendarDisplay? _display;
    private DateTime _now;
    private IReadOnlyList<CalendarEvent> _events = [];

    // The mutable touch state this module owns.
    private int _viewDateOffset;
    private CalendarEvent? _detailEvent;
    private float _detailScrollY;
    private float _maxDetailScrollY;
    private float _touchStartDetailScrollY;
    private bool _isDraggingDetail;
    private bool _isDetailScrolled;
    private SKRect _detailCardRect;
    private SKRect _detailUrlRect;
    private SKPoint? _touchDown;
    private float _agendaScrollY;
    private float _touchStartScrollY;
    private bool _isDraggingAgenda;
    private bool _isAgendaScrolled;
    private float _maxAgendaScrollY;

    /// <summary>The seam the module routes a meeting-link open through (the
    /// http/https/mailto gate + shell-open live on the widget side; tests bind a
    /// recorder so a tap is assertable without spawning a browser).</summary>
    internal Action<string>? OpenUrlSeam { get; set; }

    /// <summary>The host-services seam the module routes its repaint requests
    /// and refusal logs through (the widget binds its own context at
    /// construction). Null before the widget hands it over, which cannot happen
    /// in practice: the widget constructs this module in its field initializer,
    /// before any touch sample can arrive.</summary>
    internal IModernWigiDashContext? Context { get; set; }

    /// <summary>The viewed day's offset from today (0 = today, 1 = tomorrow,
    /// -1 = yesterday), advanced by the chevron taps and the month-grid cell
    /// taps.</summary>
    public int ViewDateOffset => _viewDateOffset;

    /// <summary>The event shown in detail mode (null = normal agenda view), set
    /// when a timed row or the hero card is tapped.</summary>
    public CalendarEvent? DetailEvent => _detailEvent;

    /// <summary>The current vertical scroll offset of the event detail card
    /// (the renderer draws the detail content shifted by this amount).</summary>
    public float DetailScrollY => _detailScrollY;

    /// <summary>The maximum vertical scroll extent for the current event detail card
    /// (0 when the content fits inside the card without overflowing).</summary>
    public float MaxDetailScrollY => _maxDetailScrollY;

    /// <summary>The current vertical scroll offset of the agenda rows viewport
    /// (the renderer draws the rows shifted by this amount).</summary>
    public float AgendaScrollY => _agendaScrollY;

    /// <summary>The maximum vertical scroll extent for the current agenda rows
    /// (0 when the rows fit the viewport).</summary>
    public float MaxAgendaScrollY => _maxAgendaScrollY;

    /// <summary>Receives the detail view card layout and max scroll extent computed during render,
    /// clamping the current detail scroll position into valid range.</summary>
    public void SetDetailFrameFacts(SKRect cardRect, float maxScrollY, SKRect urlRect)
    {
        _detailCardRect = cardRect;
        _maxDetailScrollY = Math.Max(0f, maxScrollY);
        _detailScrollY = Math.Clamp(_detailScrollY, 0f, _maxDetailScrollY);
        _detailUrlRect = urlRect;
    }

    /// <summary>Receives the frame's facts after the widget composes them: the
    /// geometry, the display, the clock read, and the event list the display was
    /// built from. Called once per render, so a touch sample always interprets
    /// the same record the canvas drew (a row resolves against the events that
    /// produced it, not a store re-read that may have moved on).</summary>
    public void SetFrameFacts(CalendarGeometry layout, CalendarDisplay? display, DateTime now, IReadOnlyList<CalendarEvent>? events = null)
    {
        _layout = layout;
        _display = display;
        _now = now;
        _events = events ?? [];
    }

    /// <summary>Recomputes the agenda's max scroll extent from the frame's row
    /// rects and clamps the current offset into range. Called once per render
    /// (after <see cref="SetFrameFacts"/>) so a resize or a row-count change
    /// re-derives the extent the renderer draws with. The content height is the
    /// span from the first row's top to the last row's bottom -- read from the
    /// rects the layout actually emitted, not a re-derived row pitch -- so the
    /// extent tracks whatever mode drew the rows.</summary>
    public void UpdateScrollExtent()
    {
        if (!_layout.AgendaScrollAreaRect.IsEmpty && _layout.RowRects.Count > 0)
        {
            IReadOnlyList<SKRect> rows = _layout.RowRects;
            float contentTop = rows[0].Top;
            float contentBottom = rows[^1].Bottom;
            float totalH = contentBottom - contentTop;
            _maxAgendaScrollY = Math.Max(0f, totalH - _layout.AgendaScrollAreaRect.Height);
            _agendaScrollY = Math.Clamp(_agendaScrollY, 0f, _maxAgendaScrollY);
        }
        else
        {
            _maxAgendaScrollY = 0f;
            _agendaScrollY = 0f;
        }
    }

    /// <summary>Interprets one touch sample. The Down/Move/Up sequence is one
    /// gesture; a release without its contact is not a gesture.</summary>
    public void Feed(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchDown)
        {
            _touchDown = localPoint;
            if (_detailEvent is not null)
            {
                _touchStartDetailScrollY = _detailScrollY;
                _isDraggingDetail = !_detailCardRect.IsEmpty && _detailCardRect.Contains(localPoint.X, localPoint.Y);
                _isDetailScrolled = false;
                return;
            }

            _touchStartScrollY = _agendaScrollY;
            _isDraggingAgenda = !_layout.AgendaScrollAreaRect.IsEmpty && _layout.AgendaScrollAreaRect.Contains(localPoint.X, localPoint.Y);
            _isAgendaScrolled = false;
            return;
        }

        if (eventType == TouchEventType.TouchMove)
        {
            if (_detailEvent is not null)
            {
                if (_isDraggingDetail && _touchDown.HasValue && _maxDetailScrollY > 0f)
                {
                    float moveDy = localPoint.Y - _touchDown.Value.Y;
                    if (Math.Abs(moveDy) > 4f)
                    {
                        _isDetailScrolled = true;
                        _detailScrollY = Math.Clamp(_touchStartDetailScrollY - moveDy, 0f, _maxDetailScrollY);
                        RequestRender();
                    }
                }
                return;
            }

            if (_isDraggingAgenda && _touchDown.HasValue && _maxAgendaScrollY > 0f)
            {
                float moveDy = localPoint.Y - _touchDown.Value.Y;
                if (Math.Abs(moveDy) > 4f)
                {
                    _isAgendaScrolled = true;
                    _agendaScrollY = Math.Clamp(_touchStartScrollY - moveDy, 0f, _maxAgendaScrollY);
                    RequestRender();
                }
            }
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

        if (_detailEvent is not null)
        {
            bool wasDragging = _isDraggingDetail;
            bool wasScrolled = _isDetailScrolled;
            _isDraggingDetail = false;
            _isDetailScrolled = false;

            if (wasDragging && wasScrolled)
            {
                // Drag-to-scroll gesture completed; do not trigger tap actions.
                return;
            }

            // Same tap-vs-drag tolerance as every other calendar gesture (the
            // shared TapDragTolerance), not a separate screen-pixel figure: the
            // detail card lives in the same rotated-local space as the agenda,
            // so its tap window must scale with the rest of the widget.
            if (Math.Abs(dx) <= TapDragTolerance && Math.Abs(dy) <= TapDragTolerance)
            {
                CalendarEvent ev = _detailEvent.Value;

                // Tap on "Tap to go back" header (above the pillbox card)
                if (!_detailCardRect.IsEmpty && localPoint.Y < _detailCardRect.Top)
                {
                    _detailEvent = null;
                    _detailScrollY = 0f;
                    _maxDetailScrollY = 0f;
                    RequestRender();
                    return;
                }

                // Tap on the meeting link: only when the URL rect was recorded by
                // the last render AND the tap lands inside it. If the rect is not
                // set (e.g. a touch arriving before the first detail render), the
                // tap falls through to the exit below instead of opening the link.
                if (!string.IsNullOrWhiteSpace(ev.Url) &&
                    !_detailUrlRect.IsEmpty &&
                    _detailCardRect.Contains(localPoint.X, localPoint.Y) &&
                    _detailUrlRect.Contains(localPoint.X, localPoint.Y))
                {
                    OpenMeetingLink(ev.Url);
                    return;
                }

                // Any other tap exits detail mode back to agenda view
                _detailEvent = null;
                _detailScrollY = 0f;
                _maxDetailScrollY = 0f;
                RequestRender();
            }
            return;
        }

        bool wasDraggingAgenda = _isDraggingAgenda;
        bool wasScrolledAgenda = _isAgendaScrolled;
        _isDraggingAgenda = false;
        _isAgendaScrolled = false;

        if (wasDraggingAgenda && wasScrolledAgenda)
        {
            // Drag-to-scroll gesture completed; do not trigger row selection.
            return;
        }

        // Minimal date card tap: toggle between today and tomorrow.
        if (_layout.Mode == CalendarViewMode.MinimalDateCard1x1)
        {
            if (Math.Abs(dx) <= TapDragTolerance && Math.Abs(dy) <= TapDragTolerance)
            {
                _viewDateOffset = _viewDateOffset == 0 ? 1 : 0;
                RequestRender();
            }
            return;
        }

        // Ignore intentional horizontal swipes/drags so global page navigation operates cleanly.
        if (Math.Abs(dx) > TapDragTolerance || Math.Abs(dy) > TapDragTolerance)
        {
            return;
        }

        // Check for chevron hit (< or >).
        if (CalendarLayout.IsPrevChevronHit(_layout, localPoint.X, localPoint.Y))
        {
            _agendaScrollY = 0f;
            ShiftViewedMonth(-1);
            RequestRender();
            return;
        }

        if (CalendarLayout.IsNextChevronHit(_layout, localPoint.X, localPoint.Y))
        {
            _agendaScrollY = 0f;
            ShiftViewedMonth(1);
            RequestRender();
            return;
        }

        CalendarDisplay? display = _display;
        if (display is null)
            return;

        // Check if the tap landed on a month-grid cell (jump to that day).
        if (TryHitMonthGridCell(localPoint, out int targetDay))
        {
            _agendaScrollY = 0f;
            DateTime viewDt = _now.Date.AddDays(_viewDateOffset);
            if (targetDay >= 1 && targetDay <= DateTime.DaysInMonth(viewDt.Year, viewDt.Month))
            {
                DateTime targetDate = new(viewDt.Year, viewDt.Month, targetDay, 0, 0, 0, DateTimeKind.Unspecified);
                _viewDateOffset = (targetDate.Date - _now.Date).Days;
                _viewDateOffset = Math.Clamp(_viewDateOffset, -365, 365);
                RequestRender();
            }
            return;
        }

        // Check hero event tap (in 5x4 layout).
        if (!_layout.AllDayRect.IsEmpty && _layout.AllDayRect.Contains(localPoint.X, localPoint.Y) && display.NextUpcomingEvent != null)
        {
            CalendarEvent? matched = CalendarEventMatcher.Match(_displaySnapshot(), display.NextUpcomingEvent);
            if (matched is not null)
            {
                _detailEvent = matched;
                _detailScrollY = 0f;
                _maxDetailScrollY = 0f;
                _detailCardRect = SKRect.Empty;
                _detailUrlRect = SKRect.Empty;
                RequestRender();
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

        CalendarEvent? foundEvent = CalendarEventMatcher.Match(_displaySnapshot(), selectedRow);
        if (foundEvent is not null)
        {
            _detailEvent = foundEvent;
            _detailScrollY = 0f;
            _maxDetailScrollY = 0f;
            _detailCardRect = SKRect.Empty;
            _detailUrlRect = SKRect.Empty;
            RequestRender();
        }
    }

    /// <summary>Shifts the viewed day by one month, clamped to a year either
    /// way (the chevron taps' one spelling, so the clamp cannot drift between
    /// the two directions).</summary>
    private void ShiftViewedMonth(int deltaMonths)
    {
        DateTime nowDt = _now;
        DateTime currentView = nowDt.Date.AddDays(_viewDateOffset);
        DateTime targetMonth = currentView.AddMonths(deltaMonths);
        _viewDateOffset = (targetMonth.Date - nowDt.Date).Days;
        _viewDateOffset = Math.Clamp(_viewDateOffset, -365, 365);
    }

    /// <summary>Builds the snapshot the row-to-event match runs against: the
    /// event list handed in at render time (the same data the canvas drew), not
    /// a fresh store read. When no events were handed in (a mode without a
    /// display), the match has nothing to resolve and returns null.</summary>
    private CalendarSnapshot? _displaySnapshot()
        => _events.Count > 0 ? new CalendarSnapshot { Events = _events, HasData = true } : null;

    /// <summary>Hit-tests a point against the month grid cells. Returns true
    /// when the point falls within a non-blank cell, with the day number in
    /// <paramref name="day"/>.</summary>
    private bool TryHitMonthGridCell(SKPoint p, out int day)
    {
        day = 0;
        SKRect rect = _layout.MonthGridRect;
        if (rect.IsEmpty || _display?.MonthGrid.Count != 35)
            return false;

        // The cell geometry comes from the layout record (the one source of truth
        // shared with the render path), so a tap resolves against the same cells
        // the canvas drew -- no re-derived weekday-header height or cell pitch. A
        // null or short cell list means the grid was not drawn (a mode without a
        // month grid), so the hit-test is a no-op.
        IReadOnlyList<MonthGridCell>? cells = _layout.MonthGrid.Cells;
        if (cells is null || cells.Count != 35)
            return false;

        for (int i = 0; i < 35; i++)
        {
            MonthGridCell cellGeo = cells[i];
            if (!cellGeo.Rect.Contains(p.X, p.Y))
                continue;

            MonthCell cell = _display.MonthGrid[i];
            if (!cell.IsCurrentMonth || cell.Day <= 0)
                return false;

            day = cell.Day;
            return true;
        }
        return false;
    }

    /// <summary>Opens a meeting link through the shell-open seam, after the
    /// http/https/mailto gate. A blank or disallowed link is a logged no-op; a
    /// spawn failure is logged, never thrown.</summary>
    private void OpenMeetingLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!ShellOpenPolicy.IsAllowedUrl(url))
        {
            ContextLogError($"Calendar: refusing to open a non-http(s)/mailto event link: {TruncateForLog(url)}");
            return;
        }

        Action<string> open = OpenUrlSeam ?? OpenUrlProduction;
        try
        {
            open(url);
        }
        catch (Exception ex)
        {
            ContextLogError("Calendar: unable to open the event link", ex);
        }
    }

    /// <summary>The production shell-open: hands the link to the OS default
    /// handler. Thread-safe (Process.Start is thread-safe); the touch poll runs
    /// off the dispatcher.</summary>
    private static void OpenUrlProduction(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void RequestRender() => Context?.RequestRender();

    private void ContextLogError(string message, Exception? ex = null)
        => Context?.LogError(message, ex);

    private static string TruncateForLog(string value) => value.Length <= 80 ? value : value[..80] + "...";
}
