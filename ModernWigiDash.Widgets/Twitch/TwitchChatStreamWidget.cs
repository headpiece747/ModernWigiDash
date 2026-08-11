using SkiaSharp;
using ModernWigiDash.Sdk;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets.Twitch;

[WidgetMetadata("twitch_chat", "Twitch", Category = "Social & Visual")]
public class TwitchChatStreamWidget : ModernWidgetBase, IWidgetActionInvoker, IWidgetPropertyOptionsProvider, IWidgetActionPresentationProvider
{
    private const string AnonymousNickPrefix = "justinfan";
    private const string AnonymousPass = "SCHMOOPIIE";
    private static readonly Uri IrcEndpoint = new("wss://irc-ws.chat.twitch.tv:443");
    /// <summary>The maximum Twitch channel-name length (Twitch's 25-char cap).</summary>
    private const int MaxChannelNameLength = 25;

    public override SKSize DefaultSize => GridSizePreset.Size2x4.ToSize();

    [WidgetProperty("Channel Name", WidgetPropertyType.Choice, "Select a followed channel after Twitch login, or type a channel manually.", "twitch")]
    public string ChannelName { get; set; } = "twitch";

    [WidgetProperty("Twitch Client ID", WidgetPropertyType.Text, "Public Twitch application ID. This is not a user token or secret.", "")]
    public string TwitchClientId { get; set; } = "";

    [WidgetProperty("Log in with Twitch", WidgetPropertyType.Button, "Authorize followed-channel access in your browser")]
    public string LoginWithTwitch { get; set; } = "";

    [WidgetProperty("Refresh live channels", WidgetPropertyType.Button, "Reload followed channels that are currently live")]
    public string RefreshLiveChannels { get; set; } = "";

    [WidgetProperty("Log out of Twitch", WidgetPropertyType.Button, "Remove the locally stored Twitch authorization")]
    public string LogoutTwitch { get; set; } = "";

    [WidgetProperty("Auto Connect", WidgetPropertyType.Boolean, "Connect automatically when the widget loads", true)]
    public bool AutoConnect { get; set; } = true;

    [WidgetProperty("Header Color", WidgetPropertyType.Color, "Channel header text color", "#FFFFFF")]
    public string HeaderColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Message Color", WidgetPropertyType.Color, "Chat message text color", "#F8FAFC")]
    public string MessageColorHex { get; set; } = "#F8FAFC";

    [WidgetProperty("Background Color", WidgetPropertyType.Color, "Widget background color", "#0F1117")]
    public string BackgroundHex { get; set; } = "#0F1117";

    [WidgetProperty("Font Size", WidgetPropertyType.Number, "Chat text font size in points", 24)]
    public int FontSize { get; set; } = 24;

    [WidgetProperty("Max Messages", WidgetPropertyType.Number, "Number of chat messages to keep on screen", 30)]
    public int MaxMessages { get; set; } = 30;

    private readonly Lock _messagesLock = new();
    private readonly List<ChatMessage> _messages = new();
    // The render-side snapshot list is replaced wholesale on every mutation
    // (add/trim/clear), so the render thread iterates it without a per-frame
    // _messages.ToArray() allocation under the lock.
    private List<ChatMessage> _renderSnapshot = [];
    private readonly WrapCache _wrapCache = new();
    private CancellationTokenSource? _cts;
    private FeedLoop? _feedLoop;
    private readonly SemaphoreSlim _authActionGate = new(1, 1);
    // Status + detail as ONE volatile reference: the render thread composes
    // them into the status line, and two independent volatiles could tear
    // (new status with the previous detail for one frame).
    private sealed record ChatState(ChatStatus Status, string Detail);
    private volatile ChatState _chatState = new(ChatStatus.Disconnected, "");
    private volatile bool _disposed;

