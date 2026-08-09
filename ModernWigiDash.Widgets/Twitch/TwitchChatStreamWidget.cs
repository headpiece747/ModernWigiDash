using System;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using SkiaSharp;
using ModernWigiDash.Sdk;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets.Twitch;

[WidgetMetadata("twitch_chat", "Twitch", Description = "Live Twitch chat with live followed-channel selection and anonymous read-only IRC access.", Author = "ModernWigiDash", Version = "4.1.0", Category = "Social & Visual", DefaultGridSize = GridSizePreset.Size2x4)]
public class TwitchChatStreamWidget : ModernWidgetBase, IWidgetActionInvoker, IWidgetPropertyOptionsProvider, IWidgetActionPresentationProvider
{
    private const string AnonymousNickPrefix = "justinfan";
    private const string AnonymousPass = "SCHMOOPIIE";
    private static readonly Uri IrcEndpoint = new("wss://irc-ws.chat.twitch.tv:443");

    private const int StatusDisconnected = 0;
    private const int StatusConnecting = 1;
    private const int StatusConnected = 2;

    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x4.ToSize();
    public override SKSize MinimumSize => new SKSize(180, 120);

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
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _socket;
    private Task? _ircTask;
    private readonly SemaphoreSlim _authActionGate = new(1, 1);
    private volatile int _status;
    private volatile string _statusDetail = "";
    private volatile bool _disposed;

    private sealed record ChatMessage(string Username, string Text, SKColor Color);

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
        => propertyName == nameof(LoginWithTwitch) && TwitchSession.Shared.IsAuthenticated
            ? "Twitch logged in"
            : null;

    public bool IsWidgetActionActive(string propertyName)
        => propertyName == nameof(LoginWithTwitch) && TwitchSession.Shared.IsAuthenticated;

