namespace ModernWigiDash.Widgets.Twitch;

/// <summary>
/// The Twitch chat stream widget: connects to a channel's IRC chat through
/// the TwitchChatConnection module (this widget keeps the property surface,
/// the rendering, and the touch toggle) and offers the Twitch device-auth
/// actions through the action invoker seam.
/// </summary>
[WidgetMetadata("twitch_chat", "Twitch", Category = "Social & Visual", DefaultGridSize = GridSizePreset.Size2x4)]
public class TwitchChatStreamWidget : ModernWidgetBase, IWidgetActionInvoker, IWidgetPropertyOptionsProvider, IWidgetActionPresentationProvider
{
    /// <summary>The "Channel Name": the channel to chat with, picked from the followed channels after login or typed manually.</summary>
    [WidgetProperty("Channel Name", WidgetPropertyType.Choice, "Select a followed channel after Twitch login, or type a channel manually.", "twitch")]
    public string ChannelName { get; set; } = "twitch";

    /// <summary>The "Twitch Client ID": the public Twitch application ID (not a user token or secret).</summary>
    [WidgetProperty("Twitch Client ID", WidgetPropertyType.Text, "Public Twitch application ID. This is not a user token or secret.", "")]
    public string TwitchClientId { get; set; } = "";

    /// <summary>The "Log in with Twitch" action button: authorizes followed-channel access in the browser.</summary>
    [WidgetProperty("Log in with Twitch", WidgetPropertyType.Button, "Authorize followed-channel access in your browser")]
    public string LoginWithTwitch { get; set; } = "";

    /// <summary>The "Refresh live channels" action button: reloads the followed channels that are currently live.</summary>
    [WidgetProperty("Refresh live channels", WidgetPropertyType.Button, "Reload followed channels that are currently live")]
    public string RefreshLiveChannels { get; set; } = "";

    /// <summary>The "Log out of Twitch" action button: removes the locally stored Twitch authorization.</summary>
    [WidgetProperty("Log out of Twitch", WidgetPropertyType.Button, "Remove the locally stored Twitch authorization")]
    public string LogoutTwitch { get; set; } = "";

    /// <summary>The "Auto Connect" toggle: connect automatically when the widget loads.</summary>
    [WidgetProperty("Auto Connect", WidgetPropertyType.Boolean, "Connect automatically when the widget loads", true)]
    public bool AutoConnect { get; set; } = true;

    /// <summary>The "Header Color": the channel header text color.</summary>
    [WidgetProperty("Header Color", WidgetPropertyType.Color, "Channel header text color", "#F59E0B")]
    public string HeaderColorHex { get; set; } = "#F59E0B";

    /// <summary>The "Message Color": the chat message text color.</summary>
    [WidgetProperty("Message Color", WidgetPropertyType.Color, "Chat message text color", "#F8FAFC")]
    public string MessageColorHex { get; set; } = "#F8FAFC";

    /// <summary>The "Background Color": the widget background color.</summary>
    [WidgetProperty("Background Color", WidgetPropertyType.Color, "Widget background color", "#0F1117")]
    public string BackgroundHex { get; set; } = "#0F1117";

    /// <summary>The "Font Size": the chat text font size in points.</summary>
    [WidgetProperty("Font Size", WidgetPropertyType.Number, "Chat text font size in points", 24)]
    public int FontSize { get; set; } = 24;

    /// <summary>The "Max Messages": how many chat messages to keep on screen.</summary>
    [WidgetProperty("Max Messages", WidgetPropertyType.Number, "Number of chat messages to keep on screen", 30)]
    public int MaxMessages { get; set; } = 30;

    private readonly SemaphoreSlim _authActionGate = new(1, 1);
    private volatile bool _disposed;
    private readonly TwitchChatCanvas _canvas = new();
    // The chat connection module: the IRC endpoint, the anonymous credentials,
    // the handshake, the PONG keepalive, the reconnect loop, the ChatState
    // transition, and the message buffer all live there behind the
    // IWebSocketFeed seam. The widget keeps the property surface, the
    // rendering, and the touch, and drives the module from its properties.
    private readonly TwitchChatConnection _connection;

    /// <summary>
    /// Test seam for the IRC socket. Defaults to the shared
    /// <see cref="ClientWebSocketFeed"/> adapter; tests inject an in-memory
    /// feed so the connection module is drivable without a network.
    /// </summary>
    internal Func<IWebSocketFeed> FeedFactory { get; set; } = () => new ClientWebSocketFeed();

