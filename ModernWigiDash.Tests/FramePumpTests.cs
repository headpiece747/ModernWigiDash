using System.Threading;
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
    private static (Exception? Error, object? Result) RunOnSta(Func<object?> work)
    {
        Exception? error = null;
        object? result = null;
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
        Assert.IsTrue(result is true, "no ticks may fire after Stop");
    }
}
