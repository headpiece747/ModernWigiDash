using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Hardware.Aida64;
using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

/// <summary>
/// The tick allocation budget pin: the steady-state 30 FPS tick (compose the
/// active page, push the frame into the delivery) must stay allocation-light
/// on the managed heap. Warms the caches, then measures the
/// <see cref="GC.GetAllocatedBytesForCurrentThread()"/> delta around each
/// tick's compose+push and asserts the per-tick and total deltas stay under
/// budget.
///
/// Counter note: the per-thread counter is used because the process-wide
/// <see cref="GC.GetTotalAllocatedBytes()"/> quantizes sub-8KB allocations
/// into ~8KB chunks when sampled at tick granularity (the 2026-08-24
/// hot-path triage's phantom "8.2KB per tick object" was exactly this
/// artifact; the true steady-state tick allocates ~1-2 KB). The per-thread
/// counter is exact on the single test thread that runs the tick. The
/// sender's own loop (drain/pace/send on the thread pool) is outside the
/// per-thread window by construction; the encode runs on the caller thread
/// inside <c>Push</c>, so the tick's real work is fully measured.
///
/// Scope note: the counter is the managed heap. It catches per-tick buffer
/// copies (a full frame is ~1.2 MB), per-tick string storms, and per-tick
/// collections/paints; the native side of the path (Skia surfaces, the
/// framebuffer) is guarded by the 2026-08-21 memory soak instead. The wait
/// for the send is outside the window (the polling machinery's own work
/// would otherwise ride the counter), and the send is paced at the real
/// 33ms cadence so the run covers the true steady state.
/// </summary>
[TestClass]
public class FramePipelineAllocationTests
{
    private const int WarmTicks = 30;
    private const int MeasuredTicks = 10;
    // Steady-state compose+push allocates ~1-2 KB per tick on the managed
    // heap (the per-draw text-blob interop floor, the time-string memo
    // re-formats at most once per second, the fonts are cache hits, the
    // encode writes into the pooled buffer). The budget is several times
    // that: a per-tick regression (a buffer copy, a string/collection
    // storm, a per-frame font or paint) must exceed it.
    private const long PerTickBudgetBytes = 16 * 1024;
    private const long TotalBudgetBytes = 160 * 1024;

