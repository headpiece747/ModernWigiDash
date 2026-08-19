using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Draws the Weather widget's per-mode scene from a render model: the
/// Detailed hero + metric pills + forecast strip, the Daily/Hourly rows, and
/// the CurrentOnly/Compact heroes. The widget keeps only orchestration —
/// caching the model, drawing the header, dispatching the mode. Every paint
/// is owned here: the card fill/stroke pair behind every pill, row, and
/// column is one shared pair, colors swapped via Paint.Color mutation
/// (hoisted out of the per-card loops).
/// </summary>
internal sealed class WeatherWidgetRenderer : IDisposable
{
    private readonly SKPaint _cardFillPaint = new() { IsAntialias = true };
    private readonly SKPaint _cardStrokePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _metricPaint = new() { IsAntialias = true };
    private readonly SKPaint _dayPaint = new() { IsAntialias = true };
    private readonly SKPaint _iconPaint = new() { IsAntialias = true };
    private readonly SKPaint _descPaint = new() { IsAntialias = true };
    private readonly SKPaint _tempPaint = new() { IsAntialias = true };
    private readonly SKPaint _timePaint = new() { IsAntialias = true };
    private readonly SKPaint _rangePaint = new() { IsAntialias = true };
    private readonly SKPaint _dayIconPaint = new() { IsAntialias = true };
    private readonly SKPaint _heroIconPaint = new() { IsAntialias = true };
    private readonly SKPaint _heroTempPaint = new() { IsAntialias = true };
    private readonly SKPaint _heroDescPaint = new() { IsAntialias = true };

    /// <summary>Releases the hoisted paints' native Skia handles — the owning
    /// widget's DisposeAsync calls this (see the paint-hoisting note there).</summary>
    public void Dispose()
    {
        _cardFillPaint.Dispose();
        _cardStrokePaint.Dispose();
        _metricPaint.Dispose();
        _dayPaint.Dispose();
        _iconPaint.Dispose();
        _descPaint.Dispose();
        _tempPaint.Dispose();
        _timePaint.Dispose();
        _rangePaint.Dispose();
        _dayIconPaint.Dispose();
        _heroIconPaint.Dispose();
        _heroTempPaint.Dispose();
        _heroDescPaint.Dispose();
    }

    /// <summary>One hero text element: the string, its cached font, and the
    /// paint it draws with (colors are mutated per mode at the call sites).</summary>
    private readonly record struct HeroTextElement(string Text, SKFont Font, SKPaint Paint);

    /// <summary>
    /// Draws the hero block shared by Detailed and CurrentOnly: the icon left
    /// of the temp/condition stack, both vertically centered on
    /// <paramref name="midY"/>. The callers own their sizing policies, gap,
    /// and stack spacing — the two modes' constants differ deliberately
    /// (Detailed clamps the gap to 20f·s; CurrentOnly pins 24f·sx), so the
    /// shared part is only the baseline math and the draws.
    /// </summary>
    private static void DrawHeroBlock(
        SKCanvas canvas,
        HeroTextElement icon,
        HeroTextElement temp,
        HeroTextElement desc,
        float blockLeft,
        float rightX,
        float midY,
        float textStackSpacing)
    {
        // Draw Icon perfectly centered vertically beside Temp + Condition
        icon.Font.GetFontMetrics(out var iconMetrics);
        float iconBaseline = midY - (iconMetrics.Ascent + iconMetrics.Descent) / 2f;
        canvas.DrawTextWithFallback(icon.Text, blockLeft, iconBaseline, icon.Font, icon.Paint);

        // Stack Temperature & Condition on right of icon with centered vertical alignment
        temp.Font.GetFontMetrics(out var tempMetrics);
        desc.Font.GetFontMetrics(out var descMetrics);
        float tempH = tempMetrics.Descent - tempMetrics.Ascent;
        float descH = descMetrics.Descent - descMetrics.Ascent;
        float textStackTotalH = tempH + textStackSpacing + descH;
        float textStackTop = midY - textStackTotalH / 2f;
        float tempBaseline = textStackTop - tempMetrics.Ascent;
        float descBaseline = tempBaseline + tempMetrics.Descent + textStackSpacing - descMetrics.Ascent;

        canvas.DrawTextWithFallback(temp.Text, rightX, tempBaseline, temp.Font, temp.Paint);
        canvas.DrawTextWithFallback(desc.Text, rightX, descBaseline, desc.Font, desc.Paint);
    }

