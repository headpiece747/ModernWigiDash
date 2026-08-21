namespace ModernWigiDash.Tests;

/// <summary>The ticker's fallback-seed cadence — pinned without pixels; the
/// widget's Render only asks the policy whether a seed may fire.</summary>
[TestClass]
public class TickerFallbackPolicyTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    [TestMethod]
    public void TryBeginFallback_FirstCall_Allows()
    {
        var clock = new FakeTimeProvider();
        var policy = new TickerFallbackPolicy(() => clock);

        Assert.IsTrue(policy.TryBeginFallback());
    }

    [TestMethod]
    public void TryBeginFallback_WithinWindow_Refuses()
    {
        var clock = new FakeTimeProvider();
        var policy = new TickerFallbackPolicy(() => clock);
        policy.TryBeginFallback();

        clock.Advance(Window - TimeSpan.FromSeconds(1));

        Assert.IsFalse(policy.TryBeginFallback());
    }

    [TestMethod]
    public void TryBeginFallback_AfterWindow_AllowsAgain()
    {
        var clock = new FakeTimeProvider();
        var policy = new TickerFallbackPolicy(() => clock);
        policy.TryBeginFallback();

        clock.Advance(Window);

        Assert.IsTrue(policy.TryBeginFallback());
    }

    [TestMethod]
    public void TryBeginFallback_ClockProvider_ReadsLiveProviderAtCallTime()
    {
        TimeProvider current = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var policy = new TickerFallbackPolicy(() => current);
        Assert.IsTrue(policy.TryBeginFallback());

        // Swapping what the provider returns (the widget's Clock test seam
        // re-points the same captured holder) must be observed at call time:
        // a provider an hour ahead starts a fresh window.
        current = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 13, 0, 0, TimeSpan.Zero));
        Assert.IsTrue(policy.TryBeginFallback());

        // And the window still applies to the CURRENT provider: the fixed
        // 13:00 clock never advances, so a second call refuses.
        Assert.IsFalse(policy.TryBeginFallback());
    }
}
