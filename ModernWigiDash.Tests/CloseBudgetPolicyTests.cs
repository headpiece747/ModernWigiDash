using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

[TestClass]
public class CloseBudgetPolicyTests
{
    [TestMethod]
    public void Create_GenerousBound_KeepsTheDesignedCaps()
    {
        // The production bound (the backends' ~50s worst case) leaves the
        // budgets at their designed caps: standby 2s, dispose 3s.
        var policy = CloseBudgetPolicy.Create(TimeSpan.FromSeconds(50));

        Assert.AreEqual(TimeSpan.FromSeconds(2), policy.StandbyCloseBudget,
            "a generous bound must leave the standby budget at its cap");
        Assert.AreEqual(TimeSpan.FromSeconds(3), policy.DisposeAbandonBudget,
            "a generous bound must leave the dispose budget at its cap");
    }

    [TestMethod]
    public void Create_BoundBelowCap_BudgetsFallToHalfTheBound()
    {
        // A lowered pipe timeout must not leave the budgets at their caps:
        // each derives to half the bound instead.
        var policy = CloseBudgetPolicy.Create(TimeSpan.FromSeconds(2.5));

        Assert.AreEqual(TimeSpan.FromSeconds(1.25), policy.StandbyCloseBudget,
            "below the cap the standby budget must derive to half the bound");
        Assert.AreEqual(TimeSpan.FromSeconds(1.25), policy.DisposeAbandonBudget,
            "below the cap the dispose budget must derive to half the bound");
    }

    [TestMethod]
    public void Create_BudgetsAreAlwaysStrictlyShorterThanTheBound()
    {
        double[] boundsSeconds = [0.5, 1, 2.5, 3.5, 10, 50, 600];
        foreach (double seconds in boundsSeconds)
        {
            var policy = CloseBudgetPolicy.Create(TimeSpan.FromSeconds(seconds));

            Assert.IsTrue(policy.StandbyCloseBudget < policy.CloseBound,
                $"the standby budget must be strictly shorter than the bound at {seconds}s");
            Assert.IsTrue(policy.DisposeAbandonBudget < policy.CloseBound,
                $"the dispose budget must be strictly shorter than the bound at {seconds}s");
            Assert.IsTrue(policy.StandbyCloseBudget <= TimeSpan.FromSeconds(2),
                "the standby budget must never exceed its cap");
            Assert.IsTrue(policy.DisposeAbandonBudget <= TimeSpan.FromSeconds(3),
                "the dispose budget must never exceed its cap");
        }
    }

    [TestMethod]
    public void Create_NonPositiveBound_ClampsToZeroBudgets()
    {
        // A non-positive worst-case bound is a degenerate input (the
        // transport's derivation is a max of positive pipe timeouts): it
        // clamps to a zero bound with zero budgets, so the engine's close
        // waits are immediate and observable, never a negative TimeSpan
        // that Task.Wait would throw on.
        foreach (var bound in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-5) })
        {
            var policy = CloseBudgetPolicy.Create(bound);

            Assert.AreEqual(TimeSpan.Zero, policy.CloseBound,
                "a non-positive bound must clamp to a zero bound");
            Assert.AreEqual(TimeSpan.Zero, policy.StandbyCloseBudget,
                "a clamped bound must derive a zero standby budget");
            Assert.AreEqual(TimeSpan.Zero, policy.DisposeAbandonBudget,
                "a clamped bound must derive a zero dispose budget");
        }
    }

    [TestMethod]
    public void EngineAndTransport_ReadTheSamePolicyValues()
    {
        // The engine's close waits are the transport's policy values, not a
        // second spelling: one value across the seam.
        Assert.AreEqual(DisplayHidTransport.CloseBudgets.StandbyCloseBudget, DisplayDeviceEngine.StandbyCloseBudget);
        Assert.AreEqual(DisplayHidTransport.CloseBudgets.DisposeAbandonBudget, DisplayDeviceEngine.DisposeAbandonBudget);
    }
}