    // Hoisted paints: every color is computed per render (theme/status-driven),
    // so each paint is one field reused via Color mutation — the 30 FPS render
    // allocates no SKPaint.
    private readonly SKPaint _bgPaint = new() { IsAntialias = true };
    private readonly SKPaint _badgePaint = new() { IsAntialias = true };
    private readonly SKPaint _statusPaint = new() { IsAntialias = true };
    private readonly SKPaint _emptyPaint = new() { IsAntialias = true };
    private readonly SKPaint _userPaint = new() { Color = SKColors.White, IsAntialias = true };
    private readonly SKPaint _msgPaint = new() { IsAntialias = true };

    /// <summary>
    /// Test seam for the IRC socket. Defaults to the shared
    /// <see cref="ClientWebSocketFeed"/> adapter; tests inject an in-memory
    /// feed so the IRC loop (handshake, reconnect backoff, message parsing) is
    /// drivable without a network.
    /// </summary>
    internal Func<IWebSocketFeed> FeedFactory { get; set; } = () => new ClientWebSocketFeed();

    /// <summary>
    /// The Twitch session (one process-wide singleton by default). Test seam in
    /// the <see cref="FeedFactory"/> image: InitializeAsync restores the session
    /// fire-and-forget, and the shared session's real token store + client must
    /// never be reached from a test host — a valid stored token would perform a
    /// real network restore and mutate the singleton's followed-channel state.
    /// </summary>
    internal TwitchSession Session { get; set; } = TwitchSession.Shared;

    /// <summary>One chat line — plain data; the wrapped lines come from the
    /// widget's shared <see cref="WrapCache"/>.</summary>
    private sealed record ChatMessage(string Username, string Text, SKColor Color);

