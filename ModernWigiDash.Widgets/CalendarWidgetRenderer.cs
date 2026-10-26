namespace ModernWigiDash.Widgets;

/// <summary>
/// Draws the Calendar widget's per-mode scene from a display model + geometry:
/// the adaptive views (minimal date card, full editorial 5x4, split banner 4x2,
/// compact poster), the classic fallback view, and the single-event detail view.
/// The widget keeps only orchestration - reading the snapshot store, building
/// the frame's facts, dispatching the mode, and routing touch. Every paint is
/// owned here: the fill/stroke/text/card pair behind every cell, row, and panel
/// is one shared set, colors swapped via Paint.Color mutation (hoisted out of
/// the per-cell loops). Colors are resolved by the widget before the call so
/// this module never parses a hex string (the house renderer pattern). The
/// date-keyed strings (the minimal card's year/month/day/weekday, the poster
/// header's "/year") are memoized on the viewed date, and the day numbers /
/// week badges are finite-domain statics, so a steady-state tick reformats
/// nothing.
/// </summary>
internal sealed class CalendarWidgetRenderer : IDisposable
{
    private readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _strokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _cardPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    private static readonly string[] StatLabels = ["12M", "52W", "365D"];
    private static readonly string[] WeekdayLabels = ["M", "T", "W", "T", "F", "S", "S"];

    // The day numbers and ISO week badges are finite domains: the strings for
    // 1..31 and W01..W53 never change, so they are built once, not formatted
    // per tick.
    private static readonly string[] DayNumbers = BuildDayNumbers();
    private static readonly string[] WeekBadges = BuildWeekBadges();

    // The date-keyed single-slot memo (the ClockCanvas.DigitalStrings house
    // model): the minimal card's year/month/day/weekday strings and the
    // poster header's "/year" change once a day (the viewed date), so the
    // 30 FPS tick reformats once a day, not once per frame. The shared memo
    // slot owns the key-compare.
    private readonly MemoSlot<DateTime, CalendarDateStrings> _dateMemo = new();

    /// <summary>The date-string memo value: the five formatted date strings.</summary>
    private readonly record struct CalendarDateStrings(string Year, string Month, string Day, string DOW, string SlashYear);

    private CalendarDateStrings DateStrings(DateTime viewDate)
    {
        var key = viewDate.Date;
        if (_dateMemo.TryGet(key, out var value))
        {
            return value;
        }

        value = new CalendarDateStrings(
            viewDate.Year.ToString(CultureInfo.InvariantCulture),
            viewDate.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant(),
            viewDate.Day.ToString(CultureInfo.InvariantCulture),
            viewDate.ToString("dddd", CultureInfo.InvariantCulture).ToUpperInvariant(),
            $"/{viewDate.Year}");
        _dateMemo.Set(key, value);
        return value;
    }

    private static string[] BuildDayNumbers()
    {
        var numbers = new string[32];
        for (int d = 1; d <= 31; d++)
        {
            numbers[d] = d.ToString(CultureInfo.InvariantCulture);
        }
        return numbers;
    }

    private static string[] BuildWeekBadges()
    {
        var badges = new string[54];
        for (int w = 1; w <= 53; w++)
        {
            badges[w] = $"W{w:D2}";
        }
        return badges;
    }

    // The detail view's line pitches and pads used to live here, shared by this
    // file's measure and draw passes. They moved to CalendarLayout with the
    // geometry they describe: the measure pass still runs here (it needs the
    // fonts), but the arithmetic it feeds is the layout's, and the draw pass
    // reads the layout's record, so a pitch can no longer be changed in one place
    // and not the other (2026-09-27).

    /// <summary>The word-wrap cache for the detail view's title/location/
    /// description blocks (memoized so the 30 FPS tick path allocates nothing).</summary>
    private readonly WrapCache _wrapCache = new(16);

    /// <summary>Releases the hoisted paints' native Skia handles - the owning
    /// widget's DisposeAsync calls this (see the paint-hoisting note there).</summary>
    public void Dispose()
    {
        _fillPaint.Dispose();
        _strokePaint.Dispose();
        _textPaint.Dispose();
        _cardPaint.Dispose();
    }

