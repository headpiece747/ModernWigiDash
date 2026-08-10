namespace ModernWigiDash.Widgets;

/// <summary>
/// Backoff policy for a <see cref="FeedLoop"/>: how long to wait between
/// cycles. Decided by the loop with the faulted flag, so fixed and exponential
/// policies are testable in one place.
/// </summary>
internal interface IReconnectPolicy
{
    TimeSpan NextDelay(bool faulted);
}

/// <summary>Reconnects after a constant delay regardless of failure history.</summary>
internal sealed class FixedReconnectPolicy(TimeSpan delay) : IReconnectPolicy
{
    public TimeSpan NextDelay(bool faulted) => delay;
}

/// <summary>
/// Reconnects with exponential backoff: a faulted cycle doubles the delay (up
/// to the cap); a healthy cycle resets to the initial delay.
/// </summary>
internal sealed class ExponentialBackoffReconnectPolicy(TimeSpan initial, TimeSpan max) : IReconnectPolicy
{
    private readonly TimeSpan _initial = initial;
    private readonly TimeSpan _max = max;
    private TimeSpan _current = initial;

    public TimeSpan NextDelay(bool faulted)
    {
        if (!faulted)
        {
            _current = _initial;
            return _initial;
        }

        _current = TimeSpan.FromSeconds(Math.Min(_current.TotalSeconds * 2, _max.TotalSeconds));
        return _current;
    }
}

/// <summary>
/// One WebSocket reconnect-loop shape, in the <see cref="PollLoop"/> image:
/// create feed → connect → onConnected → read messages until closed → dispose
/// → backoff delay, repeating until cancelled. The per-consumer differences
/// (URI, feed factory, subscription payload, message parser, status hooks,
/// backoff policy) are delegates — the price feeds and the Twitch IRC loop
/// were two hand-rolled copies of this body.
/// </summary>
internal sealed class FeedLoop : IDisposable
{
    private readonly Uri _uri;
    private readonly Func<IWebSocketFeed> _createFeed;
    private readonly Func<IWebSocketFeed, CancellationToken, Task> _onConnected;
    private readonly Action<string> _onMessage;
    private readonly IReconnectPolicy _reconnect;
    private readonly Action<bool>? _onCycleEnded;
    private readonly Action? _onStopped;
    private readonly Func<bool>? _continueAfterCycle;
    private readonly Action<Exception>? _onError;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private int _disposed;

    private IWebSocketFeed? _current;

    /// <summary>The live feed of the current cycle (null between cycles) — used
    /// by consumers for best-effort out-of-band sends (e.g. incremental
    /// subscriptions, IRC PONG). Written on the loop thread, read from caller
    /// threads, hence the volatile backing field.</summary>
    public IWebSocketFeed? Current
    {
        get => Volatile.Read(ref _current);
        private set => Volatile.Write(ref _current, value);
    }

    public FeedLoop(
        Uri uri,
        Func<IWebSocketFeed> createFeed,
        Func<IWebSocketFeed, CancellationToken, Task> onConnected,
        Action<string> onMessage,
        IReconnectPolicy reconnect,
        Action<bool>? onCycleEnded = null,
        Action? onStopped = null,
        Func<bool>? continueAfterCycle = null,
        Action<Exception>? onError = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _uri = uri;
        _createFeed = createFeed;
        _onConnected = onConnected;
        _onMessage = onMessage;
        _reconnect = reconnect;
        _onCycleEnded = onCycleEnded;
        _onStopped = onStopped;
        _continueAfterCycle = continueAfterCycle;
        _onError = onError;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Starts the loop task. Idempotent: a loop that is already
    /// running (or finished) is never started twice — multi-symbol
    /// subscriptions call this per symbol and must not create duplicate
    /// sockets.</summary>
    public void Start()
    {
        if (_task != null) return;
        _task = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool faulted = false;
                IWebSocketFeed feed = _createFeed();
                Current = feed;
                try
                {
                    await feed.ConnectAsync(_uri, ct);
                    await _onConnected(feed, ct);
                    string? message;
                    while ((message = await feed.ReceiveTextAsync(ct)) is not null)
                    {
                        _onMessage(message);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    faulted = true;
                    _onError?.Invoke(ex);
                }
                finally
                {
                    Current = null;
                    feed.Dispose();
                }

                _onCycleEnded?.Invoke(faulted);
                if (ct.IsCancellationRequested || !(_continueAfterCycle?.Invoke() ?? true)) break;
                await _delay(_reconnect.NextDelay(faulted), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: loop cancelled during shutdown
        }
        finally
        {
            _onStopped?.Invoke();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        Current?.Abort();
        try
        {
            // Bounded wait for the loop task to unwind; the timeout is the
            // cancellation, so opt out of token-based cancellation explicitly.
            // Normally fast: Abort unblocks the in-flight receive immediately.
            _task?.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch
        {
            // Loop task already faulted/cancelled — teardown is best-effort
        }
        _cts.Dispose();
    }
}