    /// <summary>
    /// The Twitch session factory seam (the <see cref="FeedFactory"/> shape):
    /// the production default returns the process-wide shared session; a test
    /// injects a factory that returns an inert session, so a test host never
    /// reaches the shared session's real token store + client (a valid stored
    /// token would perform a real network restore and mutate the singleton's
    /// followed-channel state). The factory is consulted EXACTLY ONCE, at the
    /// widget's first session use: the result is published through a
    /// <see cref="Lazy{T}"/> (the <c>??=</c> it replaces was non-atomic, so a
    /// concurrent first read - the fire-and-forget restore racing the UI
    /// thread - created two sessions). A swap before first use is honored; a
    /// swap after it is ignored by design (the widget owns one session for
    /// its lifetime). A factory that THROWS on first use is cached by the
    /// <see cref="Lazy{T}"/>: every later <c>Session</c> read rethrows the
    /// same fault (the settable property the seam replaces allowed a
    /// re-set-and-retry; the production default, <c>TwitchSession.Shared</c>,
    /// cannot throw, so the cache is observable only with a throwing test
    /// factory).
    /// </summary>
    internal Func<TwitchSession> SessionFactory { get; set; } = () => TwitchSession.Shared;

    private readonly Lazy<TwitchSession> _session;
    private TwitchSession Session => _session.Value;

    /// <summary>Binds the connection module to the widget's live properties (feed factory, MaxMessages, Auto Connect, logging, repaint request).</summary>
    public TwitchChatStreamWidget()
    {
        _session = new(() => SessionFactory(), LazyThreadSafetyMode.ExecutionAndPublication);
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

    /// <summary>
    /// Hands the context to the base and starts the chat when Auto Connect is
    /// on; restores the Twitch session (token + followed channels)
    /// fire-and-forget.
    /// </summary>
    /// <param name="context">The widget context handed to the widget on load.</param>
    /// <param name="cancellationToken">Cancels the session restore on shutdown.</param>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        if (AutoConnect) _connection.Start(ChannelName);
        _ = RestoreTwitchSessionAsync(cancellationToken);
    }

    /// <summary>
    /// Runs the named Twitch action (login, refresh live channels, logout)
    /// fire-and-forget through the session.
    /// </summary>
    /// <param name="propertyName">The action property name.</param>
    public void InvokeWidgetAction(string propertyName)
    {
        if (propertyName is nameof(LoginWithTwitch) or nameof(RefreshLiveChannels) or nameof(LogoutTwitch))
            _ = RunTwitchActionAsync(propertyName);
    }

    /// <summary>
    /// The action's display label: "Twitch logged in" for the login action
    /// while a session is authenticated, else null (the default label).
    /// </summary>
    /// <param name="propertyName">The action property name.</param>
    /// <returns>The label, or null for the default.</returns>
    public string? GetWidgetActionLabel(string propertyName)
        => string.Equals(propertyName, nameof(LoginWithTwitch), StringComparison.Ordinal) && Session.IsAuthenticated
            ? "Twitch logged in"
            : null;

    /// <summary>
    /// Whether the login action is active (a session is authenticated), so
    /// the inspector renders it as toggleable.
    /// </summary>
    /// <param name="propertyName">The action property name.</param>
    /// <returns>True when the session is authenticated.</returns>
    public bool IsWidgetActionActive(string propertyName)
        => string.Equals(propertyName, nameof(LoginWithTwitch), StringComparison.Ordinal) && Session.IsAuthenticated;

    /// <summary>
    /// The dynamic choice list for the Channel Name property: the session's
    /// followed channels (login + display label), or empty for other
    /// properties.
    /// </summary>
    /// <param name="propertyName">The property being edited.</param>
    /// <returns>The choice options for that property.</returns>
    public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
    {
        if (!string.Equals(propertyName, nameof(ChannelName), StringComparison.Ordinal)) return [];

        return Session.FollowedChannels
            .Select(channel => new WidgetPropertyOption(channel.Login, channel.DisplayLabel))
            .ToArray();
    }

    /// <summary>
    /// Routes inspector edits into the connection module: a channel change
    /// restarts the chat when Auto Connect is on, an Auto Connect toggle
    /// starts or stops it, a MaxMessages change trims the buffer.
    /// </summary>
    /// <param name="propertyName">The property that changed.</param>
    /// <param name="newValue">The new value.</param>
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

    /// <summary>
    /// Taps toggle the connection: a TouchUp stops a live chat and starts
    /// one when not connected.
    /// </summary>
    /// <param name="localPoint">The touch point in the widget's rotated-local coordinates.</param>
    /// <param name="eventType">The touch event type.</param>
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

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        // The visual facts are one frame (a struct: the per-frame build is
        // free); the connection's live facts keep their own places.
        var frame = new TwitchChatFrame(DefaultSize, ChannelName, BackgroundHex, HeaderColorHex, MessageColorHex, FontSize, AutoConnect);
        _canvas.Render(canvas, bounds, frame, _connection.State, _connection.Messages);
    }

    /// <summary>Stops the connection (loop cancel, feed abort, PONG token retirement) and disposes the canvas.</summary>
    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        _canvas.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false); // cancels the loop, aborts the feed, retires the PONG token
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
