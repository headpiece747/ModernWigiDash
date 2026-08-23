using System.IO;

namespace ModernWigiDash.Tests;

/// <summary>
/// The window's device-touch drain contract pinned through a live window on
/// STA: the queue feeds the gesture machine IN ORDER — a burst drained by one
/// drain still feeds every event (the interpreter needs the full Down → Move →
/// Up sequence, not just the last point), and a release without its contact
/// sample is not a gesture. The display's navigation input arrives only
/// through this queue, so the sequence IS the contract.
/// </summary>
[TestClass]
public class DeviceTouchDrainTests
{
    private static readonly StaHost Host = new("DeviceTouchDrain-STA");

    [TestCleanup]
    public void Cleanup()
    {
        Host.DetachApplication();
    }

    [TestMethod]
    public void Drain_InOrderSwipeBurst_SwitchesToTheNextPage()
    {
        var (_, error) = Host.Invoke(() =>
        {
            var window = NewWindow(pageCount: 2);
            try
            {
                Assert.AreEqual(0, window.ActivePageIndex,
                    "the two-page profile starts on the first page");

                // The display's left-swipe vocabulary: contact, an
                // intermediate point (the hardware still reports Down — the
                // gesture machine remaps it to Move), release past the 70 px
                // X threshold with the Y displacement inside the 80 px
                // tolerance.
                window.EnqueueDeviceTouch(new SKPoint(500, 300), TouchEventType.TouchDown);
                window.EnqueueDeviceTouch(new SKPoint(450, 300), TouchEventType.TouchDown);
                window.EnqueueDeviceTouch(new SKPoint(300, 300), TouchEventType.TouchUp);

                window.DrainDeviceTouchQueue();

                Assert.AreEqual(1, window.ActivePageIndex,
                    "the in-order swipe must reach the gesture machine as one gesture");
            }
            finally
            {
                window.Close();
            }
            return null;
        });
        Assert.IsNull(error, error?.ToString());
    }

    [TestMethod]
    public void Drain_AReleaseWithoutItsContact_IsNotAGesture()
    {
        var (_, error) = Host.Invoke(() =>
        {
            var window = NewWindow(pageCount: 2);
            try
            {
                // A stale release left in the queue has no contact sample to
                // pair with — the gesture machine refuses it (one physical
                // tap is one action, never a ghost navigation).
                window.EnqueueDeviceTouch(new SKPoint(300, 300), TouchEventType.TouchUp);

                window.DrainDeviceTouchQueue();

                Assert.AreEqual(0, window.ActivePageIndex,
                    "a release without its contact must navigate nowhere");
            }
            finally
            {
                window.Close();
            }
            return null;
        });
        Assert.IsNull(error, error?.ToString());
    }

    [TestMethod]
    public void Drain_OneDrain_FeedsEveryEventInTheBurst()
    {
        var (_, error) = Host.Invoke(() =>
        {
            var window = NewWindow(pageCount: 3);
            try
            {
                // Two complete left swipes enqueued as one burst: a drain that
                // fed only the last point (or skipped an event) would land on
                // page 1, not page 3 — the runs-to-empty loop is the "one
                // drain per burst" safety net.
                for (var i = 0; i < 2; i++)
                {
                    window.EnqueueDeviceTouch(new SKPoint(500, 300), TouchEventType.TouchDown);
                    window.EnqueueDeviceTouch(new SKPoint(450, 300), TouchEventType.TouchDown);
                    window.EnqueueDeviceTouch(new SKPoint(300, 300), TouchEventType.TouchUp);
                }

                window.DrainDeviceTouchQueue();

                Assert.AreEqual(2, window.ActivePageIndex,
                    "one drain must feed the whole burst, in order");
            }
            finally
            {
                window.Close();
            }
            return null;
        });
        Assert.IsNull(error, error?.ToString());
    }

    private static MainWindow NewWindow(int pageCount)
    {
        var profile = new ProfileLayout();
        for (var i = 0; i < pageCount; i++)
        {
            profile.Pages.Add(new PageLayout { PageName = $"Page {i + 1}" });
        }
        string dir = Path.Combine(
            Path.GetTempPath(), "wmd-drain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "profile.json");
        File.WriteAllText(path, ProfileOps.ExportJson(profile));
        return new MainWindow(new StubPresentMonNative(), path);
    }
}