    public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
    {
        if (propertyName != nameof(ChannelName)) return [];

        return TwitchSession.Shared.FollowedChannels
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
                }
                break;
        }
        base.OnPropertyChanged(propertyName, newValue);
    }

    private async Task RestoreTwitchSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await TwitchSession.Shared.RestoreAsync(TwitchClientId, Context, cancellationToken).ConfigureAwait(false);
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
                    await TwitchSession.Shared.LoginAsync(TwitchClientId, Context, CancellationToken.None).ConfigureAwait(false);
                    break;
                case nameof(RefreshLiveChannels):
                    await TwitchSession.Shared.RefreshFollowedChannelsAsync(TwitchClientId, Context, CancellationToken.None).ConfigureAwait(false);
                    break;
                case nameof(LogoutTwitch):
                    await TwitchSession.Shared.LogoutAsync(CancellationToken.None).ConfigureAwait(false);
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
        if (_status == StatusConnected) StopConnection();
        else StartConnection();
    }

    private void StartConnection()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        lock (_messagesLock) _messages.Clear();
        _status = StatusConnecting;
        _statusDetail = "";
        Context.RequestRender();
        _ircTask = Task.Run(() => RunIrcLoopAsync(_cts.Token), _cts.Token);
    }

    private void StopConnection()
    {
        _cts?.Cancel();
        _socket?.Abort();
        _status = StatusDisconnected;
        _statusDetail = "";
        Context.RequestRender();
    }

    private async Task RunIrcLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested && !_disposed)
        {
            bool faulted = false;
            try
            {
                await ConnectAndReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                faulted = true;
                Context.LogError("Twitch IRC error", ex);
            }

            if (!AutoConnect || ct.IsCancellationRequested || _disposed) break;

            _status = StatusDisconnected;
            _statusDetail = "Reconnecting…";
            Context.RequestRender();

            // Reset the backoff after a healthy (non-exception) cycle so a
            // brief blip reconnects fast; only repeated failures escalate.
            backoff = faulted
                ? TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30))
                : TimeSpan.FromSeconds(1);
            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { break; }
        }
        _status = StatusDisconnected;
        _statusDetail = "";
        Context.RequestRender();
    }

    private async Task ConnectAndReadAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        _socket = socket;
        _status = StatusConnecting;
        _statusDetail = "Connecting…";
        Context.RequestRender();

        await socket.ConnectAsync(IrcEndpoint, ct);

        var channel = NormalizeChannel(ChannelName);
        string nick = AnonymousNickPrefix + Random.Shared.Next(1000000, 9999999).ToString();
        string pass = AnonymousPass;

        await SendIrcLineAsync(socket, "CAP REQ :twitch.tv/commands twitch.tv/tags", ct);
        await SendIrcLineAsync(socket, "PASS " + pass, ct);
        await SendIrcLineAsync(socket, "NICK " + nick, ct);
        await SendIrcLineAsync(socket, "JOIN #" + channel, ct);

        _status = StatusConnecting;
        _statusDetail = "Joining #" + channel + "…";
        Context.RequestRender();

        var buffer = new byte[8192];
        List<byte> pending = [];
        while (!ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            pending.AddRange(buffer.AsSpan(0, result.Count).ToArray());
            if (result.EndOfMessage)
            {
                DispatchIncomingMessage(Encoding.UTF8.GetString(pending.ToArray()));
                pending.Clear();
            }
        }
    }

    private static async Task SendIrcLineAsync(ClientWebSocket socket, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

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
            var sock = _socket;
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
                _status = StatusConnected;
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
                        _status = StatusDisconnected;
                        _statusDetail = "Login failed — check token & username";
                        Context.LogError("Twitch login failed: " + msg);
                    }
                    else if (msg.Contains("you are not logged in", StringComparison.OrdinalIgnoreCase))
                    {
                        _status = StatusConnected;
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

    private static List<string> WrapText(string text, SKFont font, float maxWidth)
    {
        List<string> result = [];
        var current = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current.ToString() + " " + word;
            if (FontHelper.MeasureTextWithFallback(candidate, font) <= maxWidth || current.Length == 0)
            {
                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }
            else
            {
                result.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        if (result.Count == 0) result.Add("");
        return result;
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var scale = Math.Clamp(Math.Min(bounds.Width / DefaultSize.Width, bounds.Height / DefaultSize.Height), 0.4f, 3f);
        if (float.IsNaN(scale) || scale <= 0) scale = 1f;

        var bg = SKColor.TryParse(BackgroundHex, out var parsedBg) ? parsedBg : new SKColor(15, 17, 23, 235);
        var headerColor = SKColor.TryParse(HeaderColorHex, out var parsedHeader) ? parsedHeader : SKColors.White;
        var msgColor = SKColor.TryParse(MessageColorHex, out var parsedMsg) ? parsedMsg : new SKColor(248, 250, 252);

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

        string statusText = _status switch
        {
            StatusConnected => "● " + (_statusDetail.Length > 0 ? _statusDetail : "LIVE"),
            StatusConnecting => "⟳ " + (_statusDetail.Length > 0 ? _statusDetail : "Connecting…"),
            _ => "○ " + (_statusDetail.Length > 0 ? _statusDetail : "Disconnected")
        };

        var statusColor = _status == StatusConnected
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

        ChatMessage[] snapshot;
        lock (_messagesLock) snapshot = _messages.ToArray();

        float msgSize = baseFontSize * scale;
        float userSize = (Math.Max(10f, baseFontSize - 2f)) * scale;
        float lineHeight = msgSize * 1.4f;
        float userLineHeight = userSize * 1.35f;

        if (snapshot.Length == 0)
        {
            var emptyFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, msgSize);
            using var emptyPaint = new SKPaint { Color = headerColor.WithAlpha(130), IsAntialias = true };
            var hint = _status switch
            {
                StatusConnected => "Waiting for chat…",
                StatusDisconnected when !AutoConnect => "Tap to connect",
                _ => "Waiting for connection…"
            };
            canvas.DrawTextWithFallback(hint, contentBounds.Left, contentBounds.Top + msgSize, emptyFont, emptyPaint, SKTextAlign.Left);
            canvas.Restore();
            return;
        }

        float cursor = contentBounds.Bottom;

        var userFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, userSize);
        var msgFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, msgSize);
        using var userPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var msgPaint = new SKPaint { Color = msgColor, IsAntialias = true };

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            var m = snapshot[i];
            var lines = WrapText(m.Text, msgFont, contentBounds.Width);

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
        if (_cts is { } cts) await cts.CancelAsync();
        _socket?.Abort();
        try { if (_ircTask != null) await _ircTask; }
        catch
        {
            System.Diagnostics.Debug.WriteLine("IRC connection task already ended during disposal (cancelled/disposed)");
            /* connection task was already cancelled/disposed */
        }
        _cts?.Dispose();
        await base.DisposeAsync();
    }
}
