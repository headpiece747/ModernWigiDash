namespace ModernWigiDash.Tests;

/// <summary>
/// Async polling helper for tests: waits until <paramref name="condition"/> holds
/// or <paramref name="timeout"/> elapses, then asserts the condition. Replaces
/// Thread.Sleep-based polling loops (S2925) with Task.Delay-based async waits so
/// the test thread is released instead of blocked.
/// </summary>
internal static class TestWait
{
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        bool met = condition();
        while (!met && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5).ConfigureAwait(false);
            met = condition();
        }

        // Assert the last evaluation result — never re-evaluate: conditions may
        // be stateful (e.g. a poll that acquires a pooled buffer on success).
        Assert.IsTrue(met, $"Condition was not met within {timeout}.");
    }
}
