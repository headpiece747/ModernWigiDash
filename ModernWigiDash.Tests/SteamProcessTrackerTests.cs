using System.Text.RegularExpressions;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Steam process tracker's rules: the state fold (Running beats Updating
/// beats Idle beats Uninstalled, a live process overriding the ACF flags),
/// the steam://run launch URI's digits-only shape, and the launch/terminate
/// seams routing through the injected bridge and shell-exec sink. The
/// process bridge is a fake, so no real process is touched.
/// </summary>
[TestClass]
public sealed class SteamProcessTrackerTests
{
    /// <summary>The fake process bridge: a named running set plus a close-call record.</summary>
    private sealed class FakeBridge : ISteamProcessBridge
    {
        public HashSet<string> Running { get; } = [];
        public List<(string Name, TimeSpan Budget)> CloseCalls { get; } = [];
        public bool CloseResult { get; set; } = true;

        public bool IsRunning(string processName) => Running.Contains(processName);

        public bool TryClose(string processName, TimeSpan budget)
        {
            CloseCalls.Add((processName, budget));
            return CloseResult;
        }
    }

    private static SteamAppManifest Manifest(
        string exec = "game.exe",
        SteamAppState flags = SteamAppState.FullyInstalled)
        => new(730u, "Game", "gamedir", exec, flags, 0, 0, 0, 10.0);

    [TestMethod]
    public void Resolve_LiveProcess_ReturnsRunning()
    {
        var bridge = new FakeBridge { Running = { "game" } };
        var tracker = new SteamProcessTracker(bridge, _ => { });

        // Installed + idle flags, but a live process: Running wins.
        SteamGameState state = tracker.Resolve(Manifest());

        Assert.AreEqual(SteamGameState.Running, state);
    }

    [TestMethod]
    public void Resolve_RunningBeatsUpdating()
    {
        var bridge = new FakeBridge { Running = { "game" } };
        var tracker = new SteamProcessTracker(bridge, _ => { });

        // An active-update flag AND a live process: Running wins.
        SteamGameState state = tracker.Resolve(Manifest(flags: SteamAppState.FullyInstalled | SteamAppState.Downloading));

        Assert.AreEqual(SteamGameState.Running, state);
    }

    [TestMethod]
    public void Resolve_UpdatingFlag_NoProcess_ReturnsUpdating()
    {
        var bridge = new FakeBridge();
        var tracker = new SteamProcessTracker(bridge, _ => { });

        SteamGameState state = tracker.Resolve(Manifest(flags: SteamAppState.FullyInstalled | SteamAppState.UpdateRunning));

        Assert.AreEqual(SteamGameState.Updating, state);
    }

    [TestMethod]
    public void Resolve_InstalledNoProcess_ReturnsIdle()
    {
        var bridge = new FakeBridge();
        var tracker = new SteamProcessTracker(bridge, _ => { });

        SteamGameState state = tracker.Resolve(Manifest());

        Assert.AreEqual(SteamGameState.Idle, state);
    }

    [TestMethod]
    public void Resolve_UninstalledFlag_ReturnsUninstalled()
    {
        var bridge = new FakeBridge();
        var tracker = new SteamProcessTracker(bridge, _ => { });

        SteamGameState state = tracker.Resolve(Manifest(flags: SteamAppState.Uninstalled));

        Assert.AreEqual(SteamGameState.Uninstalled, state);
    }

    [TestMethod]
    public void Resolve_NoExecutableFact_FoldsOnTheFlagsAlone()
    {
        // An empty executable fact means no process match is possible, so the
        // fold is flag-driven: installed reads as Idle, not Running.
        var bridge = new FakeBridge { Running = { "anything" } };
        var tracker = new SteamProcessTracker(bridge, _ => { });

        SteamGameState state = tracker.Resolve(Manifest(exec: ""));

        Assert.AreEqual(SteamGameState.Idle, state);
    }

    [TestMethod]
    public void BuildLaunchUri_ProducesTheDigitsOnlyShape()
    {
        string uri = SteamProcessTracker.BuildLaunchUri(730);

        Assert.AreEqual("steam://run/730", uri);
        Assert.IsTrue(Regex.IsMatch(uri, @"^steam://run/\d+$"), "the URI must be steam://run/ followed by digits only");
    }

    [TestMethod]
    public void BuildLaunchUri_ZeroAppId_StillDigitsOnly()
    {
        string uri = SteamProcessTracker.BuildLaunchUri(0);

        Assert.AreEqual("steam://run/0", uri);
        Assert.IsTrue(Regex.IsMatch(uri, @"^steam://run/\d+$"));
    }

    [TestMethod]
    public void Launch_RoutesTheUriThroughTheSink()
    {
        var bridge = new FakeBridge();
        List<string> launched = [];
        var tracker = new SteamProcessTracker(bridge, launched.Add);

        tracker.Launch(730);

        CollectionAssert.AreEqual(new[] { "steam://run/730" }, launched);
    }

    [TestMethod]
    public void TryClose_DelegatesToTheBridgeWithTheThreeSecondBudget()
    {
        var bridge = new FakeBridge { CloseResult = true };
        var tracker = new SteamProcessTracker(bridge, _ => { });

        bool verdict = tracker.TryClose(Manifest());

        Assert.IsTrue(verdict);
        CollectionAssert.AreEqual(new[] { ("game", SteamProcessTracker.CloseBudget) }, bridge.CloseCalls);
        Assert.AreEqual(TimeSpan.FromSeconds(3), SteamProcessTracker.CloseBudget);
    }

    [TestMethod]
    public void TryClose_NoExecutableFact_PassesAnEmptyName()
    {
        var bridge = new FakeBridge { CloseResult = true };
        var tracker = new SteamProcessTracker(bridge, _ => { });

        tracker.TryClose(Manifest(exec: ""));

        CollectionAssert.AreEqual(new[] { ("", SteamProcessTracker.CloseBudget) }, bridge.CloseCalls);
    }
}
