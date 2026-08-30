using System.IO;
using ModernWigiDash.App.Power;
using AppClass = ModernWigiDash.App.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// The window's teardown plan pinned against the REAL list — the
/// orchestrator's synthetic steps pin the run policy (in order + the last
/// resort no matter what), these pin the sequence itself: persist before
/// teardown, the pump before the delivery it pushes into, the engine
/// strictly last, and the exit marker + final flush landing in the file.
/// </summary>
[TestClass]
public class TeardownPlanTests
{
    private static readonly StaHost Host = new("TeardownPlan-STA");

    [TestCleanup]
    public void Cleanup()
    {
        Host.DetachApplication();
    }

    [TestMethod]
    public void BuildTeardownPlan_PersistIsFirst_AndPrecedesItsDispose()
    {
        TeardownPlan plan = PlanOfLiveWindow();

        Assert.AreEqual("ProfilePersist", plan.OrderedSteps[0].Name,
            "a clean exit must land the final profile state before any teardown dispose");
        Assert.IsTrue(IndexOf(plan, "ProfilePersist") < IndexOf(plan, "ProfilePersistence"),
            "the profile persistence flush must precede its own dispose");
    }

    [TestMethod]
    public void BuildTeardownPlan_PumpPrecedesTheDelivery_ItPushesInto()
    {
        TeardownPlan plan = PlanOfLiveWindow();

        Assert.IsTrue(IndexOf(plan, "FramePump") < IndexOf(plan, "FrameDelivery"),
            "a compose tick must never push into a disposed delivery — the pump disposes first");
    }

    [TestMethod]
    public void BuildTeardownPlan_EngineIsTheLastResort_NotAnOrderedStep()
    {
        TeardownPlan plan = PlanOfLiveWindow();

        Assert.AreEqual("UsbEngineStandby", plan.LastResort.Name,
            "the display-standby dispose is the one step that must never be skipped");
        Assert.IsFalse(plan.OrderedSteps.Any(step => step.Name == "UsbEngineStandby"),
            "an ordered step is skippable after a throw — the standby dispose must ride the last resort");
    }

    [TestMethod]
    public void BuildTeardownPlan_TrayDisposeLandsAfterTheProfilePersist()
    {
        TeardownPlan plan = PlanOfLiveWindow();

        Assert.IsTrue(IndexOf(plan, "ProfilePersist") < IndexOf(plan, "TrayDispose"),
            "the profile state lands on disk before the exit affordance (the tray icon) disappears - a user who saw the icon go can trust the profile was saved");
    }

    [TestMethod]
    public void WriteExitMarker_MarkerPlusFinalFlush_LandInTheFile()
    {
        // A short marker line sits under both flush cadences (8 KB / 250 ms),
        // so it would sit in the buffer at process exit unless the exit
        // ritual's Flush lands it — the on-device close that lost the standby
        // line was exactly this.
        string logPath = Path.Combine(Path.GetTempPath(), $"wmd-exit-{Guid.NewGuid():N}", "display_device.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        string original = FileLog.LogPath;
        try
        {
            FileLog.LogPath = logPath;
            AppClass.WriteExitMarker();

            // FileLog keeps its write stream open, so the read must share
            // read+write access (File.ReadAllText's FileShare.Read rejects).
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string content = reader.ReadToEnd();
            Assert.IsTrue(content.Contains("=== Application exiting ==="),
                "the exit marker must land in the file — the final Flush is what makes it so");
        }
        finally
        {
            FileLog.LogPath = original;
            try { Directory.Delete(Path.GetDirectoryName(logPath)!, recursive: true); }
            catch (IOException) { /* best-effort: FileLog may still hold the stream */ }
        }
    }

    private static TeardownPlan PlanOfLiveWindow()
    {
        string profilePath = Path.Combine(
            Path.GetTempPath(), "wmd-teardown-" + Guid.NewGuid().ToString("N"), "profile.json");
        return Host.Run(() =>
        {
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(), UsbEngine: FakeTransport.InertEngine()));
            try
            {
                return window.BuildTeardownPlan();
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static int IndexOf(TeardownPlan plan, string name) =>
        plan.OrderedSteps.ToList().FindIndex(step => step.Name == name);
}
