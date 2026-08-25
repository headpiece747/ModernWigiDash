namespace ModernWigiDash.App;

/// <summary>
/// The window's close-sequence policy (App): runs the teardown plan's ordered
/// steps and its never-skip last resort. Per-step isolation is the
/// load-bearing policy: a throwing step (the OCE shape the close path
/// documents as expected-and-benign) logs one line and the plan continues,
/// so a long-lived host (the test process) never inherits the modules the
/// aborted tail would have released. The last resort runs no matter what,
/// including when an ordered step threw.
/// </summary>
internal sealed class ShutdownOrchestrator(TeardownPlan plan, Action<string>? log = null)
{
    public void Run()
    {
        // The plan's start and completion are observable: a window whose
        // close never runs the plan (a leaked telemetry loop in a long-lived
        // host) is visible in the display log against the wiring's start
        // lines, instead of as a flake hours later.
        log?.Invoke($"[TEARDOWN] plan starting ({plan.OrderedSteps.Count} steps + last resort)");
        foreach (TeardownStep step in plan.OrderedSteps)
        {
            try
            {
                step.Run();
            }
            catch (Exception ex)
            {
                log?.Invoke($"[TEARDOWN] step '{step.Name}' failed, continuing: {ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            plan.LastResort.Run();
        }
        catch (Exception ex)
        {
            log?.Invoke($"[TEARDOWN] last resort '{plan.LastResort.Name}' failed: {ex.GetType().Name}: {ex.Message}");
        }
        log?.Invoke("[TEARDOWN] plan complete");
    }
}
