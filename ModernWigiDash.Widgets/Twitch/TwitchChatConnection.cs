namespace ModernWigiDash.Widgets.Twitch;

/// <summary>One chat line parsed from the IRC stream: plain data; the wrapped
/// lines come from the widget's shared <see cref="WrapCache"/>.</summary>
internal sealed record TwitchChatLine(string Username, string Text, SKColor Color);

/// <summary>
/// The Twitch chat connection module: the IRC endpoint, the anonymous
/// credentials, the four-line handshake, the PONG keepalive with its
/// token-retirement protocol, the FeedLoop reconnect wiring, the
/// <see cref="TwitchChatPresentation.ChatState"/> transition (one owner, one
/// volatile reference; the NOTICE rule from
/// <see cref="TwitchChatStatusPolicy"/>), and the clamped message buffer.
/// The seam is the shared <see cref="IWebSocketFeed"/>: production binds
/// <see cref="ClientWebSocketFeed"/>, tests bind an in-memory fake, so the
/// whole connection is drivable without a network or a widget instance. The
/// widget keeps the property surface, the rendering, and the touch; it drives
/// the module from its properties and reads its <see cref="State"/> and
/// <see cref="Messages"/>.
/// </summary>
internal sealed class TwitchChatConnection : IAsyncDisposable
{
    private static readonly Uri IrcEndpoint = new("wss://irc-ws.chat.twitch.tv:443");
    private const string AnonymousNickPrefix = "justinfan";
    private const string AnonymousPass = "SCHMOOPIIE";

    private readonly Func<IWebSocketFeed> _createFeed;
    private readonly Func<int> _maxMessages;
    private readonly Func<bool>? _continueAfterCycle;
    private readonly Action<string>? _logInfo;
    private readonly Action<string, Exception?>? _logError;
    private readonly Action? _onChanged;
    private readonly Lock _messagesLock = new();
    private readonly List<TwitchChatLine> _messages = new();
    // The render-side snapshot list is replaced wholesale on every mutation
    // (add/trim/clear), so the render thread iterates it without a per-frame
    // _messages.ToArray() allocation under the lock.
    private List<TwitchChatLine> _renderSnapshot = [];
    private CancellationTokenSource? _pongCts;
    private FeedLoop? _loop;

    // Status + detail as ONE volatile reference: the render thread composes
    // them into the status line, and two independent volatiles could tear
    // (new status with the previous detail for one frame). The payload type
    // and its one-spelling factories live in TwitchChatPresentation.
    private volatile TwitchChatPresentation.ChatState _state = TwitchChatPresentation.ChatState.Disconnected();

    /// <summary>
    /// Creates the connection. <paramref name="createFeed"/> is the feed seam
    /// (tests inject an in-memory feed); <paramref name="maxMessages"/> is the
    /// live buffer bound read on every append (the widget's MaxMessages
    /// property); <paramref name="continueAfterCycle"/> vetoes the reconnect
    /// (the widget's AutoConnect + disposed fact); the log and change hooks are
    /// the host sinks (log through the context, repaint on state/buffer
    /// change).
    /// </summary>
    public TwitchChatConnection(
        Func<IWebSocketFeed> createFeed,
        Func<int> maxMessages,
        Func<bool>? continueAfterCycle = null,
        Action<string>? logInfo = null,
        Action<string, Exception?>? logError = null,
        Action? onChanged = null)
    {
        _createFeed = createFeed;
        _maxMessages = maxMessages;
        _continueAfterCycle = continueAfterCycle;
        _logInfo = logInfo;
        _logError = logError;
        _onChanged = onChanged;
    }

    /// <summary>The current connection state: the module's single truth
    /// (the widget's touch toggle and the header status line both read it).</summary>
    public TwitchChatPresentation.ChatState State => _state;

    /// <summary>The render snapshot of the chat buffer: replaced wholesale on
    /// every mutation, so the render thread reads it without a lock.</summary>
    public IReadOnlyList<TwitchChatLine> Messages
    {
        get { lock (_messagesLock) return _renderSnapshot; }
    }

