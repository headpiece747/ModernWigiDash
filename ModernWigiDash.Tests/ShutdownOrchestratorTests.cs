namespace ModernWigiDash.Tests;

/// <summary>The close-sequence policy pinned: every ordered step runs in
/// order, a throwing step is isolated (one log line, the plan continues),
/// and the last resort runs no matter what.</summary>
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
    public void Run_MiddleStepThrows_StepIsolatedRemainingStepsAndLastResortStillRun()
    {
        var order = new List<string>();
        var failures = new List<string>();
        var orchestrator = new ShutdownOrchestrator(new TeardownPlan(
        [
            new TeardownStep("persist", () => order.Add("persist")),
            new TeardownStep("boom", () => throw new InvalidOperationException("a teardown step failed")),
            new TeardownStep("pump", () => order.Add("pump")),
            new TeardownStep("telemetry", () => order.Add("telemetry"))
        ], new TeardownStep("standby", () => order.Add("standby"))), failures.Add);

        orchestrator.Run();

        CollectionAssert.AreEqual(new[] { "persist", "pump", "telemetry", "standby" }, order,
            "the failing step is isolated: the steps after it and the display-standby last resort must still run");
        CollectionAssert.AreEqual(
            new[]
            {
                "[TEARDOWN] plan starting (4 steps + last resort)",
                "[TEARDOWN] step 'boom' failed, continuing: InvalidOperationException: a teardown step failed",
                "[TEARDOWN] plan complete",
            },
            failures,
            "the failing step leaves one line naming itself, bracketed by the plan's start and completion lines");
    }

    [TestMethod]
    public void Run_LastResortThrows_FailureIsLoggedAndDoesNotPropagate()
    {
        var failures = new List<string>();
        var orchestrator = new ShutdownOrchestrator(new TeardownPlan(
        [
            new TeardownStep("persist", () => { })
        ], new TeardownStep("standby", () => throw new ObjectDisposedException("engine"))), failures.Add);

        orchestrator.Run();

        // The BCL's ODE message is multi-line (flattened to one line by
        // FileLog's LogLine rule at the file boundary), so pin the contract:
        // one isolated line, named, carrying the exception's identity.
        Assert.AreEqual(3, failures.Count);
        StringAssert.StartsWith(failures[0], "[TEARDOWN] plan starting (1 steps + last resort)");
        StringAssert.StartsWith(failures[1], "[TEARDOWN] last resort 'standby' failed: ObjectDisposedException:");
        StringAssert.Contains(failures[1], "engine");
        Assert.AreEqual("[TEARDOWN] plan complete", failures[2]);
    }

    [TestMethod]
    public void Run_NoSteps_LastResortStillRuns()
    {
        bool standbyRan = false;

        new ShutdownOrchestrator(new TeardownPlan([], new TeardownStep("standby", () => standbyRan = true))).Run();

        Assert.IsTrue(standbyRan);
    }
}
