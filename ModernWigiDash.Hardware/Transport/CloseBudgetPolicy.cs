using System.Runtime.InteropServices;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The close-budget policy: the transport's worst-case teardown bound and
/// the engine's two bounded close waits, derived together as one value so the
/// never-stall-on-close invariant (waits strictly shorter than the worst
/// case) is a construction fact instead of a cross-file pin. The budgets are
/// capped at their designed values (a healthy close completes in well under
/// a second, so the caps dominate for any realistic bound) and fall back to
/// half the bound once half the bound drops below the cap (the bound below
/// twice the cap), so a lowered pipe timeout can never silently leave a
/// budget at or above the worst case.
/// </summary>
/// <param name="CloseBound">The worst-case time a hung device can hold the
/// transport's teardown (the derivation root).</param>
/// <param name="StandbyCloseBudget">The engine's bounded standby wait on the
/// dispose path.</param>
/// <param name="DisposeAbandonBudget">The engine's bounded dispose-abandon
/// wait for the transport teardown.</param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct CloseBudgetPolicy(
    TimeSpan CloseBound,
    TimeSpan StandbyCloseBudget,
    TimeSpan DisposeAbandonBudget)
{
    private const double StandbyCapSeconds = 2;
    private const double DisposeCapSeconds = 3;
    private const double BoundFraction = 2;

    /// <summary>
    /// Derives the policy from the transport's worst-case teardown bound:
    /// each budget is the smaller of its designed cap and half the bound, so
    /// the budget is strictly shorter than the bound on every input.
    /// </summary>
    public static CloseBudgetPolicy Create(TimeSpan closeBound)
        => new(closeBound,
            Budget(StandbyCapSeconds, closeBound),
            Budget(DisposeCapSeconds, closeBound));

    private static TimeSpan Budget(double capSeconds, TimeSpan closeBound)
        => TimeSpan.FromSeconds(Math.Min(capSeconds, closeBound.TotalSeconds / BoundFraction));
}
