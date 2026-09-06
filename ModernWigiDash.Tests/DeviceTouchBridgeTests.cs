using System.IO;
using ModernWigiDash.App.Input;

namespace ModernWigiDash.Tests;

/// <summary>
/// The device-touch bridge's contract pinned without a window: the queue feeds
/// the gesture machine IN ORDER — a burst drained by one drain still feeds every
/// event (the interpreter needs the full Down → Move → Up sequence, not just the
/// last point), and a release without its contact sample is not a gesture. The
/// display's navigation input arrives only through this queue, so the sequence
/// IS the contract.
/// </summary>
[TestClass]
public class DeviceTouchBridgeTests
{
    [TestMethod]
    public void Enqueue_InOrderSwipeBurst_FeedsEveryEventInOrder()
    {
        var events = new List<(float X, float Y, TouchEventType Type)>();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var bridge = new DeviceTouchBridge(dispatcher, (x, y, type) => events.Add((x, y, type)));

        // The display's left-swipe vocabulary: contact, an intermediate point,
        // release past the 70 px X threshold with the Y displacement inside
        // the 80 px tolerance.
        bridge.Enqueue(500, 300, TouchEventType.TouchDown);
        bridge.Enqueue(450, 300, TouchEventType.TouchDown);
        bridge.Enqueue(300, 300, TouchEventType.TouchUp);

        // Drive the drain synchronously.
        bridge.DrainForTest();

        Assert.AreEqual(3, events.Count, "one drain must feed the whole burst");
        Assert.AreEqual(TouchEventType.TouchDown, events[0].Type);
        Assert.AreEqual(500f, events[0].X);
        Assert.AreEqual(TouchEventType.TouchDown, events[1].Type);
        Assert.AreEqual(450f, events[1].X);
        Assert.AreEqual(TouchEventType.TouchUp, events[2].Type);
        Assert.AreEqual(300f, events[2].X);
    }

    [TestMethod]
    public void Enqueue_ReleaseWithoutContact_IsNotAGesture()
    {
        var events = new List<(float X, float Y, TouchEventType Type)>();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var bridge = new DeviceTouchBridge(dispatcher, (x, y, type) => events.Add((x, y, type)));

        // A stale release left in the queue has no contact sample to pair with.
        bridge.Enqueue(300, 300, TouchEventType.TouchUp);

        bridge.DrainForTest();

        Assert.AreEqual(1, events.Count, "the release was enqueued");
        Assert.AreEqual(TouchEventType.TouchUp, events[0].Type, "a release without its contact must be fed (the gesture machine refuses it)");
    }

    [TestMethod]
    public void Enqueue_TwoCompleteSwipes_OneDrainFeedsAllSixEvents()
    {
        var events = new List<(float X, float Y, TouchEventType Type)>();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var bridge = new DeviceTouchBridge(dispatcher, (x, y, type) => events.Add((x, y, type)));

        // Two complete left swipes enqueued as one burst.
        for (var i = 0; i < 2; i++)
        {
            bridge.Enqueue(500, 300, TouchEventType.TouchDown);
            bridge.Enqueue(450, 300, TouchEventType.TouchDown);
            bridge.Enqueue(300, 300, TouchEventType.TouchUp);
        }

        bridge.DrainForTest();

        Assert.AreEqual(6, events.Count, "one drain must feed the whole burst, in order");
    }
}