    public override ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(context, cancellationToken);
        if (AutoConnect) StartConnection();
        _ = RestoreTwitchSessionAsync(cancellationToken);
        return ValueTask.CompletedTask;
    }

    public void InvokeWidgetAction(string propertyName)
    {
        if (propertyName is nameof(LoginWithTwitch) or nameof(RefreshLiveChannels) or nameof(LogoutTwitch))
            _ = RunTwitchActionAsync(propertyName);
    }

    public string? GetWidgetActionLabel(string propertyName)
        => propertyName == nameof(LoginWithTwitch) && Session.IsAuthenticated
            ? "Twitch logged in"
            : null;

    public bool IsWidgetActionActive(string propertyName)
        => propertyName == nameof(LoginWithTwitch) && Session.IsAuthenticated;

    public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
    {
        if (propertyName != nameof(ChannelName)) return [];

        return Session.FollowedChannels
            .Select(channel => new WidgetPropertyOption(channel.Login, channel.DisplayLabel))
            .ToArray();
    }

    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        switch (propertyName)
        {
            case nameof(ChannelName):
                if (AutoConnect) StartConnection();
                break;
            case nameof(AutoConnect):
                if (newValue is true) StartConnection();
                else StopConnection();
                break;
            case nameof(MaxMessages):
                lock (_messagesLock)
                {
                    while (_messages.Count > Math.Clamp(MaxMessages, 5, 100)) _messages.RemoveAt(0);
                    _renderSnapshot = [.. _messages];
                }
                break;
        }
        base.OnPropertyChanged(propertyName, newValue);
    }

    private async Task RestoreTwitchSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Session.RestoreAsync(TwitchClientId, Context, cancellationToken).ConfigureAwait(false);
            Context.RequestInspectorRefresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine("Twitch session restore cancelled during shutdown");
        }
        catch (Exception ex)
        {
            Context.LogError("Unable to restore the Twitch login", ex);
        }
    }

    private async Task RunTwitchActionAsync(string propertyName)
    {
        // Zero-timeout try-acquire: returns immediately, so there is no wait to
        // cancel; the IRC/_authActionGate tokens are unrelated to this gate.
        if (!await _authActionGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) return;

        try
        {
            switch (propertyName)
            {
                case nameof(LoginWithTwitch):
                    await Session.LoginAsync(TwitchClientId, Context, CancellationToken.None).ConfigureAwait(false);
                    break;
                case nameof(RefreshLiveChannels):
                    await Session.RefreshFollowedChannelsAsync(TwitchClientId, Context, CancellationToken.None).ConfigureAwait(false);
                    break;
                case nameof(LogoutTwitch):
                    await Session.LogoutAsync(CancellationToken.None).ConfigureAwait(false);
                    break;
            }

            Context.RequestInspectorRefresh();
            Context.RequestRender();
        }
        catch (Exception ex)
        {
            Context.LogError("Twitch action failed", ex);
        }
        finally
        {
            _authActionGate.Release();
        }
    }

    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType != TouchEventType.TouchUp) return;
        if (_chatState.Status == ChatStatus.Connected) StopConnection();
        else StartConnection();
    }

    private void StartConnection()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource(); // PONG token
        lock (_messagesLock)
        {
            _messages.Clear();
            _renderSnapshot = [];
        }
        _chatState = new(ChatStatus.Connecting, "Connecting…");
        Context.RequestRender();

        // The IRC loop is a FeedLoop: connect → handshake → read messages →
        // exponential backoff reconnect, driven through the feed seam.
        _feedLoop?.Dispose();
        _feedLoop = new FeedLoop(
            IrcEndpoint,
            FeedFactory,
            ConnectIrcAsync,
            DispatchIncomingMessage,
            new ExponentialBackoffReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)),
            onCycleEnded: _ => SetReconnectingStatus(),
            onStopped: () =>
            {
                _chatState = new(ChatStatus.Disconnected, "");
                Context.RequestRender();
            },
            continueAfterCycle: () => AutoConnect && !_disposed,
            onError: ex => Context.LogError("Twitch IRC error", ex));
        _feedLoop.Start();
    }

    private void StopConnection()
    {
        _feedLoop?.Dispose();
        _feedLoop = null;
        // Cancel and drop the PONG token rather than disposing it: an
        // in-flight PONG task may still hold it (same deferral the Sdk's
        // PollLoop/FrameDelivery apply). The replaced source is dropped with
        // the object; StartConnection creates a fresh one.
        _cts?.Cancel();
        _cts = null;
        _chatState = new(ChatStatus.Disconnected, "");
        Context.RequestRender();
    }

    /// <summary>Runs after the feed connected: the anonymous IRC handshake and
    /// the "Joining #channel" status.</summary>
    private async Task ConnectIrcAsync(IWebSocketFeed feed, CancellationToken ct)
    {
        var channel = NormalizeChannel(ChannelName);
        string nick = AnonymousNickPrefix + Random.Shared.Next(1000000, 9999999).ToString();
        string pass = AnonymousPass;

        await SendIrcLineAsync(feed, "CAP REQ :twitch.tv/commands twitch.tv/tags", ct);
        await SendIrcLineAsync(feed, "PASS " + pass, ct);
        await SendIrcLineAsync(feed, "NICK " + nick, ct);
        await SendIrcLineAsync(feed, "JOIN #" + channel, ct);

        _chatState = new(ChatStatus.Connecting, "Joining #" + channel + "…");
        Context.RequestRender();
    }

    private void SetReconnectingStatus()
    {
        _chatState = new(ChatStatus.Disconnected, "Reconnecting…");
        Context.RequestRender();
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
                    var sock = _feedLoop?.Current;
                    if (sock != null)
                    {
                        CancellationToken token = _cts?.Token ?? CancellationToken.None;
                        _ = Task.Run(async () =>
                        {
                            try { await SendIrcLineAsync(sock, "PONG :" + message.PingPayload, token); }
                            catch
                            {
                                System.Diagnostics.Debug.WriteLine("Failed to send PONG during shutdown (socket closed/cancelled)");
                            }
                        }, token);
                    }
                    break;
                }
            case IrcMessageKind.RoomState:
                _chatState = new(ChatStatus.Connected, "LIVE");
                Context.RequestRender();
                break;
            case IrcMessageKind.Privmsg:
                HandlePrivmsg(message);
                break;
            case IrcMessageKind.Notice:
                {
                    var (newStatus, changed) = TwitchChatPresentation.StatusFromNotice(message.Text, _chatState.Status);
                    if (changed)
                    {
                        _chatState = new(newStatus, newStatus == ChatStatus.Connected ? "LIVE" : "Login failed — check token & username");
                        if (newStatus == ChatStatus.Connected) Context.RequestRender();
                        else Context.LogError("Twitch login failed: " + message.Text);
                    }
                    else
                    {
                        Context.LogInfo("Twitch notice: " + message.Text);
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
        var color = SKColors.White;
        if (colorHex.StartsWith('#')) SKColor.TryParse(colorHex, out color);
        if (color == SKColors.White) color = TwitchIrcMessages.PaletteColorFor(message.Login.Length > 0 ? message.Login : message.Username);

        lock (_messagesLock)
        {
            _messages.Add(new ChatMessage(message.Username, message.Text, color));
            while (_messages.Count > Math.Clamp(MaxMessages, 5, 100)) _messages.RemoveAt(0);
            _renderSnapshot = [.. _messages];
        }
        Context?.RequestRender();
    }

    /// <summary>Internal test accessor: how many chat messages the live IRC
    /// loop has parsed (drive the loop through <see cref="FeedFactory"/>).</summary>
    internal int MessageCountForTest
    {
        get
        {
            lock (_messagesLock) return _messages.Count;
        }
    }

    /// <summary>
    /// Normalizes a channel name for IRC JOIN and the header badge: trims,
    /// drops the leading '#', and lowercases. Invalid names — empty,
    /// over-length (Twitch's 25-char cap), or carrying an embedded CR/LF
    /// (which could inject extra IRC lines into the JOIN command) — fall back
    /// to "twitch", the existing empty-channel fallback.
    /// </summary>
    private static string NormalizeChannel(string channel)
    {
        var c = channel.Trim().TrimStart('#');
        if (c.Length == 0) return "twitch";
        if (c.IndexOfAny(['\r', '\n']) >= 0) return "twitch";
        return c.Length > MaxChannelNameLength ? "twitch" : c.ToLowerInvariant();
    }

    // The header strings are memoized per input (the WrapCache shape): Render
    // composes the badge and status line every frame, but both change only via
    // the inspector or the IRC loop. Single-slot caches keyed by the source
    // value, so the per-frame path allocates nothing for the static header.
    private string _badgeChannelKey = "";
    private string _badgeText = "";
    private ChatStatus _statusKey = ChatStatus.Disconnected;
    private string _statusDetailKey = "";
    private string _statusText = "";

    private string ChannelBadge()
    {
        if (ChannelName != _badgeChannelKey)
        {
            _badgeChannelKey = ChannelName;
            _badgeText = "#" + NormalizeChannel(ChannelName).ToUpperInvariant();
        }
        return _badgeText;
    }

    private string StatusLine()
    {
        ChatState state = _chatState;
        ChatStatus status = state.Status;
        string statusDetail = state.Detail;
        if (status != _statusKey || statusDetail != _statusDetailKey)
        {
            _statusKey = status;
            _statusDetailKey = statusDetail;
            _statusText = TwitchChatPresentation.StatusText(status, statusDetail);
        }
        return _statusText;
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var scale = Math.Clamp(Math.Min(bounds.Width / DefaultSize.Width, bounds.Height / DefaultSize.Height), 0.4f, 3f);
        if (float.IsNaN(scale) || scale <= 0) scale = 1f;

        var bg = ColorOf(BackgroundHex, new SKColor(15, 17, 23, 235));
        var headerColor = ColorOf(HeaderColorHex, SKColors.White);
        var msgColor = ColorOf(MessageColorHex, new SKColor(248, 250, 252));

        _bgPaint.Color = bg;
        canvas.DrawRoundRect(bounds, 14f * scale, 14f * scale, _bgPaint);

        float pad = 12f * scale;
        float baseFontSize = Math.Max(10f, Math.Min(32f, FontSize));
        float titleSize = (baseFontSize + 2f) * scale;
        float statusSize = 13f * scale;

        var badgeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, titleSize);
        var statusFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, statusSize);
        _badgePaint.Color = headerColor;

        float top = bounds.Top + pad;
        string channelBadge = ChannelBadge();
        canvas.DrawTextWithFallback(channelBadge, bounds.Left + pad, top + titleSize, badgeFont, _badgePaint, SKTextAlign.Left);

        string statusText = StatusLine();

        _statusPaint.Color = TwitchChatPresentation.StatusColor(_chatState.Status);
        canvas.DrawTextWithFallback(statusText, bounds.Right - pad, top + titleSize, statusFont, _statusPaint, SKTextAlign.Right);

        float headerBottom = top + titleSize + 8f * scale;

        var contentBounds = new SKRect(bounds.Left + pad, headerBottom, bounds.Right - pad, bounds.Bottom - pad);
        if (contentBounds.Width <= 0 || contentBounds.Height <= 0)
        {
            return;
        }

        canvas.Save();
        canvas.ClipRect(contentBounds);

        // The render snapshot is replaced wholesale on every mutation, so no
        // per-frame _messages.ToArray() under the lock is needed.
        var snapshot = _renderSnapshot;

        float msgSize = baseFontSize * scale;
        float userSize = (Math.Max(10f, baseFontSize - 2f)) * scale;
        float lineHeight = msgSize * 1.4f;
        float userLineHeight = userSize * 1.35f;

        if (snapshot.Count == 0)
        {
            var emptyFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, msgSize);
            _emptyPaint.Color = headerColor.WithAlpha(130);
            var hint = TwitchChatPresentation.EmptyHint(_chatState.Status, AutoConnect);
            canvas.DrawTextWithFallback(hint, contentBounds.Left, contentBounds.Top + msgSize, emptyFont, _emptyPaint, SKTextAlign.Left);
            canvas.Restore();
            return;
        }

        float cursor = contentBounds.Bottom;

        var userFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, userSize);
        var msgFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, msgSize);
        _msgPaint.Color = msgColor;

        for (int i = snapshot.Count - 1; i >= 0; i--)
        {
            var m = snapshot[i];
            var lines = _wrapCache.GetOrWrap(m.Text, msgFont, msgSize, contentBounds.Width);

            float blockH = userLineHeight + lines.Count * lineHeight + 4f * scale;
            cursor -= blockH;
            if (cursor < contentBounds.Top - userLineHeight) break;

            _userPaint.Color = m.Color;
            canvas.DrawTextWithFallback(m.Username, contentBounds.Left, cursor + userSize, userFont, _userPaint, SKTextAlign.Left);

            float msgY = cursor + userLineHeight;
            for (int li = 0; li < lines.Count; li++)
            {
                canvas.DrawTextWithFallback(lines[li], contentBounds.Left, msgY + (li + 1) * lineHeight - (lineHeight - msgSize) * 0.5f, msgFont, _msgPaint, SKTextAlign.Left);
            }
        }

        canvas.Restore();
    }

    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        _bgPaint.Dispose();
        _badgePaint.Dispose();
        _statusPaint.Dispose();
        _emptyPaint.Dispose();
        _userPaint.Dispose();
        _msgPaint.Dispose();
        _feedLoop?.Dispose(); // cancels, aborts the live feed, and awaits the loop task
        _feedLoop = null;
        if (_cts is { } cts) await cts.CancelAsync();
        _cts?.Dispose();
        await base.DisposeAsync();
    }
}