    /// <summary>
    /// Measures pill widths (text + padding) in the pill font the draw path
    /// derives from the same WeatherLayout formulas — ONE spelling shared by
    /// the widget's model builder (at the un-shrunk sizes) and this renderer's
    /// shrink re-measure, so the cached widths can never drift from the drawn
    /// pills.
    /// </summary>
    internal static float[] MeasurePillWidths(IReadOnlyList<string> metrics, float fontSize, float pillPadX)
    {
        var metricFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, fontSize);
        var widths = new float[metrics.Count];
        for (int i = 0; i < widths.Length; i++)
        {
            widths[i] = metricFont.MeasureText(metrics[i]) + pillPadX * 2;
        }
        return widths;
    }

    public void RenderDetailed(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, float sx, float sy, WeatherRenderModel model)
    {
        // The current-condition display fact is day/night-aware (the moon
        // flip); the description stays the day-neutral WMO text.
        var (icon, desc) = WeatherPresentation.MapWmoIcon(model.WeatherCode, model.IsDay);
        float s = Math.Min(sx, sy);
        float w = bounds.Width;
        float h = bounds.Height;

        bool hasForecast = model.ShowForecast && model.Daily.Length > 0 && h >= WeatherLayout.StripsMinHeight;
        float forecastH = hasForecast ? WeatherLayout.ForecastStripHeight(sy) : 0f;

        bool hasMetrics = model.Display.Metrics.Count > 0 && h >= WeatherLayout.StripsMinHeight;
        float metricsH = hasMetrics ? WeatherLayout.MetricsStripHeight(sy) : 0f;

        float heroTop = bounds.Top + 4f * sy;
        float heroBottom = bounds.Bottom - forecastH - (hasMetrics ? metricsH + 12f * sy : 0f) - 4f * sy;
        float heroHeight = Math.Max(heroBottom - heroTop, WeatherLayout.DetailedHeroMinHeight);
        float heroMidY = heroTop + heroHeight / 2f;

        // Sizing hero elements proportionally to fit strictly inside heroHeight without overlapping pills below
        float iconSize = WeatherLayout.DetailedHeroIconSize(heroHeight);
        float tempSize = WeatherLayout.DetailedHeroTempSize(heroHeight);
        float descSize = WeatherLayout.DetailedHeroDescSize(heroHeight);

        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, iconSize);
        _heroIconPaint.Color = SKColors.Black;
        float iconW = iconFont.MeasureText(icon);

        string mainTempStr = model.Display.MainTemp;
        var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, tempSize);
        _heroTempPaint.Color = textPrimary;

        var descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, descSize);
        _heroDescPaint.Color = accentColor;

        // Ensure vertical text stack (Temp + Condition) strictly fits inside heroHeight
        tempFont.GetFontMetrics(out var tempMetrics);
        descFont.GetFontMetrics(out var descMetrics);
        float tempH = tempMetrics.Descent - tempMetrics.Ascent;
        float descH = descMetrics.Descent - descMetrics.Ascent;
        float textStackSpacing = 2f * sy;
        float textStackTotalH = tempH + textStackSpacing + descH;

        float fitScale = WeatherLayout.HeroTextStackShrinkScale(textStackTotalH, heroHeight);
        if (fitScale < 1f)
        {
            tempSize *= fitScale;
            descSize *= fitScale;
            // Re-fetch at the final size: GetCachedFont hands out a shared
            // process-wide instance — mutating .Size would corrupt the cache
            // entry for the original (typeface, size) key.
            tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, tempSize);
            descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, descSize);

            tempFont.GetFontMetrics(out tempMetrics);
            descFont.GetFontMetrics(out descMetrics);
            tempH = tempMetrics.Descent - tempMetrics.Ascent;
            descH = descMetrics.Descent - descMetrics.Ascent;
            textStackTotalH = tempH + textStackSpacing + descH;
        }

        float tempW = tempFont.MeasureText(mainTempStr);
        float descW = descFont.MeasureText(desc);

        float rightBlockW = Math.Max(tempW, descW);
        float gap = WeatherLayout.DetailedHeroGap(s);
        float totalBlockW = iconW + gap + rightBlockW;

        // Auto-scale hero block down if container is narrow
        if (totalBlockW > w)
        {
            float scaleFactor = Math.Max(WeatherLayout.HeroBlockNarrowScaleFloor, w / totalBlockW);
            iconSize *= scaleFactor;
            tempSize *= scaleFactor;
            descSize *= scaleFactor;
            gap *= scaleFactor;

            // Re-fetch at the final sizes (see the fit-scale note above).
            iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, iconSize);
            tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, tempSize);
            descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, descSize);

            iconW = iconFont.MeasureText(icon);
            tempW = tempFont.MeasureText(mainTempStr);
            descW = descFont.MeasureText(desc);
            rightBlockW = Math.Max(tempW, descW);
            totalBlockW = iconW + gap + rightBlockW;

            // The scaled fonts change the text stack's vertical metrics — the
            // baseline math below must use the FINAL sizes, or the stack
            // drifts off-center / overflows heroHeight on narrow containers.
            tempFont.GetFontMetrics(out tempMetrics);
            descFont.GetFontMetrics(out descMetrics);
            tempH = tempMetrics.Descent - tempMetrics.Ascent;
            descH = descMetrics.Descent - descMetrics.Ascent;
            textStackTotalH = tempH + textStackSpacing + descH;
        }

        float blockLeft = bounds.MidX - totalBlockW / 2f;
        float rightX = blockLeft + iconW + gap;

        DrawHeroBlock(canvas,
            new HeroTextElement(icon, iconFont, _heroIconPaint),
            new HeroTextElement(mainTempStr, tempFont, _heroTempPaint),
            new HeroTextElement(desc, descFont, _heroDescPaint),
            blockLeft, rightX, heroMidY, textStackSpacing);

        if (hasMetrics)
        {
            float pillY = heroBottom + 4f * sy;
            RenderMetricPills(canvas, new SKRect(bounds.Left, pillY, bounds.Right, pillY + metricsH), textSecondary, s, model);
        }
        RenderForecastStrip(canvas, bounds, hasForecast, forecastH, accentColor, textPrimary, textSecondary, sx, sy, model);
    }

    private void RenderMetricPills(SKCanvas canvas, SKRect stripRect, SKColor textSecondary, float s, WeatherRenderModel model)
    {
        float w = stripRect.Width;
        float pillY = stripRect.Top;
        float pillHeight = stripRect.Height;
        float metricFontSize = WeatherLayout.PillFontSize(s);
        var metricFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, metricFontSize);

        float pillPadX = WeatherLayout.PillPadX(s);
        float pillGap = WeatherLayout.PillGap(s);
        float totalPillsW = 0f;
        float[] metricWidths = model.MetricWidths;
        for (int i = 0; i < metricWidths.Length; i++)
        {
            totalPillsW += metricWidths[i];
        }
        totalPillsW += (model.Display.Metrics.Count - 1) * pillGap;

        // If pills exceed bounds width, scale down metric font size to fit inside card
        float metricScale = WeatherLayout.MetricPillShrinkScale(totalPillsW, w);
        if (metricScale < 1f)
        {
            metricFontSize = Math.Max(WeatherLayout.MetricPillFontFloor, metricFontSize * metricScale);
            // Re-fetch at the final size (see the fit-scale note above) —
            // mutating the shared cached font corrupts its cache entry.
            metricFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, metricFontSize);
            pillPadX *= metricScale;
            pillGap *= metricScale;

            // One measurement spelling: the shared pill-width helper the
            // widget's model builder uses (at the un-shrunk sizes).
            metricWidths = MeasurePillWidths(model.Display.Metrics, metricFontSize, pillPadX);

            totalPillsW = 0f;
            for (int i = 0; i < metricWidths.Length; i++)
            {
                totalPillsW += metricWidths[i];
            }
            totalPillsW += (model.Display.Metrics.Count - 1) * pillGap;
        }

        _cardStrokePaint.Color = new SKColor(255, 255, 255, 22);
        _cardStrokePaint.StrokeWidth = Math.Max(1f * s, 1f);
        _metricPaint.Color = textSecondary;

        metricFont.GetFontMetrics(out var mMetrics);
        float mBaseline = pillY + pillHeight / 2f - (mMetrics.Ascent + mMetrics.Descent) / 2f;

        float pillStartX = stripRect.MidX - totalPillsW / 2f;
        for (int i = 0; i < model.Display.Metrics.Count; i++)
        {
            SKRect pillRect = new(pillStartX, pillY, pillStartX + metricWidths[i], pillY + pillHeight);
            canvas.DrawRoundRect(pillRect, 8f * s, 8f * s, _cardStrokePaint);
            canvas.DrawTextWithFallback(model.Display.Metrics[i], pillRect.MidX, mBaseline, metricFont, _metricPaint, SKTextAlign.Center);
            pillStartX += metricWidths[i] + pillGap;
        }
    }

    private void RenderForecastStrip(SKCanvas canvas, SKRect bounds, bool hasForecast, float forecastH, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, float sx, float sy, WeatherRenderModel model)
    {
        if (!hasForecast) return;

        float s = Math.Min(sx, sy);
        float w = bounds.Width;
        int count = Math.Min(model.Daily.Length, WeatherForecastLimits.MaxStripDays);
        float stripY = bounds.Bottom - forecastH;
        SKRect stripBounds = new(bounds.Left, stripY, bounds.Right, bounds.Bottom);

        _cardStrokePaint.Color = new SKColor(255, 255, 255, 18);
        _cardStrokePaint.StrokeWidth = Math.Max(1f * s, 1f);
        canvas.DrawRoundRect(stripBounds, 12f * s, 12f * s, _cardStrokePaint);

        float colWidth = w / count;
        float dayFontSize = WeatherLayout.ForecastDayFontSize(s);
        float dayIconFontSize = WeatherLayout.ForecastDayIconFontSize(s);
        float rangeFontSize = WeatherLayout.ForecastRangeFontSize(s);

        var dayFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, dayFontSize);
        var rangeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, rangeFontSize);
        var dayIconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Normal, dayIconFontSize);

        _rangePaint.Color = textSecondary;
        _dayIconPaint.Color = SKColors.Black;

        for (int i = 0; i < count; i++)
        {
            var day = model.Daily[i];
            var (dayIcon, _) = WeatherPresentation.MapWmoCode(day.WeatherCode);
            float colCx = bounds.Left + (i + 0.5f) * colWidth;

            _dayPaint.Color = i == 0 ? accentColor : textPrimary;
            float dayY = stripY + WeatherLayout.ForecastDayTopOffset(s);

            dayFont.MeasureText(day.DayName, out var dayBounds);
            float dayX = colCx - (dayBounds.Left + dayBounds.Width / 2f);
            canvas.DrawTextWithFallback(day.DayName, dayX, dayY, dayFont, _dayPaint);

            string rangeStr = model.Display.ForecastRanges[i];
            float rangeY = stripBounds.Bottom - WeatherLayout.ForecastRangeBottomInset(s);

            rangeFont.MeasureText(rangeStr, out var rangeBounds);
            float rangeX = colCx - (rangeBounds.Left + rangeBounds.Width / 2f);
            canvas.DrawTextWithFallback(rangeStr, rangeX, rangeY, rangeFont, _rangePaint);

            // Calculate exact vertical center between Day Name and Temp Range baselines
            dayFont.GetFontMetrics(out var dayMetrics);
            rangeFont.GetFontMetrics(out var rangeMetrics);
            dayIconFont.GetFontMetrics(out var dayIconMetrics);

            float dayBottomY = dayY + dayMetrics.Descent;
            float rangeTopY = rangeY + rangeMetrics.Ascent;
            float midGapY = (dayBottomY + rangeTopY) / 2f;
            float dayIconBaseline = midGapY - (dayIconMetrics.Ascent + dayIconMetrics.Descent) / 2f;

            // Exact visual bounding box horizontal centering for emoji icon
            dayIconFont.MeasureText(dayIcon, out var iconRect);
            float iconVisualCenterX = iconRect.Left + (iconRect.Width / 2f);
            float iconX = colCx - iconVisualCenterX;

            canvas.DrawTextWithFallback(dayIcon, iconX, dayIconBaseline, dayIconFont, _dayIconPaint);
        }
    }

    public void RenderDailyForecast(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, float sx, float sy, WeatherRenderModel model)
    {
        int count = Math.Min(model.Daily.Length, WeatherForecastLimits.MaxStripDays);
        if (count == 0) return;

        float rowHeight = bounds.Height / count;
        float s = Math.Min(sx, sy);

        _cardFillPaint.Color = new SKColor(22, 26, 40, 180);
        _cardStrokePaint.Color = new SKColor(255, 255, 255, 15);
        _cardStrokePaint.StrokeWidth = 1f;
        _descPaint.Color = textSecondary;
        _tempPaint.Color = accentColor;
        _iconPaint.Color = SKColors.Black;

        var dayFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, WeatherLayout.DailyDayFontSize(s));
        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Normal, WeatherLayout.DailyIconFontSize(s));
        var descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, WeatherLayout.DailyDescFontSize(s));
        var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, WeatherLayout.DailyTempFontSize(s));

        for (int i = 0; i < count; i++)
        {
            var day = model.Daily[i];
            float y = bounds.Top + (i * rowHeight);
            SKRect rowRect = new(bounds.Left, y + 2, bounds.Right, y + rowHeight - 2);

            canvas.DrawRoundRect(rowRect, 8f * s, 8f * s, _cardFillPaint);
            canvas.DrawRoundRect(rowRect, 8f * s, 8f * s, _cardStrokePaint);

            var (icon, desc) = WeatherPresentation.MapWmoCode(day.WeatherCode);

            _dayPaint.Color = i == 0 ? accentColor : textPrimary;
            canvas.DrawTextWithFallback(day.DayName, rowRect.Left + 12f * sx, rowRect.MidY + 5f * sy, dayFont, _dayPaint);

            canvas.DrawTextWithFallback(icon, rowRect.Left + 80f * sx, rowRect.MidY + 6f * sy, iconFont, _iconPaint);

            canvas.DrawTextWithFallback(desc, rowRect.Left + 110f * sx, rowRect.MidY + 4f * sy, descFont, _descPaint);

            string highLowStr = model.Display.DailyHighLows[i];
            canvas.DrawTextWithFallback(highLowStr, rowRect.Right - FontHelper.MeasureTextWithFallback(highLowStr, tempFont) - 12f * sx, rowRect.MidY + 4f * sy, tempFont, _tempPaint);
        }
    }

    public void RenderHourlyForecast(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textSecondary, float sx, float sy, WeatherRenderModel model)
    {
        int count = Math.Min(model.Hourly.Length, WeatherForecastLimits.MaxStripHours);
        if (count == 0) return;

        float itemWidth = bounds.Width / count;
        float s = Math.Min(sx, sy);

        _cardFillPaint.Color = new SKColor(22, 26, 40, 180);
        _cardStrokePaint.Color = new SKColor(255, 255, 255, 15);
        _cardStrokePaint.StrokeWidth = 1f;
        _timePaint.Color = textSecondary;
        _tempPaint.Color = accentColor;
        _iconPaint.Color = SKColors.Black;

        var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, WeatherLayout.HourlyTimeFontSize(s));
        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Normal, WeatherLayout.HourlyIconFontSize(s));
        var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, WeatherLayout.HourlyTempFontSize(s));

        for (int i = 0; i < count; i++)
        {
            var item = model.Hourly[i];
            float x = bounds.Left + (i * itemWidth);
            SKRect colRect = new(x + 2, bounds.Top + 4, x + itemWidth - 2, bounds.Bottom - 4);

            canvas.DrawRoundRect(colRect, 8f * s, 8f * s, _cardFillPaint);
            canvas.DrawRoundRect(colRect, 8f * s, 8f * s, _cardStrokePaint);

            var (icon, _) = WeatherPresentation.MapWmoCode(item.WeatherCode);

            canvas.DrawTextWithFallback(item.TimeLabel, colRect.MidX - (FontHelper.MeasureTextWithFallback(item.TimeLabel, timeFont) / 2f), colRect.Top + 22f * sy, timeFont, _timePaint);

            canvas.DrawTextWithFallback(icon, colRect.MidX - 12f * sx, colRect.MidY + 6f * sy, iconFont, _iconPaint);

            string tempStr = model.Display.HourlyTemps[i];
            canvas.DrawTextWithFallback(tempStr, colRect.MidX - (FontHelper.MeasureTextWithFallback(tempStr, tempFont) / 2f), colRect.Bottom - 14f * sy, tempFont, _tempPaint);
        }
    }

    public void RenderCurrentOnly(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, float sx, float sy, WeatherRenderModel model)
    {
        // The current-condition display fact is day/night-aware (the moon
        // flip); the description stays the day-neutral WMO text.
        var (icon, desc) = WeatherPresentation.MapWmoIcon(model.WeatherCode, model.IsDay);
        float s = Math.Min(sx, sy);
        float midY = bounds.MidY;
        float midX = bounds.MidX;

        float iconSize = WeatherLayout.CurrentOnlyIconSize(s);
        float tempSize = WeatherLayout.CurrentOnlyTempSize(s);
        float descSize = WeatherLayout.CurrentOnlyDescSize(s);

        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, iconSize);
        _heroIconPaint.Color = SKColors.Black;
        float iconW = iconFont.MeasureText(icon);

        string mainTempStr = model.Display.MainTemp;
        var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, tempSize);
        _heroTempPaint.Color = textPrimary;
        float tempW = tempFont.MeasureText(mainTempStr);

        var descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, descSize);
        _heroDescPaint.Color = accentColor;
        float descW = descFont.MeasureText(desc);

        float rightBlockW = Math.Max(tempW, descW);
        float gap = 24f * sx;
        float totalBlockW = iconW + gap + rightBlockW;
        float blockLeft = midX - totalBlockW / 2f;
        float rightX = blockLeft + iconW + gap;

        DrawHeroBlock(canvas,
            new HeroTextElement(icon, iconFont, _heroIconPaint),
            new HeroTextElement(mainTempStr, tempFont, _heroTempPaint),
            new HeroTextElement(desc, descFont, _heroDescPaint),
            blockLeft, rightX, midY, 6f * sy);
    }

    public void RenderCompact(SKCanvas canvas, SKRect bounds, SKColor textPrimary, float sx, float sy, WeatherRenderModel model)
    {
        var (icon, _) = WeatherPresentation.MapWmoIcon(model.WeatherCode, model.IsDay);
        float s = Math.Min(sx, sy);

        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, WeatherLayout.CompactIconFontSize(s));
        _heroIconPaint.Color = SKColors.Black;
        canvas.DrawTextWithFallback(icon, bounds.Left, bounds.MidY + 10f * sy, iconFont, _heroIconPaint);

        string mainTempStr = model.Display.MainTemp;
        var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, WeatherLayout.CompactTempFontSize(s));
        _heroTempPaint.Color = textPrimary;
        canvas.DrawTextWithFallback(mainTempStr, bounds.Left + 36f * sx, bounds.MidY + 8f * sy, tempFont, _heroTempPaint);
    }
}
