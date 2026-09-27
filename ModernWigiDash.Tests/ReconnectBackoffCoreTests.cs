using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

/// <summary>
/// The shared reconnect backoff rule (2026-09-27, the architecture review's
/// "one owner for how long until we retry"). Pure arithmetic over a failure
/// count, so every rule is pinned here without a timer, a clock, or a device —
/// and the three callers (the display engine, the price feeds, the Twitch IRC
/// loop) supply only their own first delay and ceiling.
///
/// The one convention worth stating out loud, because it is the thing the two
/// former spellings disagreed about: <c>DelayFor(n)</c> is the wait BEFORE
/// attempt n+1, so the first failure still waits the first delay and the ladder
/// starts at it. The widget feed loops used to start one rung further along,
/// which meant a policy declared with a 1 s initial delay never once waited a
/// second. These pins are what stops that coming back as a "simplification".
/// </summary>
[TestClass]
public class ReconnectBackoffCoreTests
{
    [TestMethod]
    [DataRow(0, 5)]
    [DataRow(1, 5)]
    [DataRow(2, 10)]
    [DataRow(3, 20)]
    [DataRow(4, 40)]
    public void DelayFor_FirstFailureIsTheFirstDelay_ThenItDoubles(int failures, int expectedSeconds)
    {
        var first = TimeSpan.FromSeconds(5);
        var max = TimeSpan.FromMinutes(2);

        Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), ReconnectBackoff.DelayFor(failures, first, max));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(int.MinValue)]
    public void DelayFor_NoFailuresYet_IsTheFirstDelay(int failures)
    {
        // A fresh owner (0) and a defensively negative count both mean "nothing
        // has failed yet", so neither may produce a zero or negative delay that
        // would spin the loop.
        var first = TimeSpan.FromMilliseconds(20);
        Assert.AreEqual(first, ReconnectBackoff.DelayFor(failures, first, TimeSpan.FromSeconds(30)));
    }

    [TestMethod]
    [DataRow(7)]
    [DataRow(40)]
    [DataRow(int.MaxValue)]
    public void DelayFor_SaturatesAtTheCeiling(int failures)
    {
        // A permanently absent target settles at the ceiling. int.MaxValue is the
        // one that matters: the shift is clamped, so this must not overflow into
        // a negative or absurd delay.
        var max = TimeSpan.FromSeconds(30);
        Assert.AreEqual(max, ReconnectBackoff.DelayFor(failures, TimeSpan.FromSeconds(1), max));
    }

    [TestMethod]
    public void DelayFor_IsMonotonicAndPositive()
    {
        var first = TimeSpan.FromSeconds(5);
        var max = TimeSpan.FromMinutes(2);

        TimeSpan previous = TimeSpan.Zero;
        for (int failures = 0; failures < 40; failures++)
        {
            var delay = ReconnectBackoff.DelayFor(failures, first, max);
            Assert.IsTrue(delay >= previous, $"delay went backwards at {failures} failures: {delay} < {previous}");
            Assert.IsTrue(delay > TimeSpan.Zero, $"a non-positive delay at {failures} failures would spin the loop");
            Assert.IsTrue(delay <= max, $"the delay exceeded the ceiling at {failures} failures");
            previous = delay;
        }
    }

    [TestMethod]
    public void DelayFor_CallersGetTheirOwnNumbers()
    {
        // The rule is shared; the numbers are per-caller configuration. The same
        // failure count must come out differently for a chat socket and a display,
        // which is the whole reason the first delay and the ceiling are parameters
        // and not constants inside the rule.
        var chat = ReconnectBackoff.DelayFor(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        var display = ReconnectBackoff.DelayFor(2, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));

        Assert.AreEqual(TimeSpan.FromSeconds(2), chat);
        Assert.AreEqual(TimeSpan.FromSeconds(10), display);
    }
}
