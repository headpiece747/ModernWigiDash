namespace ModernWigiDash.App;

/// <summary>
/// The window's close-sequence policy (App): an ordered list of teardown
/// steps plus one never-skip last resort — the display-standby guarantee.
/// The Closed handler used to embed the ordering inline, where the rule
/// "the engine dispose must never be skipped, even when an earlier step
/// throws" had no test surface. The orchestrator makes the invariant
/// assertable: every ordered step runs in order, and the last resort runs
/// no matter what.
/// </summary>
internal sealed class ShutdownOrchestrator(Action[] orderedSteps, Action lastResort)
{
    public void Run()
    {
        try
        {
            foreach (Action step in orderedSteps)
            {
                step();
            }
        }
        finally
        {
            lastResort();
        }
    }
}
