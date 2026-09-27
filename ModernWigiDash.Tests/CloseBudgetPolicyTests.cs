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
        var hid = new DisplayHidTransport();
        var engine = new DisplayDeviceEngine(() => hid);

        Assert.AreEqual(hid.CloseBudgets.StandbyCloseBudget, engine.StandbyCloseBudget);
        Assert.AreEqual(hid.CloseBudgets.DisposeAbandonBudget, engine.DisposeAbandonBudget);
    }

    [TestMethod]
    public void Engine_BeforeAnyTransport_ReadsTheSharedDefaultWithoutConstructingOne()
    {
        // The pre-connect fallback used to be `?? new DisplayHidTransport()`,
        // which built a provider list and a logger on EVERY budget read, on the
        // reconnect path, to ask a value that is constant by construction. The
        // two facts pinned here: the fallback is the SAME policy the transport
        // hands out (no second spelling, so the two cannot drift), and it is
        // reached without a transport.
        using var engine = new DisplayDeviceEngine(() => throw new InvalidOperationException(
            "the budget read must not construct a transport"));

        // No transport has been adopted, so these read the fallback.
        Assert.AreEqual(
            DisplayHidTransport.DefaultCloseBudgets.StandbyCloseBudget,
            engine.StandbyCloseBudget);
        Assert.AreEqual(
            DisplayHidTransport.DefaultCloseBudgets.DisposeAbandonBudget,
            engine.DisposeAbandonBudget);
    }

    [TestMethod]
    public void Engine_BudgetRead_AllocatesNothingOnTheReconnectPath()
    {
        // The direct proof for the allocating fallback: read each budget many
        // times and measure the thread's allocation delta (the per-thread counter
        // the frame-pipeline allocation budget already uses, for the same
        // reason - the process-wide counter quantizes sub-8KB allocations).
        // The old `?? new DisplayHidTransport()` built a provider list and a
        // logger per read; this is on the reconnect path, inside the module
        // whose whole job is never stalling on close.
        using var engine = new DisplayDeviceEngine();

        // Warm the JIT and the statics so the measured loop is the steady state.
        for (int i = 0; i < 32; i++)
        {
            _ = engine.StandbyCloseBudget;
            _ = engine.DisposeAbandonBudget;
        }

        const int reads = 512;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < reads; i++)
        {
            _ = engine.StandbyCloseBudget;
            _ = engine.DisposeAbandonBudget;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0, allocated,
            $"{reads * 2} budget reads allocated {allocated} bytes; the close budgets are constants, so reading them must not construct a transport");
    }

    [TestMethod]
    public void TheHidTransportsPolicyIsTheSharedDefaultDerivedFromTheStaticBound()
    {
        // One derivation, one value: the instance, the static default, and a
        // FRESH derivation from the static bound must all agree — otherwise
        // "the default" means two different things. Compared against a fresh
        // Create(CloseBound), not against itself, which would prove nothing.
        using var transport = new DisplayHidTransport();
        CloseBudgetPolicy derived = CloseBudgetPolicy.Create(DisplayHidTransport.CloseBound);

        Assert.AreEqual(derived, transport.CloseBudgets);
        Assert.AreEqual(derived, DisplayHidTransport.DefaultCloseBudgets);
        Assert.AreEqual(derived.StandbyCloseBudget, transport.CloseBudgets.StandbyCloseBudget);
        Assert.AreEqual(derived.DisposeAbandonBudget, transport.CloseBudgets.DisposeAbandonBudget);
    }

    [TestMethod]
    public void FakeTransport_CarriesItsOwnBound()
    {
        // A fake transport carries its own bound; the engine reads it from
        // whatever adapter it holds. The test injects the transport directly
        // through the test seam so the budget reads are deterministic.
        var fake = new FakeTransport
        {
            CloseBudgets = CloseBudgetPolicy.Create(TimeSpan.FromSeconds(10))
        };
        var engine = new DisplayDeviceEngine(() => fake);

        // Use reflection to set the private _transport field directly
        var field = typeof(DisplayDeviceEngine).GetField("_transport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(engine, fake);

        Assert.AreEqual(fake.CloseBudgets.StandbyCloseBudget, engine.StandbyCloseBudget,
            "the engine must read the fake's standby budget");
        Assert.AreEqual(fake.CloseBudgets.DisposeAbandonBudget, engine.DisposeAbandonBudget,
            "the engine must read the fake's dispose budget");
    }
}
