namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets;

/// <summary>
/// The calendar renderer's date-keyed text memo: the minimal card's
/// year/month/day/weekday strings change once a day (the viewed date), so a
/// steady 30 FPS tick on the same date serves the memoized strings instead of
/// rebuilding them. The Skia interop draw path has a fixed per-tick floor
/// (the string-to-native conversions), so the pin is RELATIVE: steady-state
/// ticks (same date) must allocate less than date-changing ticks (the memo
/// rebuilds). A broken memo makes the two converge and the pin fails.
/// </summary>
[TestClass]
public class CalendarWidgetRendererTextTests
{
    private static readonly DateTime ViewDate = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Unspecified);

    // A generous storm-catcher bound for the steady path: the Skia interop
    // floor of a few text draws is well under this, so a per-tick string /
    // collection storm (the pre-memo behavior, a LINQ ToList, a per-frame
    // font) must exceed it.
    private const long SteadyTickStormBudgetBytes = 4096;

    [TestMethod]
    public void RenderMinimalDateCard_SteadyState_AllocatesLessThanChangingDate()
    {
        using var renderer = new CalendarWidgetRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(200, 200))!;
        var palette = CalendarSeasonalPalettes.GetForMonth(9);
        var rect = new SKRect(0, 0, 200, 200);

        // Warm: the font caches fill on the first render.
        renderer.RenderMinimalDateCard(surface.Canvas, rect, palette, 1f, ViewDate);

        // Steady: the same viewed date every tick (the memo serves, no rebuild).
        long steadyBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++)
        {
            renderer.RenderMinimalDateCard(surface.Canvas, rect, palette, 1f, ViewDate);
        }
        long steadyDelta = GC.GetAllocatedBytesForCurrentThread() - steadyBefore;

        // Changing: a different viewed date every tick (the memo rebuilds each time).
        long changeBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++)
        {
            renderer.RenderMinimalDateCard(surface.Canvas, rect, palette, 1f, ViewDate.AddDays(i + 1));
        }
        long changeDelta = GC.GetAllocatedBytesForCurrentThread() - changeBefore;

        Assert.IsTrue(steadyDelta < changeDelta,
            $"steady-state ticks ({steadyDelta} bytes over 5) must allocate less than date-changing ticks ({changeDelta} bytes over 5): the date strings are memoized on the viewed date, so a steady tick must not rebuild them");
    }

    [TestMethod]
    public void RenderMinimalDateCard_SteadyState_AllocatesUnderTheStormBudget()
    {
        using var renderer = new CalendarWidgetRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(200, 200))!;
        var palette = CalendarSeasonalPalettes.GetForMonth(9);
        var rect = new SKRect(0, 0, 200, 200);

        renderer.RenderMinimalDateCard(surface.Canvas, rect, palette, 1f, ViewDate);

        long before = GC.GetAllocatedBytesForCurrentThread();
        renderer.RenderMinimalDateCard(surface.Canvas, rect, palette, 1f, ViewDate);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(delta <= SteadyTickStormBudgetBytes,
            $"a steady-state minimal-card tick allocated {delta} bytes (budget {SteadyTickStormBudgetBytes}): a per-tick string/collection storm has crept into the date-card path");
    }
}
