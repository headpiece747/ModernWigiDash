namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets;

/// <summary>
/// The stopwatch canvas's elapsed string is memoized on the displayed
/// centisecond value (the house single-slot model): a steady-state tick
/// (the same displayed "mm:ss.cc" value) must not reformat it. The Skia
/// interop draw path has a fixed per-tick floor, so the pin is RELATIVE
/// (steady ticks allocate less than changing ticks) plus a storm-catcher
/// bound. A render-smoke test pins the draw owner's surface for C1.
/// </summary>
[TestClass]
public class StopwatchTimerCanvasTests
{
    private const long SteadyTickStormBudgetBytes = 4096;

    [TestMethod]
    public void Render_Smoke_PaintsTheTimeAndStatus()
    {
        using var canvas = new StopwatchTimerCanvas();
        using var bitmap = new SKBitmap(300, 200);
        using var skCanvas = new SKCanvas(bitmap);

        canvas.Render(skCanvas, new SKRect(0, 0, 300, 200), TimeSpan.FromSeconds(65.42), true, SKColors.White, SKColors.Cyan);

        // The time + status text sits in the canvas center; a fully
        // transparent canvas would mean nothing was rendered.
        int painted = 0;
        for (int x = 100; x < 200; x += 5)
        {
            for (int y = 70; y < 130; y += 5)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0) painted++;
            }
        }
        Assert.IsTrue(painted > 0, "the stopwatch time/status must paint onto the canvas");
    }

    [TestMethod]
    public void Render_SteadyState_AllocatesLessThanChangingTotal()
    {
        using var canvas = new StopwatchTimerCanvas();
        using var bitmap = new SKBitmap(300, 200);
        using var skCanvas = new SKCanvas(bitmap);
        var bounds = new SKRect(0, 0, 300, 200);

        // Warm: the font caches fill on the first render.
        canvas.Render(skCanvas, bounds, TimeSpan.FromSeconds(65.42), true, SKColors.White, SKColors.Cyan);

        // Steady: the same displayed total every tick (the memo serves).
        long steadyBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++)
        {
            canvas.Render(skCanvas, bounds, TimeSpan.FromSeconds(65.42), true, SKColors.White, SKColors.Cyan);
        }
        long steadyDelta = GC.GetAllocatedBytesForCurrentThread() - steadyBefore;

        // Changing: a different displayed total every tick (the memo rebuilds).
        long changeBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++)
        {
            canvas.Render(skCanvas, bounds, TimeSpan.FromSeconds(65.42 + i), true, SKColors.White, SKColors.Cyan);
        }
        long changeDelta = GC.GetAllocatedBytesForCurrentThread() - changeBefore;

        Assert.IsTrue(steadyDelta < changeDelta,
            $"steady-state ticks ({steadyDelta} bytes over 5) must allocate less than changing-total ticks ({changeDelta} bytes over 5): the elapsed string is memoized on the displayed centisecond value");
    }

    [TestMethod]
    public void Render_SteadyState_AllocatesUnderTheStormBudget()
    {
        using var canvas = new StopwatchTimerCanvas();
        using var bitmap = new SKBitmap(300, 200);
        using var skCanvas = new SKCanvas(bitmap);
        var bounds = new SKRect(0, 0, 300, 200);

        canvas.Render(skCanvas, bounds, TimeSpan.FromSeconds(65.42), true, SKColors.White, SKColors.Cyan);

        long before = GC.GetAllocatedBytesForCurrentThread();
        canvas.Render(skCanvas, bounds, TimeSpan.FromSeconds(65.42), true, SKColors.White, SKColors.Cyan);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(delta <= SteadyTickStormBudgetBytes,
            $"a steady-state stopwatch tick allocated {delta} bytes (budget {SteadyTickStormBudgetBytes}): a per-tick string/collection storm has crept into the stopwatch path");
    }
}
