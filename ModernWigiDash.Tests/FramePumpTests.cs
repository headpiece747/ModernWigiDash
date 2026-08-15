using System.Windows.Threading;
using ModernWigiDash.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// Drives the <see cref="FramePump"/> cadence on a real STA + Dispatcher
/// (DispatcherTimer ticks only fire on a pumping dispatcher). The compose/send
/// and repaint steps are recording delegates, so no compositor or USB engine
/// is involved.
/// </summary>
[TestClass]
public class FramePumpTests
{
    private static (Exception? Error, T? Result) RunOnSta<T>(Func<T> work)
    {
        Exception? error = null;
        T? result = default;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
            Name = "FramePumpTests-STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return (error, result);
    }

    [TestMethod]
    public void Tick_ComposeGateFalse_SkipsComposeButRepaintsAndFiresOnTick()
    {
        var (error, counts) = RunOnSta(() =>
        {
            int composes = 0;
            int repaints = 0;
            int onTicks = 0;
            var pump = new FramePump(
                composeAndSend: () => Interlocked.Increment(ref composes),
                requestRepaint: () => Interlocked.Increment(ref repaints),
                onTick: () => Interlocked.Increment(ref onTicks),
                composeGate: () => false);

            pump.Tick();
            pump.Tick();

            return (composes, repaints, onTicks);
        });

        Assert.IsNull(error);
        var (c, r, t) = counts;
        Assert.AreEqual(0, c, "The gate veto must skip the compose step");
        Assert.AreEqual(2, r, "The repaint must still fire every tick");
        Assert.AreEqual(2, t, "The badge callback must still fire every tick");
    }

    [TestMethod]
    public void Tick_ComposeGateTrue_ComposesNormally()
    {
        var (error, counts) = RunOnSta(() =>
        {
            int composes = 0;
            var pump = new FramePump(
                composeAndSend: () => Interlocked.Increment(ref composes),
                requestRepaint: () => { },
                composeGate: () => true);

            pump.Tick();

            return composes;
        });

        Assert.IsNull(error);
        Assert.AreEqual(1, counts, "An open gate must not change the compose behavior");
    }

    [TestMethod]
    public void Start_ComposesSendsAndRepaints_OnCadence()
    {
        var (error, ticks) = RunOnSta(() =>
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

        Assert.IsNull(error, error?.ToString());
        Assert.IsTrue(ticks is int t && t >= 3, $"expected at least 3 ticks, got {ticks}");
    }

    [TestMethod]
    public void Stop_HaltsTicks()
    {
        var (error, result) = RunOnSta(() =>
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

        Assert.IsNull(error, error?.ToString());
        Assert.IsTrue(result, "no ticks may fire after Stop");
    }

    [TestMethod]
    public void Dispose_QueuedTick_DoesNotFireCallbacks()
    {
        // A tick queued in the dispatcher just before Dispose is not cancelled
        // by DispatcherTimer.Stop — it runs after teardown. The disposed guard
        // must make that queued tick a no-op, or the late tick composes onto
        // disposed state (ObjectDisposedException during close).
        var (error, ticks) = RunOnSta(() =>
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

        Assert.IsNull(error, error?.ToString());
        Assert.AreEqual(0, ticks, "No callback may fire after Dispose.");
    }

    [TestMethod]
    public void Start_InvokesOnTick_OncePerCadence()
    {
        var (error, result) = RunOnSta(() =>
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

        Assert.IsNull(error, error?.ToString());
        var (ticks, onTicks) = result is (int t, int o) ? (t, o) : (0, 0);
        Assert.IsTrue(ticks >= 3, $"expected at least 3 ticks, got {ticks}");
        Assert.AreEqual(ticks, onTicks, "onTick must fire exactly once per cadence tick");
    }

    [TestMethod]
    public void Dispose_QueuedTick_DoesNotFireOnTick()
    {
        // The disposed guard covers the badge callback too: a tick queued just
        // before Dispose must not run onTick (the window's UpdateUsbBadge)
        // against torn-down state.
        var (error, onTicks) = RunOnSta(() =>
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

        Assert.IsNull(error, error?.ToString());
        Assert.AreEqual(0, onTicks, "No onTick may fire after Dispose.");
    }
}
