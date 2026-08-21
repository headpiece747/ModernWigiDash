namespace ModernWigiDash.Widgets;

/// <summary>
/// The REST poll cycle — the third loop shape beside the Sdk's
/// <see cref="PollLoop"/> (probe + sink on a real-time timer) and the
/// <see cref="FeedLoop"/> (WebSocket session with a reconnect policy):
/// delay-first, per-symbol failure isolation (one bad fetch must not kill
/// the cycle or its siblings — the failure routes through the injected
/// <paramref name="failLog"/> and the cycle continues), and an optional
/// batch-tail hook that runs once per cycle after all symbols have polled
/// (the crypto cycle's CoinGecko fallback rides it). The delay rides an
/// injected delegate so tests drive the cadence with a fake clock instead
/// of waiting real seconds.
/// </summary>
internal static class RestPollLoop
{
    /// <summary>
    /// Runs the cycle until <paramref name="isActive"/> reads false or
    /// <paramref name="token"/> cancels (a cancelled delay ends the loop
    /// normally instead of faulting the stored task — an unobserved task
    /// fault would surface on dispose).
    /// </summary>
    internal static async Task RunAsync(
        TimeSpan interval,
        Func<bool> isActive,
        CancellationToken token,
        IEnumerable<string> subscribed,
        Func<string, Task> pollSymbol,
        Func<TimeSpan, CancellationToken, Task> delay,
        DiagLog failLog,
        Func<Task>? afterBatch = null)
    {
        while (isActive())
        {
            try
            {
                await delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown: end the loop normally instead of faulting the stored task.
                break;
            }

            foreach (var symbol in subscribed)
            {
                try
                {
                    await pollSymbol(symbol).ConfigureAwait(false);
                }
                catch
                {
                    // Individual symbol failure is non-fatal.
                    failLog.Write(() => $"REST poll failed for '{LogSanitizer.Sanitize(symbol)}'; continuing");
                }
            }

            if (afterBatch is not null)
            {
                await afterBatch().ConfigureAwait(false);
            }
        }
    }
}
