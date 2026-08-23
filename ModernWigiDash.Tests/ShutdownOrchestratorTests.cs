namespace ModernWigiDash.Tests;

/// <summary>The close-sequence policy — the standby guarantee pinned: every
/// ordered step runs in order, and the last resort runs no matter what.</summary>
[TestClass]
public class ShutdownOrchestratorTests
{
    [TestMethod]
    public void Run_OrderedSteps_RunInOrderThenLastResort()
    {
        var order = new List<string>();
        var orchestrator = new ShutdownOrchestrator(new TeardownPlan(
        [
            new TeardownStep("persist", () => order.Add("persist")),
            new TeardownStep("pump", () => order.Add("pump")),
            new TeardownStep("engine", () => order.Add("engine"))
        ], new TeardownStep("standby", () => order.Add("standby"))));

        orchestrator.Run();

        CollectionAssert.AreEqual(new[] { "persist", "pump", "engine", "standby" }, order);
    }

    [TestMethod]
    public void Run_MiddleStepThrows_LastResortStillRuns()
    {
        var order = new List<string>();
        var orchestrator = new ShutdownOrchestrator(new TeardownPlan(
        [
            new TeardownStep("persist", () => order.Add("persist")),
            new TeardownStep("boom", () => throw new InvalidOperationException("a teardown step failed")),
            new TeardownStep("pump", () => order.Add("pump"))
        ], new TeardownStep("standby", () => order.Add("standby"))));

        Assert.ThrowsExactly<InvalidOperationException>(() => orchestrator.Run());
        CollectionAssert.AreEqual(new[] { "persist", "standby" }, order,
            "the failing step aborts the sequence, but the display-standby last resort must never be skipped");
    }

    [TestMethod]
    public void Run_NoSteps_LastResortStillRuns()
    {
        bool standbyRan = false;

        new ShutdownOrchestrator(new TeardownPlan([], new TeardownStep("standby", () => standbyRan = true))).Run();

        Assert.IsTrue(standbyRan);
    }
}
