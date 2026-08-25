namespace ModernWigiDash.Widgets.Twitch;

[WidgetMetadata("twitch_chat", "Twitch", Category = "Social & Visual", DefaultGridSize = GridSizePreset.Size2x4)]
public class TwitchChatStreamWidget : ModernWidgetBase, IWidgetActionInvoker, IWidgetPropertyOptionsProvider, IWidgetActionPresentationProvider
{
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

    [WidgetProperty("Header Color", WidgetPropertyType.Color, "Channel header text color", "#F59E0B")]
    public string HeaderColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Message Color", WidgetPropertyType.Color, "Chat message text color", "#F8FAFC")]
    public string MessageColorHex { get; set; } = "#F8FAFC";

    [WidgetProperty("Background Color", WidgetPropertyType.Color, "Widget background color", "#0F1117")]
    public string BackgroundHex { get; set; } = "#0F1117";

    [WidgetProperty("Font Size", WidgetPropertyType.Number, "Chat text font size in points", 24)]
    public int FontSize { get; set; } = 24;

    [WidgetProperty("Max Messages", WidgetPropertyType.Number, "Number of chat messages to keep on screen", 30)]
    public int MaxMessages { get; set; } = 30;

    private readonly WrapCache _wrapCache = new();
    private readonly SemaphoreSlim _authActionGate = new(1, 1);
    private volatile bool _disposed;
    // The chat connection module: the IRC endpoint, the anonymous credentials,
    // the handshake, the PONG keepalive, the reconnect loop, the ChatState
    // transition, and the message buffer all live there behind the
    // IWebSocketFeed seam. The widget keeps the property surface, the
    // rendering, and the touch, and drives the module from its properties.
    private readonly TwitchChatConnection _connection;

    // Hoisted paints: every color is computed per render (theme/status-driven),
    // so each paint is one field reused via Color mutation - the 30 FPS render
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
    /// feed so the connection module is drivable without a network.
    /// </summary>
    internal Func<IWebSocketFeed> FeedFactory { get; set; } = () => new ClientWebSocketFeed();

    /// <summary>
    /// The Twitch session (one process-wide singleton by default). Test seam in
    /// the <see cref="FeedFactory"/> image: InitializeAsync restores the session
    /// fire-and-forget, and the shared session's real token store + client must
    /// never be reached from a test host - a valid stored token would perform a
    /// real network restore and mutate the singleton's followed-channel state.
    /// </summary>
    internal TwitchSession Session { get; set; } = TwitchSession.Shared;

    public TwitchChatStreamWidget()
    {
        // The live reads (MaxMessages, AutoConnect, Context) keep the module bound
        // to the widget's current facts: a test's post-ctor FeedFactory swap
        // and an inspector write both take effect.
        _connection = new TwitchChatConnection(
            () => FeedFactory(),
            () => MaxMessages,
            () => AutoConnect && !_disposed,
            msg => Context?.LogInfo(msg),
            (msg, ex) => Context?.LogError(msg, ex),
            () => Context?.RequestRender());
    }

    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        if (AutoConnect) _connection.Start(ChannelName);
        _ = RestoreTwitchSessionAsync(cancellationToken);
    }

    public void InvokeWidgetAction(string propertyName)
    {
        if (propertyName is nameof(LoginWithTwitch) or nameof(RefreshLiveChannels) or nameof(LogoutTwitch))
            _ = RunTwitchActionAsync(propertyName);
    }

    public string? GetWidgetActionLabel(string propertyName)
        => string.Equals(propertyName, nameof(LoginWithTwitch), StringComparison.Ordinal) && Session.IsAuthenticated
            ? "Twitch logged in"
            : null;

    public bool IsWidgetActionActive(string propertyName)
        => string.Equals(propertyName, nameof(LoginWithTwitch), StringComparison.Ordinal) && Session.IsAuthenticated;

    public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
    {
        if (!string.Equals(propertyName, nameof(ChannelName), StringComparison.Ordinal)) return [];

        return Session.FollowedChannels
            .Select(channel => new WidgetPropertyOption(channel.Login, channel.DisplayLabel))
            .ToArray();
    }

    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        switch (propertyName)
        {
            case nameof(ChannelName):
                if (AutoConnect) _connection.Start(ChannelName);
                break;
            case nameof(AutoConnect):
                if (newValue is true) _connection.Start(ChannelName);
                else _connection.Stop();
                break;
            case nameof(MaxMessages):
                _connection.TrimMessages();
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
        if (_connection.State.Status == ChatStatus.Connected) _connection.Stop();
        else _connection.Start(ChannelName);
    }

    /// <summary>Internal test accessor: how many chat messages the live IRC
    /// loop has parsed (forwards the module's count through the
    /// <see cref="FeedFactory"/> seam).</summary>
    internal int MessageCountForTest => _connection.MessageCount;

    // The header strings are memoized per input (the shared MemoSlot shape):
    // Render composes the badge and status line every frame, but both change
    // only via the inspector or the IRC loop. Single-slot memos keyed by the
    // source value, so the per-frame path allocates nothing for the static
    // header.
    private readonly MemoSlot<string, string> _badgeMemo = new();
    private readonly MemoSlot<(ChatStatus Status, string Detail), string> _statusMemo = new();

    private string ChannelBadge()
        => _badgeMemo.GetOrCompute(ChannelName, () => "#" + TwitchIrcMessages.NormalizeChannel(ChannelName).ToUpperInvariant());

    private string StatusLine(TwitchChatPresentation.ChatState state)
    {
        return _statusMemo.GetOrCompute(
            (state.Status, state.Detail),
            () => TwitchChatPresentation.StatusText(state));
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var scale = Math.Clamp(Math.Min(bounds.Width / DefaultSize.Width, bounds.Height / DefaultSize.Height), 0.4f, 3f);
        if (float.IsNaN(scale) || scale <= 0) scale = 1f;

        var bg = ColorOf(BackgroundHex, WidgetPalette.ChatBackground);
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

        // One read of the module's state for the whole frame: the status
        // color, the status line, and the empty hint all agree.
        var state = _connection.State;
        string statusText = StatusLine(state);

        _statusPaint.Color = TwitchChatPresentation.StatusColor(state.Status);
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
        // per-frame allocation under a lock is needed.
        var snapshot = _connection.Messages;

        float msgSize = baseFontSize * scale;
        float userSize = (Math.Max(10f, baseFontSize - 2f)) * scale;
        float lineHeight = msgSize * 1.4f;
        float userLineHeight = userSize * 1.35f;

        if (snapshot.Count == 0)
        {
            var emptyFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, msgSize);
            _emptyPaint.Color = headerColor.WithAlpha(130);
            var hint = TwitchChatPresentation.EmptyHint(state.Status, AutoConnect);
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
        await _connection.DisposeAsync().ConfigureAwait(false); // cancels the loop, aborts the feed, retires the PONG token
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
