using System.IO;
using ModernWigiDash.App.Power;

namespace ModernWigiDash.Tests;

/// <summary>
/// The window's startup wiring pinned against the REAL list. The context's
/// null-tolerant module derefs pin the run policy (a reorder degrades to a
/// benign no-op, not the historical startup NRE); these pins pin the
/// sequence itself: the persistence + host modules before the profile load
/// (a widget's InitializeAsync runs synchronously inside the load and calls
/// back into the context), the state resyncs before the wired arm (their
/// XAML events stay guarded), the pump after the modules it composes into,
/// and the wired arm last.
/// </summary>
[TestClass]
public class StartupWiringTests
{
    private static readonly StaHost Host = new("StartupWiring-STA");

    [TestCleanup]
    public void Cleanup()
    {
        Host.DetachApplication();
    }

    [TestMethod]
    public void BuildStartupWiring_PersistenceAndHostModules_PrecedeTheProfileLoad()
    {
        StartupWiring plan = PlanOfLiveWindow();

        Assert.IsTrue(IndexOf(plan, "ProfilePersistence") < IndexOf(plan, "HostModules"),
            "the inspector's onProfileChanged hook references the persistence module, which the host modules construct");
        Assert.IsTrue(IndexOf(plan, "HostModules") < IndexOf(plan, "ProfileLoad"),
            "a widget's InitializeAsync runs synchronously inside the profile load and calls back into the context - the host modules must exist first");
    }

    [TestMethod]
    public void BuildStartupWiring_PumpComesAfterTheModulesItComposesInto()
    {
        StartupWiring plan = PlanOfLiveWindow();

        Assert.IsTrue(IndexOf(plan, "FrameDelivery") < IndexOf(plan, "FramePump"),
            "the pump pushes into the delivery - the delivery must exist before the first tick");
        Assert.IsTrue(IndexOf(plan, "ProfileLoad") < IndexOf(plan, "FramePump"),
            "the compose reads the profile - the profile load must precede the first tick");
    }

    [TestMethod]
    public void BuildStartupWiring_StateResyncs_PrecedeTheWiredArm()
    {
        StartupWiring plan = PlanOfLiveWindow();

        Assert.IsTrue(IndexOf(plan, "SnapToGridResync") < IndexOf(plan, "Wired"),
            "the snap resync fires the guarded checkbox event - a startup state resync is not a mutation and must not arm a save");
        Assert.IsTrue(IndexOf(plan, "EditModeResync") < IndexOf(plan, "Wired"),
            "the checkbox-to-compositor resync fires while the guard is still off - the guard must not arm yet");
    }

    [TestMethod]
    public void BuildStartupWiring_WiredArmsLast()
    {
        StartupWiring plan = PlanOfLiveWindow();

        Assert.AreEqual("Wired", plan.OrderedSteps[^1].Name,
            "the guarded XAML handlers must arm only after every module they forward to exists");
    }

    [TestMethod]
    public void BuildStartupWiring_TrayPrecedesTheWiredArm()
    {
        StartupWiring plan = PlanOfLiveWindow();

        Assert.IsTrue(IndexOf(plan, "Tray") < IndexOf(plan, "Wired"),
            "the tray's click handlers forward to the window's show/quit like every other module - the wired arm must arm last, after the tray exists");
    }

    [TestMethod]
    public void BuildStartupWiring_StepNames_AreUnique()
    {
        StartupWiring plan = PlanOfLiveWindow();

        Assert.AreEqual(plan.OrderedSteps.Count,
            plan.OrderedSteps.Select(step => step.Name).Distinct().Count(),
            "duplicate step names would make the ordering pins read the first occurrence and hide a real reorder");
    }

    private static StartupWiring PlanOfLiveWindow()
    {
        string profilePath = Path.Combine(
            Path.GetTempPath(), "wmd-startup-" + Guid.NewGuid().ToString("N"), "profile.json");
        return Host.Run(() =>
        {
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(), null, null, null, null, FakeTransport.InertEngine());
            try
            {
                // The constructor already applied one plan; this second build
                // is never run - it only shapes the same ordered list, so the
                // pins read the sequence without re-wiring the window.
                return window.BuildStartupWiring(
                    new StubPresentMonNative(), profilePath, new NoopPowerModeSource());
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static int IndexOf(StartupWiring plan, string name)
    {
        // A removed step must fail loudly: FindIndex's -1 would make a
        // strict < ordering pin on a missing step silently go green.
        int index = plan.OrderedSteps.ToList().FindIndex(step => step.Name == name);
        if (index < 0)
        {
            Assert.Fail($"step '{name}' is missing from the startup plan - the ordering pins would read a silent -1");
        }
        return index;
    }
}