    /// <summary>The minimal 1x1 date card: a color box with year, month, day,
    /// and weekday stacked vertically. Font sizes are derived from the rect
    /// dimensions so the text fills the available space at any widget size.</summary>
    public void RenderMinimalDateCard(SKCanvas canvas, SKRect rect, CalendarSeasonalPalette palette, float scale, DateTime viewDate)
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
        float h = rect.Height;
        float w = rect.Width;

        (string yearStr, string monthStr, string dayStr, string dowStr, _) = DateStrings(viewDate);

        // Year at top - sized to fill width
        var yearFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Min(h * 0.12f, w * 0.25f));
        _textPaint.Color = palette.Text.WithAlpha(170);
        float yearW = FontHelper.MeasureTextWithFallback(yearStr, yearFont);
        canvas.DrawTextWithFallback(yearStr, midX - yearW / 2f, rect.Top + h * 0.15f, yearFont, _textPaint);

        // Month name - sized to fill width
        var monthFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Min(h * 0.15f, w * 0.35f));
        _textPaint.Color = palette.Text;
        float monthW = FontHelper.MeasureTextWithFallback(monthStr, monthFont);
        canvas.DrawTextWithFallback(monthStr, midX - monthW / 2f, rect.Top + h * 0.35f, monthFont, _textPaint);

        // Day number - largest element, fills most of the space
        var dayFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Min(h * 0.45f, w * 0.6f));
        _textPaint.Color = palette.Text;
        float dayW = FontHelper.MeasureTextWithFallback(dayStr, dayFont);
        canvas.DrawTextWithFallback(dayStr, midX - dayW / 2f, rect.Top + h * 0.65f, dayFont, _textPaint);

        // Weekday at bottom - sized to fill width
        var dowFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Min(h * 0.1f, w * 0.3f));
        _textPaint.Color = palette.Text.WithAlpha(180);
        float dowW = FontHelper.MeasureTextWithFallback(dowStr, dowFont);
        canvas.DrawTextWithFallback(dowStr, midX - dowW / 2f, rect.Bottom - h * 0.12f, dowFont, _textPaint);
    }

    /// <summary>The adaptive view dispatcher: draws the dark container then
    /// routes to the mode-specific panels based on the computed layout.</summary>
    public void RenderAdaptiveView(SKCanvas canvas, SKRect bounds, CalendarGeometry layout, CalendarDisplay display, CalendarSeasonalPalette palette, float scale, DateTime now, DateTime viewDate, float agendaScrollY, float maxAgendaScrollY)
    {
        // Dark outer container canvas - sharp rect fills 1016x592 physical LCD edge-to-edge
        _cardPaint.Color = new SKColor(12, 13, 18);
        canvas.DrawRect(bounds, _cardPaint);

        if (layout.Mode == CalendarViewMode.MinimalDateCard1x1)
        {
            RenderMinimalDateCard(canvas, layout.MonthCardRect.IsEmpty ? bounds : layout.MonthCardRect, palette, scale, viewDate);
            return;
        }

        if (layout.Mode == CalendarViewMode.FullEditorial5x4)
        {
            DrawLeftEditorialStrip(canvas, layout.LeftStripRect, palette, scale, viewDate);
            DrawPosterMonthCard(canvas, layout, display, palette, scale, viewDate);
            DrawAgendaPanel(canvas, layout, display, palette, scale, now, viewDate, agendaScrollY, maxAgendaScrollY);
        }
        else if (layout.Mode == CalendarViewMode.SplitBanner4x2)
        {
            DrawPosterMonthCard(canvas, layout, display, palette, scale, viewDate);
            DrawAgendaPanel(canvas, layout, display, palette, scale, now, viewDate, agendaScrollY, maxAgendaScrollY);
        }
        else
        {
            DrawCompactPoster(canvas, layout, display, palette, scale, viewDate);
        }
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

        // Week badge at top - use original 5.5f font size for consistency
        int weekNum = System.Globalization.ISOWeek.GetWeekOfYear(viewDate);
        var weekFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 5.5f * scale);
        _textPaint.Color = palette.Accent;
        string weekStr = WeekBadges[weekNum];
        float ww = FontHelper.MeasureTextWithFallback(weekStr, weekFont);
        canvas.DrawTextWithFallback(weekStr, rect.MidX - ww / 2f, rect.Top + 16f * scale, weekFont, _textPaint);

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

        // Stacked indicators at bottom: 12M / 52W / 365D - tightly stacked, almost touching
        var statFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 5f * scale);
        _textPaint.Color = SKColors.White.WithAlpha(140);
        float statY = rect.Bottom - 8f * scale;
        for (int i = StatLabels.Length - 1; i >= 0; i--)
        {
            string label = StatLabels[i];
            float sw = FontHelper.MeasureTextWithFallback(label, statFont);
            canvas.DrawTextWithFallback(label, rect.MidX - sw / 2f, statY, statFont, _textPaint);
            statY -= 7f * scale;
        }
    }

    private void DrawPosterMonthCard(SKCanvas canvas, CalendarGeometry layout, CalendarDisplay display, CalendarSeasonalPalette palette, float scale, DateTime viewDate)
    {
        SKRect rect = layout.MonthCardRect;
        if (rect.IsEmpty)
            return;

        // Poster month card background with seasonal color
        _cardPaint.Color = palette.Background;
        canvas.DrawRoundRect(rect, 14f * scale, 14f * scale, _cardPaint);

        // Header
        SKRect headerRect = layout.HeaderRect;
        if (!headerRect.IsEmpty)
        {
            (_, _, _, _, string slashYear) = DateStrings(viewDate);
            var yearFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f * scale);
            _textPaint.Color = palette.Text.WithAlpha(170);
            canvas.DrawTextWithFallback(slashYear, headerRect.Left + 2f * scale, headerRect.Top + 13f * scale, yearFont, _textPaint);

            var monthFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 26f * scale);
            _textPaint.Color = palette.Text;
            canvas.DrawTextWithFallback(palette.MonthCode, headerRect.Left + 2f * scale, headerRect.Top + 36f * scale, monthFont, _textPaint);

            DrawChevron(canvas, layout.PrevChevronRect, "<", palette.Text, scale);
            DrawChevron(canvas, layout.NextChevronRect, ">", palette.Text, scale);
        }

        // Weekday header & Month grid -- the cell geometry comes from the layout
        // record (the one source of truth shared with the touch path), so the
        // drawn cells and the hit-test cells can never drift apart.
        SKRect gridRect = layout.MonthGridRect;
        IReadOnlyList<MonthGridCell>? cells = layout.MonthGrid.Cells;
        if (!gridRect.IsEmpty && display.MonthGrid.Count == 35 && cells is not null && cells.Count == 35)
        {
            var wdFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 9f * scale);
            _textPaint.Color = palette.Text.WithAlpha(160);
            for (int c = 0; c < 7; c++)
            {
                SKPoint center = cells[c].Center;
                float tw = FontHelper.MeasureTextWithFallback(WeekdayLabels[c], wdFont);
                canvas.DrawTextWithFallback(WeekdayLabels[c], center.X - tw / 2f, gridRect.Top + 10f * scale, wdFont, _textPaint);
            }

            var dayFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 10f * scale);

            for (int i = 0; i < 35; i++)
            {
                MonthGridCell cellGeo = cells[i];
                float cx = cellGeo.Center.X;
                float cy = cellGeo.Center.Y;
                float cellH = cellGeo.Rect.Height;
                MonthCell cell = display.MonthGrid[i];

                if (!cell.IsCurrentMonth)
                {
                    // Overflow days from adjacent months rendered at muted contrast
                    string numStr = DayNumbers[cell.Day];
                    _textPaint.Color = palette.Text.WithAlpha(55);
                    float nw = FontHelper.MeasureTextWithFallback(numStr, dayFont);
                    canvas.DrawTextWithFallback(numStr, cx - nw / 2f, cy - dayFont.Metrics.Top * 0.4f, dayFont, _textPaint);
                    continue;
                }

                if (cell.IsToday)
                {
                    _fillPaint.Color = palette.Accent;
                    canvas.DrawCircle(cx, cy, Math.Min(cellGeo.Rect.Width, cellH) * 0.42f, _fillPaint);
                    _textPaint.Color = (palette.Accent.Red * 0.299 + palette.Accent.Green * 0.587 + palette.Accent.Blue * 0.114) > 160 ? new SKColor(20, 20, 25) : SKColors.White;
                }
                else if (cell.IsViewed)
                {
                    _strokePaint.Color = palette.Accent;
                    _strokePaint.StrokeWidth = 1.5f * scale;
                    canvas.DrawCircle(cx, cy, Math.Min(cellGeo.Rect.Width, cellH) * 0.40f, _strokePaint);
                    _textPaint.Color = palette.Text;
                }
                else if (cell.HasEvents)
                {
                    _fillPaint.Color = palette.Text.WithAlpha(35);
                    canvas.DrawCircle(cx, cy, Math.Min(cellGeo.Rect.Width, cellH) * 0.38f, _fillPaint);
                    _textPaint.Color = palette.Text;
                }
                else
                {
                    _textPaint.Color = palette.Text.WithAlpha(210);
                }

                string curNumStr = DayNumbers[cell.Day];
                float curNw = FontHelper.MeasureTextWithFallback(curNumStr, dayFont);
                canvas.DrawTextWithFallback(curNumStr, cx - curNw / 2f, cy - dayFont.Metrics.Top * 0.4f, dayFont, _textPaint);

                if (cell.HasEvents)
                {
                    _fillPaint.Color = cell.IsToday ? _textPaint.Color : palette.Accent;
                    canvas.DrawCircle(cellGeo.DotCenter.X, cellGeo.DotCenter.Y, 1.5f * scale, _fillPaint);
                }
                else if (cell.IsNotable)
                {
                    _fillPaint.Color = palette.Text.WithAlpha(140);
                    canvas.DrawCircle(cellGeo.DotCenter.X, cellGeo.DotCenter.Y, 1.2f * scale, _fillPaint);
                }
            }
        }
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

    private void DrawAgendaPanel(SKCanvas canvas, CalendarGeometry layout, CalendarDisplay display, CalendarSeasonalPalette palette, float scale, DateTime now, DateTime viewDate, float agendaScrollY, float maxAgendaScrollY)
    {
        SKRect rect = layout.AgendaRect;
        if (rect.IsEmpty)
            return;

        _cardPaint.Color = new SKColor(18, 20, 28);
        canvas.DrawRoundRect(rect, 14f * scale, 14f * scale, _cardPaint);

        _strokePaint.Color = SKColors.White.WithAlpha(15);
        _strokePaint.StrokeWidth = 1f * scale;
        canvas.DrawRoundRect(rect, 14f * scale, 14f * scale, _strokePaint);

        // Header. The "is today" verdict reads the frame's clock read (now), not
        // the machine wall clock (DateTime.Today): a render tick that crosses a
        // midnight boundary mid-frame would otherwise disagree with the rest of
        // the frame, and a test driving a fixed clock could not pin the header.
        var hFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f * scale);
        _textPaint.Color = new SKColor(120, 160, 255);
        string agendaHeader = viewDate.Date == now.Date ? "AGENDA" : "UPCOMING AGENDA";
        canvas.DrawTextWithFallback(agendaHeader, rect.Left + 14f * scale, rect.Top + 16f * scale, hFont, _textPaint);

        var dFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 13f * scale);
        _textPaint.Color = SKColors.White;
        canvas.DrawTextWithFallback(display.DateHeaderText, rect.Left + 14f * scale, rect.Top + 33f * scale, dFont, _textPaint);

        // Hero Card (Next Event)
        SKRect heroRect = layout.AllDayRect;
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
                string sub = string.IsNullOrEmpty(nextEv.FeedLabel) ? nextEv.TimeText : $"{nextEv.TimeText} \u00B7 {nextEv.FeedLabel}";
                canvas.DrawTextWithFallback(sub, heroRect.Left + 12f * scale, heroRect.Top + 29f * scale, subFont, _textPaint);
            }
            else
            {
                _fillPaint.Color = new SKColor(24, 26, 36);
                canvas.DrawRoundRect(heroRect, 8f * scale, 8f * scale, _fillPaint);

                // Auto-scale hint text to fit within the hero card bounds
                string hint = display.HasData ? "No upcoming events scheduled" : display.StalenessHint;
                float maxWidth = heroRect.Width - 24f * scale;
                float maxHeight = heroRect.Height - 8f * scale;

                var testFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 11f * scale);
                var tb = new SKRect();
                testFont.MeasureText(hint, out tb, _textPaint);

                float fontSize = 11f * scale;
                if (tb.Width > maxWidth || tb.Height > maxHeight)
                {
                    float scaleW = maxWidth / tb.Width;
                    float scaleH = maxHeight / tb.Height;
                    fontSize *= Math.Min(scaleW, scaleH);
                }

                var emptyFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, fontSize);
                _textPaint.Color = SKColors.White.WithAlpha(120);
                emptyFont.MeasureText(hint, out tb, _textPaint);
                canvas.DrawTextWithFallback(hint, heroRect.MidX - tb.Width / 2f, heroRect.MidY - tb.Height / 2f, emptyFont, _textPaint);
            }
        }

        SKRect scrollArea = layout.AgendaScrollAreaRect;
        if (!scrollArea.IsEmpty)
        {
            canvas.Save();
            canvas.ClipRect(scrollArea);
            DrawRows(canvas, layout, display, scale, agendaScrollY);
            canvas.Restore();

            // Vertical scrollbar thumb when events exceed the viewport; the geometry
            // is owned by CalendarLayout so the render path only draws it.
            AgendaScrollGeometry thumb = CalendarLayout.BuildAgendaScrollGeometry(scrollArea, maxAgendaScrollY, agendaScrollY, scale);
            if (thumb.Visible)
            {
                _fillPaint.Color = SKColors.White.WithAlpha(60);
                canvas.DrawRoundRect(new SKRect(thumb.ThumbX, thumb.ThumbY, thumb.ThumbX + thumb.ThumbWidth, thumb.ThumbY + thumb.ThumbHeight), 1.5f * scale, 1.5f * scale, _fillPaint);
            }
        }
        else
        {
            DrawRows(canvas, layout, display, scale, 0f);
        }
    }

    private void DrawCompactPoster(SKCanvas canvas, CalendarGeometry layout, CalendarDisplay display, CalendarSeasonalPalette palette, float scale, DateTime viewDate)
    {
        DrawPosterMonthCard(canvas, layout, display, palette, scale, viewDate);

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

    /// <summary>The classic fallback view: background + header + month grid +
    /// rows, used when the compact poster would be too cramped.</summary>
    public void RenderClassicView(SKCanvas canvas, SKRect bounds, CalendarGeometry layout, CalendarDisplay display, float scale, SKColor textColor, SKColor accentColor)
    {
        DrawBackground(canvas, bounds, scale);
        if (!display.HasData)
        {
            DrawUnavailable(canvas, bounds, scale, display.StalenessHint, textColor);
            return;
        }
        DrawHeader(canvas, layout, display, scale, textColor);
        DrawClassicMonthGrid(canvas, layout, display, scale, textColor, accentColor);
        DrawRows(canvas, layout, display, scale, 0f);
    }

    private void DrawBackground(SKCanvas canvas, SKRect bounds, float scale)
    {
        _fillPaint.Color = new SKColor(18, 18, 24);
        canvas.DrawRoundRect(bounds, 16f * scale, 16f * scale, _fillPaint);
    }

    private void DrawUnavailable(SKCanvas canvas, SKRect bounds, float scale, string hint, SKColor textColor)
    {
        // Scale font size to fit the hint text within the bounds
        float maxWidth = bounds.Width * 0.9f;
        float maxHeight = bounds.Height * 0.5f;

        var testFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 20f * scale);
        var tb = new SKRect();
        testFont.MeasureText(hint, out tb, _textPaint);

        float fontSize = 20f * scale;
        if (tb.Width > maxWidth || tb.Height > maxHeight)
        {
            float scaleW = maxWidth / tb.Width;
            float scaleH = maxHeight / tb.Height;
            fontSize *= Math.Min(scaleW, scaleH);
        }

        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, fontSize);
        _textPaint.Color = textColor.WithAlpha(160);
        font.MeasureText(hint, out tb, _textPaint);
        canvas.DrawTextWithFallback(hint, bounds.MidX - tb.Width / 2f, bounds.MidY - tb.Height / 2f, font, _textPaint);
    }

    private void DrawHeader(SKCanvas canvas, CalendarGeometry layout, CalendarDisplay display, float scale, SKColor textColor)
    {
        SKRect rect = layout.HeaderRect;
        if (rect.IsEmpty || string.IsNullOrEmpty(display.DateHeaderText))
            return;

        var monthFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 13f * scale);
        _textPaint.Color = textColor.WithAlpha(180);
        canvas.DrawTextWithFallback(display.MonthTitle, rect.Left + 2f * scale, rect.Top + 2f * scale - monthFont.Metrics.Top, monthFont, _textPaint);

        var dateFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 18f * scale);
        _textPaint.Color = textColor;
        canvas.DrawTextWithFallback(display.DateHeaderText, rect.Left + 2f * scale, rect.Bottom - 8f * scale - dateFont.Metrics.Top, dateFont, _textPaint);
    }

    private void DrawClassicMonthGrid(SKCanvas canvas, CalendarGeometry layout, CalendarDisplay display, float scale, SKColor textColor, SKColor accent)
    {
        SKRect rect = layout.MonthGridRect;
        if (rect.IsEmpty || display.MonthGrid.Count != 35)
            return;

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
                numColor = textColor.WithAlpha(180);

            _textPaint.Color = numColor;
            string dayStr = DayNumbers[cell.Day];
            float dayW = FontHelper.MeasureTextWithFallback(dayStr, font);
            canvas.DrawTextWithFallback(dayStr, cx - dayW / 2f, cy - font.Metrics.Top * 0.5f, font, _textPaint);

            if (cell.HasEvents)
            {
                _fillPaint.Color = cell.IsViewed ? SKColors.White : new SKColor(245, 158, 11);
                canvas.DrawCircle(cx, cy + cellH * 0.3f, 1.5f * scale, _fillPaint);
            }
        }
    }

    private void DrawRows(SKCanvas canvas, CalendarGeometry layout, CalendarDisplay display, float scale, float scrollY)
    {
        SKColor text = SKColors.White;
        float gutterW = layout.TimeGutterWidth;

        for (int i = 0; i < display.Rows.Count && i < layout.RowRects.Count; i++)
        {
            SKRect baseRect = layout.RowRects[i];
            if (baseRect.IsEmpty)
                continue;

            SKRect rect = Math.Abs(scrollY) > 0.001f
                ? new SKRect(baseRect.Left, baseRect.Top - scrollY, baseRect.Right, baseRect.Bottom - scrollY)
                : baseRect;

            if (!layout.AgendaScrollAreaRect.IsEmpty &&
                (rect.Bottom < layout.AgendaScrollAreaRect.Top || rect.Top > layout.AgendaScrollAreaRect.Bottom))
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

    /// <summary>Measures the detail frame for one event: the wrapped lines per
    /// section and the geometry they imply. NO CANVAS - which is the point.
    ///
    /// The widget calls this before it draws anything, so the gesture module
    /// holds the real geometry from the first frame in detail mode. It used to
    /// receive the geometry as a by-product of rendering, so a touch arriving
    /// before the first detail draw found no card rect, no link target and no
    /// scroll extent: a drag did nothing and a link tap fell through to "exit
    /// detail" instead of opening the link. Five `IsEmpty` guards stood in for
    /// that missing state; a `default` geometry now means the same thing to
    /// `SKRect.Contains` without the guards (2026-09-27).</summary>
    /// <param name="bounds">The widget's placement bounds.</param>
    /// <param name="scale">The frame's uniform scale factor.</param>
    /// <param name="ev">The event being shown.</param>
    /// <param name="scrollY">The current scroll offset; the layout clamps it.</param>
    public CalendarDetailFrame MeasureDetail(SKRect bounds, float scale, CalendarEvent ev, float scrollY)
    {
        float pad = CalendarLayout.DetailPad * scale;
        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 20f * scale);
        var locFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 15f * scale);
        var descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 14f * scale);
        var urlFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 14f * scale);

        // The wrap width is the text column the layout is about to compute, from
        // the layout's own numbers rather than a second derivation of them.
        float cardLeft = bounds.Left + pad;
        float cardRight = bounds.Right - pad;
        float textX = cardLeft + CalendarLayout.DetailTextInset * scale;
        float maxW = cardRight - textX - CalendarLayout.DetailTextRightInset * scale;

        string title = string.IsNullOrWhiteSpace(ev.Title) ? "Untitled" : ev.Title;
        IReadOnlyList<string> titleLines = _wrapCache.GetOrWrap(title, titleFont, 20f * scale, maxW);
        IReadOnlyList<string>? locLines = !string.IsNullOrWhiteSpace(ev.Location)
            ? _wrapCache.GetOrWrap(ev.Location, locFont, 15f * scale, maxW)
            : null;
        IReadOnlyList<string>? descLines = !string.IsNullOrWhiteSpace(ev.Description)
            ? _wrapCache.GetOrWrap(ev.Description, descFont, 14f * scale, maxW)
            : null;
        IReadOnlyList<string>? urlLines = !string.IsNullOrWhiteSpace(ev.Url)
            ? _wrapCache.GetOrWrap(ev.Url, urlFont, 14f * scale, maxW)
            : null;

        var counts = new DetailLineCounts(
            titleLines.Count,
            locLines?.Count,
            descLines?.Count,
            urlLines?.Count,
            !string.IsNullOrWhiteSpace(ev.FeedLabel),
            !ev.IsAllDay);

        return new CalendarDetailFrame(
            CalendarLayout.ComputeDetail(bounds, scale, counts, scrollY),
            titleLines,
            locLines,
            descLines,
            urlLines,
            counts.HasFeedLabel,
            counts.HasDuration);
    }

    /// <summary>Draws a measured detail frame.
    ///
    /// The geometry is an INPUT, not this method's output. It is handed the same
    /// <see cref="CalendarDetailFrame"/> the widget already gave the gesture
    /// module, so what is drawn and what is hit-tested are the same measurement
    /// rather than two computations that have to agree (2026-09-27).</summary>
    /// <param name="frame">The measured frame (from <see cref="MeasureDetail"/>).</param>
    /// <param name="canvas">The target canvas.</param>
    /// <param name="bounds">The widget's placement bounds.</param>
    /// <param name="scale">The frame's uniform scale factor.</param>
    /// <param name="ev">The event being shown.</param>
    /// <param name="textColor">The theme's text color.</param>
    /// <param name="accentColor">The theme's accent color.</param>
    /// <param name="now">The frame's clock read, for the live marker.</param>
    public void RenderDetailView(
        CalendarDetailFrame frame,
        SKCanvas canvas,
        SKRect bounds,
        float scale,
        CalendarEvent ev,
        SKColor textColor,
        SKColor accentColor,
        DateTime now)
    {
        CalendarDetailGeometry geo = frame.Geometry;

        _cardPaint.Color = new SKColor(18, 18, 24);
        canvas.DrawRoundRect(bounds, 16f * scale, 16f * scale, _cardPaint);

        float pad = CalendarLayout.DetailPad * scale;
        var backFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 13f * scale);
        _textPaint.Color = textColor.WithAlpha(120);
        canvas.DrawTextWithFallback("Tap to go back", bounds.Left + pad, bounds.Top + pad - backFont.Metrics.Top, backFont, _textPaint);

        var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 18f * scale);
        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 20f * scale);
        var feedFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 14f * scale);
        var locFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 15f * scale);
        var descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 14f * scale);
        var durFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 14f * scale);
        var urlFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 14f * scale);

        _cardPaint.Color = accentColor.WithAlpha(20);
        canvas.DrawRoundRect(geo.CardRect, 12f * scale, 12f * scale, _cardPaint);

        bool isLive = ev.Start <= now && now < ev.End;
        _fillPaint.Color = isLive ? new SKColor(239, 68, 68) : accentColor;
        canvas.DrawRoundRect(
            new SKRect(geo.CardRect.Left, geo.CardRect.Top, geo.CardRect.Left + 5f * scale, geo.CardRect.Bottom),
            2.5f * scale, 2.5f * scale, _fillPaint);

        string timeStr = ev.IsAllDay ? "All day" : $"{ev.Start:HH:mm} – {ev.End:HH:mm}";

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(geo.ClipRect, 10f * scale, 10f * scale), antialias: true);

        _textPaint.Color = isLive ? new SKColor(239, 68, 68) : accentColor;
        canvas.DrawTextWithFallback(timeStr, geo.TextX, geo.TimeY, timeFont, _textPaint);

        _textPaint.Color = textColor;
        float y = geo.TitleY;
        foreach (string line in frame.TitleLines)
        {
            canvas.DrawTextWithFallback(line, geo.TextX, y, titleFont, _textPaint);
            y += CalendarLayout.DetailTitleLineH * scale;
        }

        if (frame.HasFeedLabel)
        {
            _textPaint.Color = new SKColor(120, 160, 255);
            canvas.DrawTextWithFallback($"[{ev.FeedLabel}]", geo.TextX, geo.FeedY, feedFont, _textPaint);
        }

        if (frame.LocationLines is not null)
        {
            _textPaint.Color = textColor.WithAlpha(200);
            y = geo.LocationY;
            foreach (string line in frame.LocationLines)
            {
                canvas.DrawTextWithFallback(line, geo.TextX, y, locFont, _textPaint);
                y += CalendarLayout.DetailLocLineH * scale;
            }
        }

        if (frame.DescriptionLines is not null)
        {
            _textPaint.Color = textColor.WithAlpha(180);
            y = geo.DescriptionY;
            foreach (string line in frame.DescriptionLines)
            {
                canvas.DrawTextWithFallback(line, geo.TextX, y, descFont, _textPaint);
                y += CalendarLayout.DetailDescLineH * scale;
            }
        }

        if (frame.HasDuration)
        {
            TimeSpan dur = ev.End - ev.Start;
            string durStr = dur.TotalHours >= 1
                ? $"{(int)dur.TotalHours}h {dur.Minutes:D2}m"
                : $"{dur.Minutes}m";
            _textPaint.Color = textColor.WithAlpha(160);
            canvas.DrawTextWithFallback($"Duration: {durStr}", geo.TextX, geo.DurationY, durFont, _textPaint);
        }

        if (frame.UrlLines is not null)
        {
            _textPaint.Color = new SKColor(120, 160, 255);
            y = geo.UrlY;
            foreach (string chunk in frame.UrlLines)
            {
                canvas.DrawTextWithFallback(chunk, geo.TextX, y, urlFont, _textPaint);
                y += CalendarLayout.DetailUrlLineH * scale;
            }
        }

        canvas.Restore();

        // Scroll indicator inside the pillbox when content overflows
        if (geo.MaxScrollY > 0f)
        {
            float trackTop = geo.CardRect.Top + 10f * scale;
            float trackBottom = geo.CardRect.Bottom - 10f * scale;
            float trackH = trackBottom - trackTop;
            float trackX = geo.CardRect.Right - 6f * scale;

            _strokePaint.Color = textColor.WithAlpha(30);
            _strokePaint.StrokeWidth = 2f * scale;
            canvas.DrawLine(trackX, trackTop, trackX, trackBottom, _strokePaint);

            float thumbH = Math.Clamp(trackH * (geo.CardRect.Height / geo.TotalContentHeight), 20f * scale, trackH);
            float thumbTop = trackTop + (geo.ClampedScrollY / geo.MaxScrollY) * (trackH - thumbH);
            _strokePaint.Color = accentColor.WithAlpha(180);
            _strokePaint.StrokeWidth = 3f * scale;
            _strokePaint.StrokeCap = SKStrokeCap.Round;
            canvas.DrawLine(trackX, thumbTop, trackX, thumbTop + thumbH, _strokePaint);
            _strokePaint.StrokeCap = SKStrokeCap.Butt;
        }
    }
}