    /// <summary>How many chat lines the buffer currently holds.</summary>
    public int MessageCount
    {
        get { lock (_messagesLock) return _messages.Count; }
    }

    /// <summary>
    /// Starts (or restarts) the connection to the given channel: clears the
    /// buffer, moves to the Connecting state, and starts the FeedLoop
    /// (connect to the handshake to read messages to exponential-backoff
    /// reconnect, driven through the feed seam). A restart with a new channel
    /// retires the old loop and the in-flight PONG token first.
    /// </summary>
    public void Start(string channelRaw)
    {
        string channel = TwitchIrcMessages.NormalizeChannel(channelRaw);
        RetirePongToken();
        // Retire the old loop before clearing the buffer: a still-running
        // loop is appending the old channel's messages, and clearing first
        // would let a late append land in the fresh buffer for the new
        // channel. The new PONG token is created after the old loop is
        // disposed so its unwinding cannot observe the new token.
        _loop?.Dispose();
        _loop = null;
        _pongCts = new CancellationTokenSource();
        lock (_messagesLock)
        {
            _messages.Clear();
            _renderSnapshot = [];
        }
        SetState(TwitchChatPresentation.ChatState.Connecting());

        // The IRC loop is a FeedLoop: connect to handshake to read messages
        // to exponential backoff reconnect, driven through the feed seam.
        _loop = new FeedLoop(
            IrcEndpoint,
            _createFeed,
            (feed, ct) => ConnectIrcAsync(feed, channel, ct),
            DispatchIncomingMessage,
            new ExponentialBackoffReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)),
            onCycleEnded: _ => SetState(TwitchChatPresentation.ChatState.Reconnecting()),
            onStopped: () => SetState(TwitchChatPresentation.ChatState.Disconnected()),
            continueAfterCycle: _continueAfterCycle,
            onError: ex => _logError?.Invoke("Twitch IRC error", ex));
        _loop.Start();
    }

    /// <summary>Stops the connection: disposes the loop, retires the PONG
    /// token, and moves to the disconnected state (no reconnect follows).</summary>
    public void Stop()
    {
        _loop?.Dispose();
        _loop = null;
        RetirePongToken();
        SetState(TwitchChatPresentation.ChatState.Disconnected());
    }

    /// <summary>Trims the buffer to the current (clamped) bound and replaces
    /// the render snapshot: the property-change trim path (an inspector
    /// MaxMessages write).</summary>
    public void TrimMessages()
    {
        int cap = TwitchChatStatusPolicy.ClampMaxMessages(_maxMessages());
        lock (_messagesLock)
        {
            while (_messages.Count > cap) _messages.RemoveAt(0);
            _renderSnapshot = [.. _messages];
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _loop?.Dispose(); // cancels, aborts the live feed, and awaits the loop task
        _loop = null;
        // The terminal PONG-token retirement: the feed loop (the only PONG
        // launcher) unwound above, so no in-flight holder remains and the
        // graceful cancel + dispose is safe here, unlike RetirePongToken's
        // alive-phase deferral.
        if (_pongCts is { } cts) await cts.CancelAsync().ConfigureAwait(false);
        _pongCts?.Dispose();
    }

    /// <summary>
    /// Runs after the feed connected: the anonymous IRC handshake and the
    /// "Joining #channel" status.
    /// </summary>
    private async Task ConnectIrcAsync(IWebSocketFeed feed, string channel, CancellationToken ct)
    {
        string nick = AnonymousNickPrefix + Random.Shared.Next(1000000, 9999999).ToString(CultureInfo.InvariantCulture);
        string pass = AnonymousPass;

        await SendIrcLineAsync(feed, "CAP REQ :twitch.tv/commands twitch.tv/tags", ct).ConfigureAwait(false);
        await SendIrcLineAsync(feed, "PASS " + pass, ct).ConfigureAwait(false);
        await SendIrcLineAsync(feed, "NICK " + nick, ct).ConfigureAwait(false);
        await SendIrcLineAsync(feed, "JOIN #" + channel, ct).ConfigureAwait(false);

        SetState(TwitchChatPresentation.ChatState.JoiningChannel(channel));
    }

    private static Task SendIrcLineAsync(IWebSocketFeed socket, string line, CancellationToken ct)
        => socket.SendTextAsync(line + "\r\n", ct);

    private void DispatchIncomingMessage(string data)
    {
        foreach (var rawLine in data.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            HandleLine(rawLine);
        }
    }

    private void HandleLine(string line)
    {
        if (!TwitchIrcMessages.TryParse(line, out var message)) return;

        switch (message.Kind)
        {
            case IrcMessageKind.Ping:
                {
                    var sock = _loop?.Current;
                    if (sock != null)
                    {
                        CancellationToken token = _pongCts?.Token ?? CancellationToken.None;
                        _ = Task.Run(async () =>
                        {
                            try { await SendIrcLineAsync(sock, "PONG :" + message.PingPayload, token).ConfigureAwait(false); }
                            catch
                            {
                                System.Diagnostics.Debug.WriteLine("Failed to send PONG during shutdown (socket closed/cancelled)");
                            }
                        }, token);
                    }
                    break;
                }
            case IrcMessageKind.RoomState:
                SetState(TwitchChatPresentation.ChatState.Live());
                break;
            case IrcMessageKind.Privmsg:
                HandlePrivmsg(message);
                break;
            case IrcMessageKind.Notice:
                {
                    var (newStatus, changed) = TwitchChatStatusPolicy.StatusFromNotice(message.Text, _state.Status);
                    if (changed)
                    {
                        // The error line lands BEFORE the state publish: the
                        // state observation is the sync point (a test or the
                        // widget sees the transition through the changed bag
                        // and then expects the log entry), so a loop-thread
                        // preemption between the two would drop the log from
                        // the observer's view. Same-thread program order +
                        // the queue's thread safety makes log-then-state the
                        // only order in which the observation is safe.
                        if (newStatus != ChatStatus.Connected) _logError?.Invoke("Twitch login failed: " + message.Text, null);
                        SetState(newStatus == ChatStatus.Connected ? TwitchChatPresentation.ChatState.Live() : TwitchChatPresentation.ChatState.LoginFailed());
                    }
                    else
                    {
                        _logInfo?.Invoke("Twitch notice: " + message.Text);
                    }
                    break;
                }
            case IrcMessageKind.Other:
                break;
        }
    }

    private void HandlePrivmsg(IrcMessage message)
    {
        var colorHex = message.ColorHex;
        var color = colorHex.StartsWith('#') && SKColor.TryParse(colorHex, out var parsed) ? parsed : SKColors.White;
        if (color == SKColors.White) color = TwitchIrcMessages.PaletteColorFor(message.Login.Length > 0 ? message.Login : message.Username);

        lock (_messagesLock)
        {
            _messages.Add(new TwitchChatLine(message.Username, message.Text, color));
            while (_messages.Count > TwitchChatStatusPolicy.ClampMaxMessages(_maxMessages())) _messages.RemoveAt(0);
            _renderSnapshot = [.. _messages];
        }
        _onChanged?.Invoke();
    }

    /// <summary>
    /// The alive-phase PONG-token retirement: cancel and drop the reference,
    /// do NOT dispose. An in-flight PONG task may still hold the token (the
    /// fire-and-forget task at the Ping dispatch passes it to the socket
    /// send), and disposing a source with live holders is what the deferral
    /// the Sdk's PollLoop/FrameDelivery and the feed manager apply avoids;
    /// the dropped source is GC'd when its last holder unwinds. The terminal
    /// variant (DisposeAsync) is the only site that disposes: by then the
    /// feed loop, the only PONG launcher, has unwound, so no holder remains.
    /// </summary>
    private void RetirePongToken()
    {
        _pongCts?.Cancel();
        _pongCts = null;
    }

    private void SetState(TwitchChatPresentation.ChatState state)
    {
        _state = state;
        _onChanged?.Invoke();
    }
}
