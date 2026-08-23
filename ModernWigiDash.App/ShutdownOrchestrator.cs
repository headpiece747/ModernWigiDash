namespace ModernWigiDash.App;

/// <summary>
/// The window's close-sequence policy (App): runs the teardown plan's ordered
/// steps and its never-skip last resort. The invariant is assertable here:
/// every ordered step runs in order, and the last resort runs no matter what
/// — the engine dispose must never be skipped, even when an earlier step
/// throws.
/// </summary>
internal sealed class ShutdownOrchestrator(TeardownPlan plan)
{
    public void Run()
    {
        try
        {
            foreach (TeardownStep step in plan.OrderedSteps)
            {
                step.Run();
            }
        }
        finally
        {
            plan.LastResort.Run();
        }
    }
}