    [TestMethod]
    public void WidgetRenderPaths_SteadyState_AllocateUnderBudget()
    {
        // The set extension of the single-widget pin above: each entry composes
        // a one-widget page and pushes it through the same delivery, so the
        // per-tick budget applies to the widget's render path in the real tick
        // pipeline. Entries are fixture-driven (fixed clock, empty/in-memory
        // stores, the placeholder legs); a steady tick on a slow-changing input
        // must stay under the same budget as the digital clock.
        //
        // Attested, not fixture-driven here (their tick paths are live-service
        // shaped: a network fetch, a socket, a scan, a capture): weather, steam,
        // twitch chat, crypto ticker, audio visualizer, frame time. Their draw
        // owners are pinned at the canvas level (the round-9 render-smoke test
        // files) and share the same hoisted-paint + memo discipline this pin
        // measures; a per-tick allocation in one of those widget render methods
        // is caught by the canvas-level pin plus this test's shape.
        using var delivery = FrameDelivery.Create(
            encoder: new SkiaRgb565Encoder(),
            send: _ => FrameSendResult.Sent);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 24, 10, 30, 0, TimeSpan.Zero));

        foreach (RenderPath path in RenderPaths(clock))
        {
            (long worstTick, long total, long warmFloor) = MeasurePath(path, delivery);

            Assert.IsTrue(worstTick <= PerTickBudgetBytes,
                $"{path.Name}: a steady-state tick allocated {worstTick} bytes (budget {PerTickBudgetBytes}, warm-floor {warmFloor}) - a per-tick allocation (buffer copy, string storm, per-frame font or paint) has crept into the {path.Name} render path");
            Assert.IsTrue(total <= TotalBudgetBytes,
                $"{path.Name}: the {MeasuredTicks} measured ticks allocated {total} bytes total (budget {TotalBudgetBytes}) - the {path.Name} render path is no longer allocation-light");
        }
    }

    /// <summary>One render path under measurement: the name (for the failure
    /// message) and the one-widget page builder.</summary>
    private sealed record RenderPath(string Name, Func<(SkiaFrameCompositor Compositor, PlacedWidgetInstance Placed)> BuildPage);

    private static (long Worst, long Total, long WarmFloor) MeasurePath(RenderPath path, FrameDelivery delivery)
    {
        var (compositor, placed) = path.BuildPage();
        using (compositor)
        {
            var page = new PageLayout
            {
                BackgroundHexColor = "#12141D",
                Widgets = [placed],
            };

            long Tick()
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                compositor.Compose(page);
                long expected = delivery.FramesSent + 1;
                delivery.Push(compositor.FrameBuffer);
                long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                WaitForSend(delivery, expected);
                return delta;
            }

            long worstTick = 0, total = 0, warmFloor = 0;
            for (int i = 0; i < WarmTicks; i++)
            {
                warmFloor = Math.Max(warmFloor, Tick());
            }

            // Paint proof: the composed frame must actually paint the
            // widget's region. A page the compositor skipped (a bad entry
            // wiring) would allocate ~0 and pass the budget vacuously; the
            // canvas smoke files pin the draw owners, this guards the entry.
            var frame = compositor.FrameBuffer;
            int painted = 0;
            for (int x = (int)placed.X + 10; x < (int)placed.X + placed.Width - 10 && x < frame.Width; x += 5)
            {
                for (int y = (int)placed.Y + 10; y < (int)placed.Y + placed.Height - 10 && y < frame.Height; y += 5)
                {
                    if (frame.GetPixel(x, y).Alpha != 0)
                    {
                        painted++;
                    }
                }
            }
            Assert.IsTrue(painted > 0,
                $"{path.Name}: the composed frame painted nothing in the widget's region - the entry wiring is wrong and the allocation budget passes vacuously");

            for (int i = 0; i < MeasuredTicks; i++)
            {
                long delta = Tick();
                worstTick = Math.Max(worstTick, delta);
                total += delta;
            }

            return (worstTick, total, warmFloor);
        }
    }

    private static void WaitForSend(FrameDelivery delivery, long expected)
        => TestWait.WaitUntilAsync(() => delivery.FramesSent >= expected, TimeSpan.FromSeconds(5))
            .GetAwaiter().GetResult();

    private static IEnumerable<RenderPath> RenderPaths(FakeTimeProvider clock)
    {
        yield return new RenderPath("stopwatch", () => Build("stopwatch_timer", 460, 220,
            new StopwatchTimerWidget { Clock = clock }));
        yield return new RenderPath("calendar (minimal card)", () => Build("calendar", 200, 180,
            new CalendarWidget { Clock = clock }));
        yield return new RenderPath("cozy todo (empty snapshot)", () => Build("cozy_todo", 460, 220,
            new CozyTodoWidget { Clock = clock }));
        yield return new RenderPath("aida panel (placeholder)", () => Build("aida_panel", 460, 220,
            new AidaPanelWidget(new AidaMmapReader(new FakeAidaMmapSource()))));
        yield return new RenderPath("now playing (idle)", () => Build("now_playing", 460, 220,
            new NowPlayingWidget { Clock = clock }));
        yield return new RenderPath("picture (placeholder)", () => Build("picture", 300, 300,
            new PictureAndGifWidget { Clock = clock }));
        yield return new RenderPath("text label", () => Build("text_label", 460, 110,
            new TextLabelWidget { Text = "hello world" }));
    }

    private static (SkiaFrameCompositor Compositor, PlacedWidgetInstance Placed) Build(string pluginId, int width, int height, IModernWidget widget)
    {
        var compositor = new SkiaFrameCompositor { IsEditMode = true };
        var placed = new PlacedWidgetInstance
        {
            PluginId = pluginId,
            DisplayName = pluginId,
            ActiveInstance = widget,
            X = 120,
            Y = 160,
            Width = width,
            Height = height,
        };
        compositor.SelectedWidget = placed;
        return (compositor, placed);
    }

    [TestMethod]
    public void TickPath_SteadyState_AllocatesUnderBudget()
    {
        using var compositor = new SkiaFrameCompositor();
        compositor.IsEditMode = true;

        // One real widget, edit mode on and the widget selected: the compose
        // path covers the background, the grid, the widget render, and the
        // selection badge (the T4-class per-frame badge work).
        // A fixed clock keeps the time-string memo stable across the run.
        var clock = new DigitalAnalogClockWidget
        {
            ClockMode = "Digital",
            Clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 24, 10, 30, 0, TimeSpan.Zero))
        };
        var placed = new PlacedWidgetInstance
        {
            PluginId = "clock_modern",
            DisplayName = "Clock",
            ActiveInstance = clock,
            X = 120,
            Y = 160,
            Width = 460,
            Height = 220,
        };
        compositor.SelectedWidget = placed;

        var page = new PageLayout
        {
            BackgroundHexColor = "#12141D",
            Widgets = [placed],
        };

        // The real production encoder (the encode is part of the tick's work)
        // and an inert send at the real 33ms pacing cadence.
        using var delivery = FrameDelivery.Create(
            encoder: new SkiaRgb565Encoder(),
            send: _ => FrameSendResult.Sent);

        long worstTick = 0;
        long total = 0;
        long warmFloor = 0;

        // One compose+push: the window is compose+push only; the send is
        // waited for OUTSIDE the window (the polling machinery's own work
        // must not ride the counter). <c>expected</c> is captured BEFORE the
        // push so the wait targets exactly the frame this tick sent.
        long Tick()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            compositor.Compose(page);
            long expected = delivery.FramesSent + 1;
            delivery.Push(compositor.FrameBuffer);
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            WaitForSend(delivery, expected);
            return delta;
        }

        // Warm: fill the font caches, the widget's memo, the pool, and the
        // delivery's first-send paths. The warm window is not measured.
        for (int i = 0; i < WarmTicks; i++)
        {
            warmFloor = Math.Max(warmFloor, Tick());
        }

        // Measure: one delta per tick around compose+push only.
        for (int i = 0; i < MeasuredTicks; i++)
        {
            long delta = Tick();
            worstTick = Math.Max(worstTick, delta);
            total += delta;
        }

        Assert.IsTrue(worstTick <= PerTickBudgetBytes,
            $"a steady-state tick allocated {worstTick} bytes (budget {PerTickBudgetBytes}, warm-floor {warmFloor}) - a per-tick allocation (buffer copy, string storm, per-frame font or paint) has crept into the compose/push path");
        Assert.IsTrue(total <= TotalBudgetBytes,
            $"the {MeasuredTicks} measured ticks allocated {total} bytes total (budget {TotalBudgetBytes}) - the tick path is no longer allocation-light");
    }
}
