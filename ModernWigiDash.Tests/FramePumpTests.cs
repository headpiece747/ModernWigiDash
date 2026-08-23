using System.Windows.Threading;

namespace ModernWigiDash.Tests;

/// <summary>
/// Drives the <see cref="FramePump"/> cadence on a real STA + Dispatcher
/// (DispatcherTimer ticks only fire on a pumping dispatcher). The compose/send
/// and repaint steps are recording delegates, so no compositor or USB engine
/// is involved. The one-shot STA invocations ride the shared
/// <see cref="StaRunner"/>, the house's STA double — this file keeps no
/// private pump of its own.
/// </summary>
[TestClass]
public class FramePumpTests
{
    [TestMethod]
    public void Tick_ComposeGateFalse_SkipsComposeButRepaintsAndFiresOnTick()
    {
        var (composes, repaints, onTicks) = StaRunner.Run(() =>
        {
            int c = 0;
            int r = 0;
            int t = 0;
            var pump = new FramePump(
                composeAndSend: () => Interlocked.Increment(ref c),
                requestRepaint: () => Interlocked.Increment(ref r),
                onTick: () => Interlocked.Increment(ref t),
                composeGate: () => false);

            pump.Tick();
            pump.Tick();

            return (c, r, t);
        });

        Assert.AreEqual(0, composes, "The gate veto must skip the compose step");
        Assert.AreEqual(2, repaints, "The repaint must still fire every tick");
        Assert.AreEqual(2, onTicks, "The badge callback must still fire every tick");
    }

    [TestMethod]
    public void Tick_ComposeGateTrue_ComposesNormally()
    {
        var composes = StaRunner.Run(() =>
        {
            int c = 0;
            var pump = new FramePump(
                composeAndSend: () => Interlocked.Increment(ref c),
                requestRepaint: () => { },
                composeGate: () => true);

            pump.Tick();

            return c;
        });

        Assert.AreEqual(1, composes, "An open gate must not change the compose behavior");
    }

    [TestMethod]
    public void Tick_OrdersComposeBeforeRepaint_BeforeOnTick()
    {
        // The buffer the window draws is the buffer that was sent: a repaint
        // that ran before the compose would draw the previous buffer, so the
        // tick's internal order (compose → repaint → badge) is a display
        // invariant, not an implementation detail.
        var order = StaRunner.Run(() =>
        {
            var log = new List<string>();
            var pump = new FramePump(
                composeAndSend: () => log.Add("compose"),
                requestRepaint: () => log.Add("repaint"),
                onTick: () => log.Add("badge"));

            pump.Tick();
            return log;
        });

        Assert.AreEqual("compose, repaint, badge", string.Join(", ", order),
            "compose must precede the repaint (the window draws the buffer it sent), and the badge rides last");
    }

    [TestMethod]
    public void Start_ComposesSendsAndRepaints_OnCadence()
    {
        var ticks = StaRunner.Run(() =>
        {
            int ticks = 0;
            int repaints = 0;
            var dispatcher = Dispatcher.CurrentDispatcher;
            var pump = new FramePump(
                composeAndSend: () => Interlocked.Increment(ref ticks),
                requestRepaint: () => Interlocked.Increment(ref repaints),
                interval: TimeSpan.FromMilliseconds(10));
            pump.Start();

            var observer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            observer.Tick += (_, _) =>
            {
                observer.Stop();
                pump.Stop();
                pump.Dispose();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            };
            observer.Start();
            Dispatcher.Run();
            return ticks;
        });

        Assert.IsTrue(ticks is int t && t >= 3, $"expected at least 3 ticks, got {ticks}");
    }

    [TestMethod]
    public void Stop_HaltsTicks()
    {
        var result = StaRunner.Run(() =>
        {
            int ticks = 0;
            var dispatcher = Dispatcher.CurrentDispatcher;
            var pump = new FramePump(
                composeAndSend: () => Interlocked.Increment(ref ticks),
                requestRepaint: () => { },
                interval: TimeSpan.FromMilliseconds(10));
            pump.Start();

            int countAtStop = -1;
            var stopTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            stopTimer.Tick += (_, _) =>
            {
                stopTimer.Stop();
                pump.Stop();
                countAtStop = ticks;
            };
            stopTimer.Start();

            var checkTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            checkTimer.Tick += (_, _) =>
            {
                checkTimer.Stop();
                pump.Dispose();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            };
            checkTimer.Start();
            Dispatcher.Run();

            return countAtStop == ticks;
        });

        Assert.IsTrue(result, "no ticks may fire after Stop");
    }

    [TestMethod]
    public void Dispose_QueuedTick_DoesNotFireCallbacks()
    {
        // A tick queued in the dispatcher just before Dispose is not cancelled
        // by DispatcherTimer.Stop — it runs after teardown. The disposed guard
        // must make that queued tick a no-op, or the late tick composes onto
        // disposed state (ObjectDisposedException during close).
        var ticks = StaRunner.Run(() =>
        {
            int ticks = 0;
            var pump = new FramePump(
                composeAndSend: () => Interlocked.Increment(ref ticks),
                requestRepaint: () => { },
                interval: TimeSpan.FromMilliseconds(10));

            pump.Dispose();
            pump.Tick(); // the queued tick, invoked after Dispose

            return ticks;
        });

        Assert.AreEqual(0, ticks, "No callback may fire after Dispose.");
    }

    [TestMethod]
    public void Start_InvokesOnTick_OncePerCadence()
    {
        var (ticks, onTicks) = StaRunner.Run(() =>
        {
            int ticks = 0;
            int onTicks = 0;
            var dispatcher = Dispatcher.CurrentDispatcher;
            var pump = new FramePump(
                composeAndSend: () => Interlocked.Increment(ref ticks),
                requestRepaint: () => { },
                onTick: () => Interlocked.Increment(ref onTicks),
                interval: TimeSpan.FromMilliseconds(10));
            pump.Start();

            var observer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            observer.Tick += (_, _) =>
            {
                observer.Stop();
                pump.Stop();
                pump.Dispose();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            };
            observer.Start();
            Dispatcher.Run();
            return (ticks, onTicks);
        });

        Assert.IsTrue(ticks >= 3, $"expected at least 3 ticks, got {ticks}");
        Assert.AreEqual(ticks, onTicks, "onTick must fire exactly once per cadence tick");
    }

    [TestMethod]
    public void Dispose_QueuedTick_DoesNotFireOnTick()
    {
        // The disposed guard covers the badge callback too: a tick queued just
        // before Dispose must not run onTick (the window's UpdateUsbBadge)
        // against torn-down state.
        var onTicks = StaRunner.Run(() =>
        {
            int onTicks = 0;
            var pump = new FramePump(
                composeAndSend: () => { },
                requestRepaint: () => { },
                onTick: () => Interlocked.Increment(ref onTicks));

            pump.Dispose();
            pump.Tick(); // the queued tick, invoked after Dispose

            return onTicks;
        });

        Assert.AreEqual(0, onTicks, "No onTick may fire after Dispose.");
    }
}
