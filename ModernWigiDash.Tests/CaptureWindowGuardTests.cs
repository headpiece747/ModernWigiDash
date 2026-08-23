namespace ModernWigiDash.Tests;

[TestClass]
public class CaptureWindowGuardTests
{
    private static string KeyFor(string city)
        => WeatherQueryKey.Build(new WeatherLocation("city", city, null, null, null));

    [TestMethod]
    public void StillCurrent_LiveKeyUnchanged_True()
    {
        string key = KeyFor("Berlin");
        var guard = new CaptureWindowGuard(key, () => key);

        Assert.IsTrue(guard.StillCurrent());
        Assert.IsFalse(guard.Dropped);
    }

    [TestMethod]
    public void StillCurrent_LiveKeyChanged_False()
    {
        var guard = new CaptureWindowGuard(KeyFor("Berlin"), () => KeyFor("Munich"));

        Assert.IsFalse(guard.StillCurrent());
        Assert.IsTrue(guard.Dropped, "a changed live key is the drop verdict");
    }

    [TestMethod]
    public void StillCurrent_ReadsTheLiveKeyPerCall_NotCachedAtCapture()
    {
        string berlin = KeyFor("Berlin");
        string live = berlin;
        var guard = new CaptureWindowGuard(berlin, () => live);

        Assert.IsTrue(guard.StillCurrent(), "the unedited identity is still current");
        live = KeyFor("Munich");

        Assert.IsTrue(guard.Dropped, "an edit landing after the capture must be seen at the next re-check");
    }

    [TestMethod]
    public void StartKey_IsTheCapturedKey()
    {
        string key = KeyFor("Berlin");
        var guard = new CaptureWindowGuard(key, () => key);

        Assert.AreEqual(key, guard.StartKey, "the guard holds the key the window started for");
    }

    [TestMethod]
    public void StillCurrent_ClearedIdentity_DropsAnyStartedWindow()
    {
        // The edit path's invalidation clears the client's identity query to
        // empty (never null in production: the live source is the fetch
        // control's LastLocationQuery, "" or a built key). A cleared live
        // key is a change for any started fetch.
        var guard = new CaptureWindowGuard(KeyFor("Berlin"), () => "");

        Assert.IsTrue(guard.Dropped, "an invalidated identity must drop the in-flight result");
    }

    [TestMethod]
    public void StillCurrent_RoutesThroughTheAdrPredicate_NotASecondRule()
    {
        // The re-check IS the ADR-0006 predicate on (start, live): ordinal,
        // case is identity. A case change is a new place, so it drops.
        string start = "City|Berlin|||";
        var guard = new CaptureWindowGuard(start, () => "City|berlin|||");

        Assert.IsFalse(WeatherQueryKey.SameKey(start, "City|berlin|||"));
        Assert.IsTrue(guard.Dropped, "case is identity: a case change is a new place");
    }
}
