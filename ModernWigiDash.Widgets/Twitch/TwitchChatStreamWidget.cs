using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
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
    private CancellationTokenSource? _cts;
    private FeedLoop? _feedLoop;
    private readonly SemaphoreSlim _authActionGate = new(1, 1);
    private volatile ChatStatus _status;
    private volatile string _statusDetail = "";
    private volatile bool _disposed;

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

    /// <summary>
    /// One chat line. The wrapped lines are cached on the message (keyed by the
    /// render font size + width they were computed with) so re-wrap work is
    /// skipped on every frame between font/width changes.
    /// </summary>
    private sealed record ChatMessage(string Username, string Text, SKColor Color)
    {
        public List<string>? WrappedLines { get; set; }
        public float WrapFontSize { get; set; }
        public float WrapWidth { get; set; }
    }

    private static readonly SKColor[] NamePalette =
    {
        new(255, 121, 198), new(189, 147, 249), new(127, 202, 250), new(187, 247, 208),
        new(254, 240, 138), new(253, 186, 116), new(199, 210, 254), new(165, 243, 252)
    };

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
        if (_status == ChatStatus.Connected) StopConnection();
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
        _status = ChatStatus.Connecting;
        _statusDetail = "Connecting…";
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
                _status = ChatStatus.Disconnected;
                _statusDetail = "";
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
        _status = ChatStatus.Disconnected;
        _statusDetail = "";
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

        _status = ChatStatus.Connecting;
        _statusDetail = "Joining #" + channel + "…";
        Context.RequestRender();
    }

    private void SetReconnectingStatus()
    {
        _status = ChatStatus.Disconnected;
        _statusDetail = "Reconnecting…";
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
        if (line.StartsWith("PING", StringComparison.Ordinal))
        {
            var sock = _feedLoop?.Current;
            if (sock != null)
            {
                CancellationToken token = _cts?.Token ?? CancellationToken.None;
                _ = Task.Run(async () =>
                {
                    try { await SendIrcLineAsync(sock, "PONG :tmi.twitch.tv", token); }
                    catch
                    {
                        System.Diagnostics.Debug.WriteLine("Failed to send PONG during shutdown (socket closed/cancelled)");
                        /* socket closed / cancelled during shutdown */
                    }
                }, token);
            }
            return;
        }

        string[] tags = [];
        if (line.StartsWith('@'))
        {
            var tagEnd = line.IndexOf(' ');
            if (tagEnd < 0) return;
            tags = line[1..tagEnd].Split(';');
            line = line[(tagEnd + 1)..];
        }

        var parts = line.Split(' ', 4);
        if (parts.Length < 2) return;
        var command = parts[1];
        var trailing = parts.Length > 3 ? parts[3] : "";

        switch (command)
        {
            case "ROOMSTATE":
                _status = ChatStatus.Connected;
                _statusDetail = "LIVE";
                Context.RequestRender();
                break;
            case "PRIVMSG":
                HandlePrivmsg(tags, trailing);
                break;
            case "NOTICE":
                {
                    var msg = trailing.TrimStart(':');
                    if (msg.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("Invalid NICK", StringComparison.OrdinalIgnoreCase))
                    {
                        _status = ChatStatus.Disconnected;
                        _statusDetail = "Login failed — check token & username";
                        Context.LogError("Twitch login failed: " + msg);
                    }
                    else if (msg.Contains("you are not logged in", StringComparison.OrdinalIgnoreCase))
                    {
                        _status = ChatStatus.Connected;
                        _statusDetail = "LIVE";
                        Context.RequestRender();
                    }
                    else
                    {
                        Context.LogInfo("Twitch notice: " + msg);
                    }
                    break;
                }
        }
    }

    private void HandlePrivmsg(string[] tags, string trailing)
    {
        if (!trailing.StartsWith(':')) return;

        var text = Unescape(trailing[1..]);
        if (text.Length > 400) text = text[..400];

        var displayName = GetTag(tags, "display-name");
        var login = GetTag(tags, "login");
        string username;
        if (displayName.Length > 0) username = displayName;
        else if (login.Length > 0) username = login;
        else username = "user";

        var colorHex = GetTag(tags, "color");
        var color = SKColors.White;
        if (colorHex.StartsWith('#')) SKColor.TryParse(colorHex, out color);
        if (color == SKColors.White) color = PaletteFor(login.Length > 0 ? login : username);

        lock (_messagesLock)
        {
            _messages.Add(new ChatMessage(username, text, color));
            while (_messages.Count > Math.Clamp(MaxMessages, 5, 100)) _messages.RemoveAt(0);
            _renderSnapshot = [.. _messages];
        }
        Context?.RequestRender();
    }

    /// <summary>
    /// Internal test seam: feeds a raw IRC PRIVMSG line through the real
    /// parser (HandleLine -> HandlePrivmsg) so tests exercise GetTag,
    /// Unescape, the color palette and the message clamp.
    /// </summary>
    internal void AddTestChatMessageForTesting(string username, string text)
    {
        HandleLine($":{username}!{username}@{username}.tmi.twitch.tv PRIVMSG #channel :{text}");
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

    private static string GetTag(string[] tags, string key)
    {
        foreach (var t in tags)
        {
            var eq = t.IndexOf('=');
            var k = eq < 0 ? t : t[..eq];
            if (k == key) return eq < 0 ? "" : t[(eq + 1)..];
        }
        return "";
    }

    private static string Unescape(string s) =>
        s.Replace("\\s", " ").Replace("\\:", ";").Replace("\\\\", "\\");

    private static string NormalizeChannel(string channel)
    {
        var c = channel.Trim().TrimStart('#');
        return c.Length == 0 ? "twitch" : c.ToLowerInvariant();
    }

    private static SKColor PaletteFor(string name)
    {
        int hash = 17;
        foreach (var c in name) hash = (hash * 31 + c) & 0x7FFFFFFF;
        return NamePalette[hash % NamePalette.Length];
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var scale = Math.Clamp(Math.Min(bounds.Width / DefaultSize.Width, bounds.Height / DefaultSize.Height), 0.4f, 3f);
        if (float.IsNaN(scale) || scale <= 0) scale = 1f;

        var bg = ColorOf(BackgroundHex, new SKColor(15, 17, 23, 235));
        var headerColor = ColorOf(HeaderColorHex, SKColors.White);
        var msgColor = ColorOf(MessageColorHex, new SKColor(248, 250, 252));

        using var bgPaint = new SKPaint { Color = bg, IsAntialias = true };
        canvas.DrawRoundRect(bounds, 14f * scale, 14f * scale, bgPaint);

        float pad = 12f * scale;
        float baseFontSize = Math.Max(10f, Math.Min(32f, FontSize));
        float titleSize = (baseFontSize + 2f) * scale;
        float statusSize = 13f * scale;

        var badgeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, titleSize);
        var statusFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, statusSize);
        using var badgePaint = new SKPaint { Color = headerColor, IsAntialias = true };

        float top = bounds.Top + pad;
        string channelBadge = "#" + NormalizeChannel(ChannelName).ToUpperInvariant();
        canvas.DrawTextWithFallback(channelBadge, bounds.Left + pad, top + titleSize, badgeFont, badgePaint, SKTextAlign.Left);

        string statusText = TwitchChatPresentation.StatusText(_status, _statusDetail);

        var statusColor = _status == ChatStatus.Connected
            ? new SKColor(0x10, 0xB9, 0x81)
            : SKColors.White;
        using var statusPaint = new SKPaint { Color = statusColor, IsAntialias = true };
        canvas.DrawTextWithFallback(statusText, bounds.Right - pad, top + titleSize, statusFont, statusPaint, SKTextAlign.Right);

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
            using var emptyPaint = new SKPaint { Color = headerColor.WithAlpha(130), IsAntialias = true };
            var hint = TwitchChatPresentation.EmptyHint(_status, AutoConnect);
            canvas.DrawTextWithFallback(hint, contentBounds.Left, contentBounds.Top + msgSize, emptyFont, emptyPaint, SKTextAlign.Left);
            canvas.Restore();
            return;
        }

        float cursor = contentBounds.Bottom;

        var userFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, userSize);
        var msgFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, msgSize);
        using var userPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var msgPaint = new SKPaint { Color = msgColor, IsAntialias = true };

        for (int i = snapshot.Count - 1; i >= 0; i--)
        {
            var m = snapshot[i];
            var lines = m.WrappedLines;
            if (lines is null
                || Math.Abs(m.WrapFontSize - msgSize) > 0.01f
                || Math.Abs(m.WrapWidth - contentBounds.Width) > 0.5f)
            {
                lines = TextRenderHelper.WrapText(m.Text, msgFont, contentBounds.Width);
                m.WrappedLines = lines;
                m.WrapFontSize = msgSize;
                m.WrapWidth = contentBounds.Width;
            }

            float blockH = userLineHeight + lines.Count * lineHeight + 4f * scale;
            cursor -= blockH;
            if (cursor < contentBounds.Top - userLineHeight) break;

            userPaint.Color = m.Color;
            canvas.DrawTextWithFallback(m.Username, contentBounds.Left, cursor + userSize, userFont, userPaint, SKTextAlign.Left);

            float msgY = cursor + userLineHeight;
            for (int li = 0; li < lines.Count; li++)
            {
                canvas.DrawTextWithFallback(lines[li], contentBounds.Left, msgY + (li + 1) * lineHeight - (lineHeight - msgSize) * 0.5f, msgFont, msgPaint, SKTextAlign.Left);
            }
        }

        canvas.Restore();
    }

    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        _feedLoop?.Dispose(); // cancels, aborts the live feed, and awaits the loop task
        _feedLoop = null;
        if (_cts is { } cts) await cts.CancelAsync();
        _cts?.Dispose();
        await base.DisposeAsync();
    }
}
