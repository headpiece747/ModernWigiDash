using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SkiaSharp;
using ModernWigiDash.Sdk;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("picture_viewer", "Picture & GIF Viewer", "Displays pictures and animated GIFs with rounded borders and click-to-cycle folder viewing.", "ModernWigiDash", "2.0.0", "Social & Visual", GridSizePreset.Size2x2)]
public class PictureAndGifWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x2.ToSize();

    [WidgetProperty("Image Folder/File Path", WidgetPropertyType.Path, "Path to image or folder of images", "C:\\Pictures")]
    public string ImagePath { get; set; } = "C:\\Pictures";

    [WidgetProperty("Source Mode", WidgetPropertyType.Choice, "Auto detects file or folder; forces one mode when set", "Auto", "Auto", "Single Image", "Folder (Cycle)")]
    public string SourceMode { get; set; } = "Auto";

    [WidgetProperty("Fit Mode", WidgetPropertyType.Choice, "Aspect ratio scaling mode", "Cover", "Cover", "Contain", "Stretch")]
    public string FitMode { get; set; } = "Cover";

    [WidgetProperty("Corner Radius", WidgetPropertyType.Number, "Rounded corners radius", 16f)]
    public float CornerRadius { get; set; } = 16f;

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Placeholder icon and hint color", "#FFFFFF")]
    public string TextColorHex { get; set; } = "#FFFFFF";

    private string[] _folderImages = Array.Empty<string>();
    private int _imageIndex = 0;
    private SKBitmap? _staticBitmap;
    private SKCodec? _gifCodec;
    private SKBitmap[]? _gifFrames;
    private int _gifFrameIndex;
    private long _gifNextFrameTick;
    private string _loadedPath = "";

    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        if (propertyName is nameof(ImagePath) or nameof(SourceMode))
        {
            ResetMedia();
        }
        base.OnPropertyChanged(propertyName, newValue);
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        string? currentFile = GetActiveImageFile();

        if (!string.IsNullOrEmpty(currentFile) && File.Exists(currentFile))
        {
            if (currentFile != _loadedPath)
            {
                LoadMedia(currentFile);
            }

            if (_gifFrames is { Length: > 1 })
            {
                if (Environment.TickCount64 >= _gifNextFrameTick)
                {
                    _gifFrameIndex = (_gifFrameIndex + 1) % _gifFrames.Length;
                    _gifNextFrameTick = Environment.TickCount64 + GifFrameDurationMs(_gifFrameIndex);
                }
                DrawImage(canvas, bounds, _gifFrames[_gifFrameIndex]);
                return;
            }

            if (_staticBitmap != null)
            {
                DrawImage(canvas, bounds, _staticBitmap);
                return;
            }
        }

        DrawPlaceholder(canvas, bounds);
    }

    private void LoadMedia(string path)
    {
        ResetMedia();
        _loadedPath = path;

        try
        {
            _gifCodec = SKCodec.Create(path);
            if (_gifCodec != null && _gifCodec.FrameCount > 1)
            {
                int frameCount = _gifCodec.FrameCount;
                SKImageInfo info = _gifCodec.Info;
                _gifFrames = new SKBitmap[frameCount];

                for (int i = 0; i < frameCount; i++)
                {
                    var frame = new SKBitmap(info);
                    IntPtr pixels = frame.GetPixels();
                    var options = new SKCodecOptions { FrameIndex = i };
                    if (_gifCodec.GetPixels(info, pixels, options) != SKCodecResult.Success)
                    {
                        frame.Dispose();
                        frame = new SKBitmap(info);
                    }
                    _gifFrames[i] = frame;
                }

                _gifFrameIndex = 0;
                _gifNextFrameTick = Environment.TickCount64 + GifFrameDurationMs(0);
                return;
            }

            _gifCodec?.Dispose();
            _gifCodec = null;
            _staticBitmap = SKBitmap.Decode(path);
        }
        catch
        {
            ResetMedia();
        }
    }

    private void ResetMedia()
    {
        _loadedPath = "";
        if (_gifFrames != null)
        {
            foreach (var frame in _gifFrames)
            {
                frame.Dispose();
            }
            _gifFrames = null;
        }
        _gifCodec?.Dispose();
        _gifCodec = null;
        _staticBitmap?.Dispose();
        _staticBitmap = null;
        _gifFrameIndex = 0;
        _folderImages = Array.Empty<string>();
        _imageIndex = 0;
    }

    private long GifFrameDurationMs(int frameIndex)
    {
        if (_gifCodec != null && frameIndex >= 0 && frameIndex < _gifCodec.FrameInfo.Length)
        {
            long ms = _gifCodec.FrameInfo[frameIndex].Duration;
            if (ms > 0) return ms;
        }
        return 100L;
    }

    private void DrawImage(SKCanvas canvas, SKRect bounds, SKBitmap bitmap)
    {
        if (bitmap == null) return;

        canvas.Save();
        float radius = Math.Clamp(CornerRadius, 0f, Math.Min(bounds.Width, bounds.Height) / 2f);
        using (var clipBuilder = new SKPathBuilder())
        {
            clipBuilder.AddRoundRect(bounds, radius, radius);
            using var clipPath = clipBuilder.Snapshot();
            canvas.ClipPath(clipPath);
            canvas.DrawBitmap(bitmap, GetDrawRect(bounds, bitmap.Width, bitmap.Height), new SKSamplingOptions(SKFilterMode.Linear));
        }
        canvas.Restore();
    }

    private SKRect GetDrawRect(SKRect bounds, int imgW, int imgH)
    {
        if (FitMode == "Stretch")
        {
            return bounds;
        }

        float scale = FitMode == "Contain"
            ? Math.Min(bounds.Width / imgW, bounds.Height / imgH)
            : Math.Max(bounds.Width / imgW, bounds.Height / imgH);

        float w = imgW * scale;
        float h = imgH * scale;
        return new SKRect(bounds.MidX - w / 2f, bounds.MidY - h / 2f, bounds.MidX + w / 2f, bounds.MidY + h / 2f);
    }

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds)
    {
        SKColor textColor = SKColor.TryParse(TextColorHex, out var parsedText) ? parsedText : SKColors.White;
        using var iconFont = FontHelper.CreateFont("Segoe UI Emoji", SKFontStyle.Bold, 36f);
        using var iconPaint = new SKPaint { Color = textColor, IsAntialias = true };
        var tb = new SKRect();
        iconFont.MeasureText("🖼️", out tb, iconPaint);
        canvas.DrawText("🖼️", bounds.MidX - (tb.Width / 2f), bounds.MidY - 10f, SKTextAlign.Left, iconFont, iconPaint);

        using var labelFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, 12f);
        using var labelPaint = new SKPaint { Color = textColor, IsAntialias = true };
        string hint = "Click/Tap to Cycle Pictures";
        var lb = new SKRect();
        labelFont.MeasureText(hint, out lb, labelPaint);
        canvas.DrawText(hint, bounds.MidX - (lb.Width / 2f), bounds.MidY + 25f, SKTextAlign.Left, labelFont, labelPaint);
    }

    private string? GetActiveImageFile()
    {
        bool singleMode = SourceMode == "Single Image";
        bool folderMode = SourceMode == "Folder (Cycle)";

        if (!folderMode && File.Exists(ImagePath))
        {
            return ImagePath;
        }

        if (!singleMode && Directory.Exists(ImagePath))
        {
            if (_folderImages.Length == 0)
            {
                _folderImages = Directory.GetFiles(ImagePath, "*.*")
                    .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            if (_folderImages.Length > 0)
            {
                _imageIndex %= _folderImages.Length;
                return _folderImages[_imageIndex];
            }
        }

        return null;
    }

    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        bool folderMode = SourceMode == "Folder (Cycle)";
        bool autoFolder = SourceMode == "Auto" && Directory.Exists(ImagePath);

        if (eventType == TouchEventType.TouchUp && _folderImages.Length > 0 && (folderMode || autoFolder))
        {
            _imageIndex = (_imageIndex + 1) % _folderImages.Length;
            _loadedPath = "";
            Context?.RequestRender();
        }
    }

    public override ValueTask DisposeAsync()
    {
        ResetMedia();
        return base.DisposeAsync();
    }
}

[WidgetMetadata("twitch_chat", "Twitch", "Live Twitch chat with live followed-channel selection and anonymous read-only IRC access.", "ModernWigiDash", "4.1.0", "Social & Visual", GridSizePreset.Size2x4)]
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

    [WidgetProperty("Channel Name", WidgetPropertyType.Choice, "Select a followed channel after Twitch login, or type a channel manually.", "shroud")]
    public string ChannelName { get; set; } = "shroud";

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
    public string HeaderColorHex { get; set; } = "#FFFFFF";

    [WidgetProperty("Message Color", WidgetPropertyType.Color, "Chat message text color", "#F8FAFC")]
    public string MessageColorHex { get; set; } = "#F8FAFC";

    [WidgetProperty("Background Color", WidgetPropertyType.Color, "Widget background color", "#0F1117")]
    public string BackgroundHex { get; set; } = "#0F1117";

    [WidgetProperty("Max Messages", WidgetPropertyType.Number, "Number of chat messages to keep on screen", 30)]
    public int MaxMessages { get; set; } = 30;

    private readonly object _messagesLock = new();
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

    public override ValueTask InitializeAsync(ModernWigiDashContext context, CancellationToken cancellationToken = default)
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
            (Context as IWidgetHostInteraction)?.RequestInspectorRefresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Context.LogError("Unable to restore the Twitch login", ex);
        }
    }

    private async Task RunTwitchActionAsync(string propertyName)
    {
        if (!await _authActionGate.WaitAsync(0).ConfigureAwait(false)) return;

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

            (Context as IWidgetHostInteraction)?.RequestInspectorRefresh();
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
        _ircTask = Task.Run(() => RunIrcLoopAsync(_cts.Token));
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
                Context.LogError("Twitch IRC error", ex);
            }

            if (!AutoConnect || ct.IsCancellationRequested || _disposed) break;

            _status = StatusDisconnected;
            _statusDetail = "Reconnecting…";
            Context.RequestRender();
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
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

        await SendAsync(socket, "CAP REQ :twitch.tv/commands twitch.tv/tags", ct);
        await SendAsync(socket, "PASS " + pass, ct);
        await SendAsync(socket, "NICK " + nick, ct);
        await SendAsync(socket, "JOIN #" + channel, ct);

        _status = StatusConnecting;
        _statusDetail = "Joining #" + channel + "…";
        Context.RequestRender();

        var buffer = new byte[8192];
        var pending = new List<byte>();
        while (!ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            pending.AddRange(buffer.AsSpan(0, result.Count).ToArray());
            if (result.EndOfMessage)
            {
                ProcessIncoming(Encoding.UTF8.GetString(pending.ToArray()));
                pending.Clear();
            }
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private void ProcessIncoming(string data)
    {
        foreach (var rawLine in data.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            ProcessLine(rawLine);
        }
    }

    private void ProcessLine(string line)
    {
        if (line.StartsWith("PING", StringComparison.Ordinal))
        {
            var sock = _socket;
            if (sock != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await SendAsync(sock, "PONG :tmi.twitch.tv", _cts?.Token ?? CancellationToken.None); }
                    catch { /* socket closed / cancelled during shutdown */ }
                });
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
                _statusDetail = "LIVE · #" + NormalizeChannel(ChannelName).ToUpperInvariant();
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
                    _statusDetail = "LIVE · #" + NormalizeChannel(ChannelName).ToUpperInvariant();
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
        Context.RequestRender();
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
        return c.Length == 0 ? "shroud" : c.ToLowerInvariant();
    }

    private static SKColor PaletteFor(string name)
    {
        int hash = 17;
        foreach (var c in name) hash = (hash * 31 + c) & 0x7FFFFFFF;
        return NamePalette[hash % NamePalette.Length];
    }

    private static List<string> WrapText(string text, SKFont font, float maxWidth)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current.ToString() + " " + word;
            if (font.MeasureText(candidate) <= maxWidth || current.Length == 0)
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
        canvas.DrawRoundRect(bounds, 14f, 14f, bgPaint);

        float pad = 12f * scale;
        float titleSize = 13f * scale;
        float statusSize = 10f * scale;
        float msgSize = 12f * scale;
        float lineHeight = 20f * scale;

        using var titleFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, titleSize);
        using var statusFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, statusSize);
        using var titlePaint = new SKPaint { Color = headerColor, IsAntialias = true };
        using var statusPaint = new SKPaint { Color = headerColor.WithAlpha((byte)(headerColor.Alpha * 0.65f)), IsAntialias = true };

        float top = bounds.Top + pad;
        canvas.DrawText("💬 TWITCH LIVE CHAT", bounds.Left + pad, top + titleSize, SKTextAlign.Left, titleFont, titlePaint);
        canvas.DrawText("#" + NormalizeChannel(ChannelName).ToUpperInvariant(), bounds.Right - pad, top + titleSize, SKTextAlign.Right, titleFont, titlePaint);

        float headerBottom = top + titleSize + 6f * scale;

        string statusText = _status switch
        {
            StatusConnected => "● " + _statusDetail,
            StatusConnecting => "⟳ " + _statusDetail,
            _ => "○ " + _statusDetail
        };
        if (_statusDetail.Length == 0)
        {
            statusText = _status switch
            {
                StatusConnected => "● LIVE",
                StatusConnecting => "⟳ Connecting…",
                _ => "○ Disconnected"
            };
        }
        float statusY = headerBottom + 6f * scale;
        canvas.DrawText(statusText, bounds.Left + pad, statusY + statusSize, SKTextAlign.Left, statusFont, statusPaint);
        headerBottom = statusY + statusSize + 4f * scale;

        ChatMessage[] snapshot;
        lock (_messagesLock) snapshot = _messages.ToArray();

        if (snapshot.Length == 0)
        {
            using var emptyFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, msgSize);
            using var emptyPaint = new SKPaint { Color = headerColor.WithAlpha(130), IsAntialias = true };
            var hint = _status switch
            {
                StatusConnected => "Waiting for chat…",
                StatusDisconnected when !AutoConnect => "Tap to connect",
                _ => "Waiting for connection…"
            };
            canvas.DrawText(hint, bounds.Left + pad, headerBottom + msgSize, SKTextAlign.Left, emptyFont, emptyPaint);
            return;
        }

        float maxTextWidth = bounds.Width - pad * 2f;
        float cursor = bounds.Bottom - pad;

        using var userFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, msgSize);
        using var msgFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, msgSize);
        using var userPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var msgPaint = new SKPaint { Color = msgColor, IsAntialias = true };

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            var m = snapshot[i];
            var userLabel = m.Username + ": ";
            var userLabelWidth = userFont.MeasureText(userLabel);
            var wrapWidth = Math.Max(20f, maxTextWidth - userLabelWidth);
            var lines = WrapText(m.Text, msgFont, wrapWidth);

            float blockH = lines.Count * lineHeight;
            cursor -= blockH;
            if (cursor < headerBottom) break;

            userPaint.Color = m.Color;
            canvas.DrawText(userLabel, bounds.Left + pad, cursor + lineHeight - 4f * scale, SKTextAlign.Left, userFont, userPaint);

            float tx = bounds.Left + pad + userLabelWidth;
            for (int li = 0; li < lines.Count; li++)
            {
                canvas.DrawText(lines[li], tx, cursor + (li + 1) * lineHeight - 4f * scale, SKTextAlign.Left, msgFont, msgPaint);
                tx = bounds.Left + pad;
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_cts is { } cts) await cts.CancelAsync();
        _socket?.Abort();
        try { if (_ircTask != null) await _ircTask; }
        catch { /* connection task was already cancelled/disposed */ }
        _cts?.Dispose();
        await base.DisposeAsync();
    }
}

[WidgetMetadata("weather_forecast", "Weather Forecast", "Displays live real-time weather, hourly/daily forecasts, metrics, and custom layouts via Open-Meteo API. Supports city names, ZIP/postal codes, and coordinates.", "ModernWigiDash", "2.0.0", "Social & Visual", GridSizePreset.Size5x4)]
public class WeatherForecastWidget : ModernWidgetBase
{
    private const float DesignWidth = 406f;
    private const float DesignHeight = 296f;

    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size5x4.ToSize();
    public override SKSize MinimumSize => new SKSize(200, 160);

    [WidgetProperty("Location Type", WidgetPropertyType.Choice, "City name, ZIP code, or lat,lon pair", "Fixed Location", "Fixed Location")]
    public string LocationType { get; set; } = "Fixed Location";

    [WidgetProperty("Location", WidgetPropertyType.Text, "City name, ZIP/postal code, or lat,lon (e.g. 40.71,-74.00)", "New York")]
    public string Location { get; set; } = "New York";

    [WidgetProperty("Custom Label", WidgetPropertyType.Text, "Custom title display name override", "")]
    public string CustomLabel { get; set; } = "";

    [WidgetProperty("Unit System", WidgetPropertyType.Choice, "Temperature & speed units", "Fahrenheit (°F, mph)", "Fahrenheit (°F, mph)", "Celsius (°C, km/h)", "Celsius (°C, mph)", "Celsius (°C, m/s)", "Kelvin (K, m/s)")]
    public string UnitSystem { get; set; } = "Fahrenheit (°F, mph)";

    [WidgetProperty("Layout Mode", WidgetPropertyType.Choice, "Display view style", "Detailed", "Detailed", "Daily Forecast", "Hourly Forecast", "Current Only", "Compact")]
    public string LayoutMode { get; set; } = "Detailed";

    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary glowing accent color", "#870000")]
    public string AccentColorHex { get; set; } = "#870000";

    [WidgetProperty("Show Humidity", WidgetPropertyType.Boolean, "Display relative humidity metric", true)]
    public bool ShowHumidity { get; set; } = true;

    [WidgetProperty("Show Wind", WidgetPropertyType.Boolean, "Display wind speed & direction", true)]
    public bool ShowWind { get; set; } = true;

    [WidgetProperty("Show Feels Like", WidgetPropertyType.Boolean, "Display apparent temperature", true)]
    public bool ShowFeelsLike { get; set; } = true;

    [WidgetProperty("Show High / Low", WidgetPropertyType.Boolean, "Display today's max and min temp", true)]
    public bool ShowHighLow { get; set; } = true;

    [WidgetProperty("Show Forecast Strip", WidgetPropertyType.Boolean, "Display multi-day forecast strip in Detailed view", true)]
    public bool ShowForecast { get; set; } = true;

    [WidgetProperty("Static Snapshot", WidgetPropertyType.Boolean, "Freeze current weather data as a static snapshot", false)]
    public bool StaticSnapshot { get; set; } = false;

    [WidgetProperty("Latitude", WidgetPropertyType.Text, "Override latitude (e.g. 40.7128). Leave empty to auto-resolve from Location.", "")]
    public string Latitude { get; set; } = "";

    [WidgetProperty("Longitude", WidgetPropertyType.Text, "Override longitude (e.g. -74.0060). Leave empty to auto-resolve from Location.", "")]
    public string Longitude { get; set; } = "";

    private readonly record struct DailyForecastItem(string DayName, double MaxTempC, double MinTempC, int WeatherCode);
    private readonly record struct HourlyForecastItem(string TimeLabel, double TempC, int WeatherCode);

    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        EnableMultipleHttp2Connections = true
    });

    private DateTime _lastFetchTime = DateTime.MinValue;
    private bool _isFetching;
    private string _lastLocationQuery = "";

    private double? _lat;
    private double? _lon;
    private string _resolvedCityName = "New York";

    private double _currentTempC = 25.0; // 77°F default
    private double _feelsLikeC = 22.2;  // 72°F default
    private double _humidity = 87.0;
    private double _windSpeedKmH = 16.1; // 10 mph default
    private int _weatherCode = 51;      // Drizzle default
    private double _highTempC = 26.6;   // 80°F default
    private double _lowTempC = 20.5;    // 69°F default

    private readonly List<DailyForecastItem> _dailyForecasts = [];
    private readonly List<HourlyForecastItem> _hourlyForecasts = [];

    private static readonly string CacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "weather_cache");
    private Timer? _refreshTimer;
    private CancellationTokenSource? _pollCts;
    private string CachePath => Path.Combine(CacheDir, $"weather_{InstanceId}.json");

    public override ValueTask InitializeAsync(ModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(context, cancellationToken);
        Directory.CreateDirectory(CacheDir);
        _ = LoadCacheAsync();
        _pollCts = new CancellationTokenSource();
        _refreshTimer = new Timer(async _ => await FetchLiveWeatherAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(15));
        _ = FetchLiveWeatherAsync();
        return ValueTask.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_refreshTimer is IAsyncDisposable asyncTimer)
            await asyncTimer.DisposeAsync();
        else
            _refreshTimer?.Dispose();
        if (_pollCts != null)
        {
            await _pollCts.CancelAsync();
            _pollCts.Dispose();
        }
        await base.DisposeAsync();
    }

    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        if (propertyName is nameof(Location) or nameof(Latitude) or nameof(Longitude))
        {
            _lat = null;
            _lon = null;
            _lastFetchTime = DateTime.MinValue;
            _ = FetchLiveWeatherAsync(force: true);
        }
        base.OnPropertyChanged(propertyName, newValue);
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        _ = FetchLiveWeatherAsync();

        SKColor accentColor = SKColors.White;
        SKColor textPrimary = SKColors.White;
        SKColor textSecondary = SKColors.White;

        float sx = bounds.Width / DesignWidth;
        float sy = bounds.Height / DesignHeight;
        float s = Math.Min(sx, sy);
        float pad = Math.Clamp(14f * s, 8f, 32f);
        float headerHeight = Math.Clamp(44f * sy, 24f, 90f);

        // Prominent Location Name Header
        string cityRaw = string.IsNullOrWhiteSpace(CustomLabel) ? _resolvedCityName : CustomLabel;
        string headerDisplay = cityRaw.ToUpper();
        float locationFontSize = Math.Clamp(24f * s, 12f, 44f);
        using var titleFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, locationFontSize);
        using var titlePaint = new SKPaint { Color = textPrimary, IsAntialias = true };

        float headerTextY = bounds.Top + headerHeight * 0.65f;

        // Auto-truncate city name to guarantee header text fits without overlapping badge
        var (tempUnit, speedUnit) = ParseUnitSystem(UnitSystem);
        float badgeWidth = Math.Clamp(54f * s, 30f, 100f);
        float badgeHeight = Math.Clamp(26f * sy, 16f, 50f);
        float maxTitleW = Math.Max(30f, bounds.Width - pad * 2f - badgeWidth);

        string truncatedHeader = TruncateText(headerDisplay, titleFont, maxTitleW);
        canvas.DrawText(truncatedHeader, bounds.Left + pad, headerTextY, SKTextAlign.Left, titleFont, titlePaint);

        // Styled Unit Toggle Badge [°F] / [°C] (No background card)
        SKRect badgeRect = new(bounds.Right - pad - badgeWidth, bounds.Top + (headerHeight - badgeHeight) / 2f, bounds.Right - pad, bounds.Top + (headerHeight + badgeHeight) / 2f);

        using var unitFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, Math.Clamp(17f * s, 10f, 30f));
        using var unitPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        float uW = unitFont.MeasureText(tempUnit);
        canvas.DrawText(tempUnit, badgeRect.MidX - uW / 2f, badgeRect.MidY + 4.5f * s, SKTextAlign.Left, unitFont, unitPaint);

        // Content Area Bounds
        SKRect contentBounds = new(bounds.Left + pad, bounds.Top + headerHeight + 6f * sy, bounds.Right - pad, bounds.Bottom - pad);

        switch (LayoutMode)
        {
            case "Daily Forecast":
                RenderDailyForecast(canvas, contentBounds, accentColor, textPrimary, textSecondary, tempUnit, speedUnit, sx, sy);
                break;
            case "Hourly Forecast":
                RenderHourlyForecast(canvas, contentBounds, accentColor, textPrimary, textSecondary, tempUnit, speedUnit, sx, sy);
                break;
            case "Current Only":
                RenderCurrentOnly(canvas, contentBounds, accentColor, textPrimary, textSecondary, tempUnit, speedUnit, sx, sy);
                break;
            case "Compact":
                RenderCompact(canvas, contentBounds, accentColor, textPrimary, textSecondary, tempUnit, speedUnit, sx, sy);
                break;
            default:
                RenderDetailed(canvas, contentBounds, accentColor, textPrimary, textSecondary, tempUnit, speedUnit, sx, sy);
                break;
        }
    }

    private void RenderDetailed(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, string tempUnit, string speedUnit, float sx, float sy)
    {
        var (icon, desc) = MapWmoCode(_weatherCode);
        float s = Math.Min(sx, sy);
        float w = bounds.Width;
        float h = bounds.Height;

        // Show forecast strip only if container height is at least 150px physical units
        bool hasForecast = ShowForecast && _dailyForecasts.Count > 0 && h >= 150f;
        float forecastH = hasForecast ? Math.Clamp(80f * sy, 45f, 160f) : 0f;

        var metrics = new List<string>();
        if (ShowFeelsLike) metrics.Add($"Feels: {FormatTemp(_feelsLikeC, tempUnit, true)}");
        if (ShowHumidity) metrics.Add($"Humidity: {_humidity:F0}%");
        if (ShowWind) metrics.Add($"Wind: {FormatSpeed(_windSpeedKmH, speedUnit)}");
        if (ShowHighLow) metrics.Add($"H:{FormatTemp(_highTempC, tempUnit, true)} L:{FormatTemp(_lowTempC, tempUnit, true)}");

        // Show metrics pill strip only if container height is at least 150px physical units
        bool hasMetrics = metrics.Count > 0 && h >= 150f;
        float metricsH = hasMetrics ? Math.Clamp(28f * sy, 16f, 50f) : 0f;

        float heroTop = bounds.Top + 4f * sy;
        float heroBottom = bounds.Bottom - forecastH - (hasMetrics ? metricsH + 12f * sy : 0f) - 4f * sy;
        float heroHeight = Math.Max(heroBottom - heroTop, 35f);
        float heroMidY = heroTop + heroHeight / 2f;

        // Sizing hero elements proportionally to fit strictly inside heroHeight without overlapping pills below
        float iconSize = Math.Clamp(heroHeight * 0.75f, 20f, 220f);
        float tempSize = Math.Clamp(heroHeight * 0.45f, 14f, 140f);
        float descSize = Math.Clamp(heroHeight * 0.18f, 9f, 45f);

        using var iconFont = FontHelper.CreateFont("Segoe UI Emoji", SKFontStyle.Bold, iconSize);
        using var iconPaint = new SKPaint { IsAntialias = true };
        float iconW = iconFont.MeasureText(icon);

        string mainTempStr = FormatTemp(_currentTempC, tempUnit);
        using var tempFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, tempSize);
        using var tempPaint = new SKPaint { Color = textPrimary, IsAntialias = true };

        using var descFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, descSize);
        using var descPaint = new SKPaint { Color = accentColor, IsAntialias = true };

        // Ensure vertical text stack (Temp + Condition) strictly fits inside heroHeight
        tempFont.GetFontMetrics(out var tempMetrics);
        descFont.GetFontMetrics(out var descMetrics);
        float tempH = tempMetrics.Descent - tempMetrics.Ascent;
        float descH = descMetrics.Descent - descMetrics.Ascent;
        float textStackSpacing = 2f * sy;
        float textStackTotalH = tempH + textStackSpacing + descH;

        if (textStackTotalH > heroHeight * 0.85f)
        {
            float fitScale = (heroHeight * 0.85f) / textStackTotalH;
            tempSize *= fitScale;
            descSize *= fitScale;
            tempFont.Size = tempSize;
            descFont.Size = descSize;

            tempFont.GetFontMetrics(out tempMetrics);
            descFont.GetFontMetrics(out descMetrics);
            tempH = tempMetrics.Descent - tempMetrics.Ascent;
            descH = descMetrics.Descent - descMetrics.Ascent;
            textStackTotalH = tempH + textStackSpacing + descH;
        }

        float tempW = tempFont.MeasureText(mainTempStr);
        float descW = descFont.MeasureText(desc);

        float rightBlockW = Math.Max(tempW, descW);
        float gap = Math.Clamp(20f * s, 8f, 50f);
        float totalBlockW = iconW + gap + rightBlockW;

        // Auto-scale hero block down if container is narrow
        if (totalBlockW > w)
        {
            float scaleFactor = Math.Max(0.5f, w / totalBlockW);
            iconSize *= scaleFactor;
            tempSize *= scaleFactor;
            descSize *= scaleFactor;
            gap *= scaleFactor;

            iconFont.Size = iconSize;
            tempFont.Size = tempSize;
            descFont.Size = descSize;

            iconW = iconFont.MeasureText(icon);
            tempW = tempFont.MeasureText(mainTempStr);
            descW = descFont.MeasureText(desc);
            rightBlockW = Math.Max(tempW, descW);
            totalBlockW = iconW + gap + rightBlockW;
        }

        float blockLeft = bounds.MidX - totalBlockW / 2f;
        float rightX = blockLeft + iconW + gap;

        // Draw Icon perfectly centered vertically beside Temp + Condition
        iconFont.GetFontMetrics(out var iconMetrics);
        float iconBaseline = heroMidY - (iconMetrics.Ascent + iconMetrics.Descent) / 2f;
        canvas.DrawText(icon, blockLeft, iconBaseline, SKTextAlign.Left, iconFont, iconPaint);

        // Stack Temperature & Condition on right of icon with centered vertical alignment
        float textStackTop = heroMidY - textStackTotalH / 2f;
        float tempBaseline = textStackTop - tempMetrics.Ascent;
        float descBaseline = tempBaseline + tempMetrics.Descent + textStackSpacing - descMetrics.Ascent;

        canvas.DrawText(mainTempStr, rightX, tempBaseline, SKTextAlign.Left, tempFont, tempPaint);
        canvas.DrawText(desc, rightX, descBaseline, SKTextAlign.Left, descFont, descPaint);

        // Render Metrics Pill Strip below hero area with zero overlap
        if (hasMetrics)
        {
            float pillY = heroBottom + 4f * sy;
            float pillHeight = metricsH;
            float metricFontSize = Math.Clamp(13f * s, 8f, 24f);
            using var metricFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, metricFontSize);

            float pillPadX = Math.Clamp(10f * s, 4f, 20f);
            float pillGap = Math.Clamp(8f * s, 3f, 16f);
            float totalPillsW = 0f;
            float[] metricWidths = new float[metrics.Count];
            for (int i = 0; i < metrics.Count; i++)
            {
                metricWidths[i] = metricFont.MeasureText(metrics[i]) + pillPadX * 2;
                totalPillsW += metricWidths[i];
            }
            totalPillsW += (metrics.Count - 1) * pillGap;

            // If pills exceed bounds width, scale down metric font size to fit inside card
            if (totalPillsW > w)
            {
                float metricScale = Math.Max(0.6f, w / totalPillsW);
                metricFontSize = Math.Max(7f, metricFontSize * metricScale);
                metricFont.Size = metricFontSize;
                pillPadX *= metricScale;
                pillGap *= metricScale;

                totalPillsW = 0f;
                for (int i = 0; i < metrics.Count; i++)
                {
                    metricWidths[i] = metricFont.MeasureText(metrics[i]) + pillPadX * 2;
                    totalPillsW += metricWidths[i];
                }
                totalPillsW += (metrics.Count - 1) * pillGap;
            }

            float pillStartX = bounds.MidX - totalPillsW / 2f;
            for (int i = 0; i < metrics.Count; i++)
            {
                SKRect pillRect = new(pillStartX, pillY, pillStartX + metricWidths[i], pillY + pillHeight);
                using var pillBorder = new SKPaint { Color = new SKColor(255, 255, 255, 22), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f * s, 1f), IsAntialias = true };
                canvas.DrawRoundRect(pillRect, 8f * s, 8f * s, pillBorder);

                using var metricPaint = new SKPaint { Color = textSecondary, IsAntialias = true };
                metricFont.GetFontMetrics(out var mMetrics);
                float mBaseline = pillRect.MidY - (mMetrics.Ascent + mMetrics.Descent) / 2f;
                canvas.DrawText(metrics[i], pillRect.MidX, mBaseline, SKTextAlign.Center, metricFont, metricPaint);
                pillStartX += metricWidths[i] + pillGap;
            }
        }

        // Render Forecast Strip with exact visual bounding box horizontal centering for emoji icons
        if (hasForecast)
        {
            int count = Math.Min(_dailyForecasts.Count, 5);
            float stripY = bounds.Bottom - forecastH;
            SKRect stripBounds = new(bounds.Left, stripY, bounds.Right, bounds.Bottom);

            using var stripBorder = new SKPaint { Color = new SKColor(255, 255, 255, 18), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f * s, 1f), IsAntialias = true };
            canvas.DrawRoundRect(stripBounds, 12f * s, 12f * s, stripBorder);

            float colWidth = w / count;
            float dayFontSize = Math.Clamp(14f * s, 8f, 24f);
            float dayIconFontSize = Math.Clamp(22f * s, 10f, 48f);
            float rangeFontSize = Math.Clamp(12f * s, 7f, 22f);

            for (int i = 0; i < count; i++)
            {
                var day = _dailyForecasts[i];
                var (dayIcon, _) = MapWmoCode(day.WeatherCode);
                float colCx = bounds.Left + (i + 0.5f) * colWidth;

                using var dayFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, dayFontSize);
                using var dayPaint = new SKPaint { Color = i == 0 ? accentColor : textPrimary, IsAntialias = true };
                float dayY = stripY + Math.Clamp(18f * s, 10f, 36f);

                dayFont.MeasureText(day.DayName, out var dayBounds);
                float dayX = colCx - (dayBounds.Left + dayBounds.Width / 2f);
                canvas.DrawText(day.DayName, dayX, dayY, SKTextAlign.Left, dayFont, dayPaint);

                string rangeStr = $"{FormatTemp(day.MaxTempC, tempUnit, true)} / {FormatTemp(day.MinTempC, tempUnit, true)}";
                using var rangeFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, rangeFontSize);
                using var rangePaint = new SKPaint { Color = textSecondary, IsAntialias = true };
                float rangeY = stripBounds.Bottom - Math.Clamp(10f * s, 5f, 20f);

                rangeFont.MeasureText(rangeStr, out var rangeBounds);
                float rangeX = colCx - (rangeBounds.Left + rangeBounds.Width / 2f);
                canvas.DrawText(rangeStr, rangeX, rangeY, SKTextAlign.Left, rangeFont, rangePaint);

                using var dayIconFont = FontHelper.CreateFont("Segoe UI Emoji", SKFontStyle.Normal, dayIconFontSize);
                using var dayIconPaint = new SKPaint { IsAntialias = true };

                // Calculate exact vertical center between Day Name and Temp Range baselines
                dayFont.GetFontMetrics(out var dayMetrics);
                rangeFont.GetFontMetrics(out var rangeMetrics);
                dayIconFont.GetFontMetrics(out var dayIconMetrics);

                float dayBottomY = dayY + dayMetrics.Descent;
                float rangeTopY = rangeY + rangeMetrics.Ascent;
                float midGapY = (dayBottomY + rangeTopY) / 2f;
                float dayIconBaseline = midGapY - (dayIconMetrics.Ascent + dayIconMetrics.Descent) / 2f;

                // Exact visual bounding box horizontal centering for emoji icon
                dayIconFont.MeasureText(dayIcon, out var iconRect);
                float iconVisualCenterX = iconRect.Left + (iconRect.Width / 2f);
                float iconX = colCx - iconVisualCenterX;

                canvas.DrawText(dayIcon, iconX, dayIconBaseline, SKTextAlign.Left, dayIconFont, dayIconPaint);
            }
        }
    }

    private void RenderDailyForecast(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, string tempUnit, string speedUnit, float sx, float sy)
    {
        int count = Math.Min(_dailyForecasts.Count, 5);
        if (count == 0) return;

        float rowHeight = bounds.Height / count;
        float s = Math.Min(sx, sy);

        for (int i = 0; i < count; i++)
        {
            var day = _dailyForecasts[i];
            float y = bounds.Top + (i * rowHeight);
            SKRect rowRect = new(bounds.Left, y + 2, bounds.Right, y + rowHeight - 2);

            using var rowBg = new SKPaint { Color = new SKColor(22, 26, 40, 180), IsAntialias = true };
            using var rowBorder = new SKPaint { Color = new SKColor(255, 255, 255, 15), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
            canvas.DrawRoundRect(rowRect, 8f * s, 8f * s, rowBg);
            canvas.DrawRoundRect(rowRect, 8f * s, 8f * s, rowBorder);

            var (icon, desc) = MapWmoCode(day.WeatherCode);

            using var dayFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, Math.Clamp(13f * s, 9f, 18f));
            using var dayPaint = new SKPaint { Color = i == 0 ? accentColor : textPrimary, IsAntialias = true };
            canvas.DrawText(day.DayName, rowRect.Left + 12f * sx, rowRect.MidY + 5f * sy, SKTextAlign.Left, dayFont, dayPaint);

            using var iconFont = FontHelper.CreateFont("Segoe UI Emoji", SKFontStyle.Normal, Math.Clamp(16f * s, 10f, 22f));
            using var iconPaint = new SKPaint { IsAntialias = true };
            canvas.DrawText(icon, rowRect.Left + 80f * sx, rowRect.MidY + 6f * sy, SKTextAlign.Left, iconFont, iconPaint);

            using var descFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, Math.Clamp(11f * s, 8f, 15f));
            using var descPaint = new SKPaint { Color = textSecondary, IsAntialias = true };
            canvas.DrawText(desc, rowRect.Left + 110f * sx, rowRect.MidY + 4f * sy, SKTextAlign.Left, descFont, descPaint);

            string highLowStr = $"High: {FormatTemp(day.MaxTempC, tempUnit)}  Low: {FormatTemp(day.MinTempC, tempUnit)}";
            using var tempFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, Math.Clamp(12f * s, 8f, 16f));
            using var tempPaint = new SKPaint { Color = accentColor, IsAntialias = true };
            canvas.DrawText(highLowStr, rowRect.Right - tempFont.MeasureText(highLowStr) - 12f * sx, rowRect.MidY + 4f * sy, SKTextAlign.Left, tempFont, tempPaint);
        }
    }

    private void RenderHourlyForecast(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, string tempUnit, string speedUnit, float sx, float sy)
    {
        int count = Math.Min(_hourlyForecasts.Count, 6);
        if (count == 0) return;

        float itemWidth = bounds.Width / count;
        float s = Math.Min(sx, sy);

        for (int i = 0; i < count; i++)
        {
            var item = _hourlyForecasts[i];
            float x = bounds.Left + (i * itemWidth);
            SKRect colRect = new(x + 2, bounds.Top + 4, x + itemWidth - 2, bounds.Bottom - 4);

            using var colBg = new SKPaint { Color = new SKColor(22, 26, 40, 180), IsAntialias = true };
            using var colBorder = new SKPaint { Color = new SKColor(255, 255, 255, 15), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
            canvas.DrawRoundRect(colRect, 8f * s, 8f * s, colBg);
            canvas.DrawRoundRect(colRect, 8f * s, 8f * s, colBorder);

            var (icon, _) = MapWmoCode(item.WeatherCode);

            using var timeFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, Math.Clamp(11f * s, 8f, 15f));
            using var timePaint = new SKPaint { Color = textSecondary, IsAntialias = true };
            canvas.DrawText(item.TimeLabel, colRect.MidX - (timeFont.MeasureText(item.TimeLabel) / 2f), colRect.Top + 22f * sy, SKTextAlign.Left, timeFont, timePaint);

            using var iconFont = FontHelper.CreateFont("Segoe UI Emoji", SKFontStyle.Normal, Math.Clamp(20f * s, 12f, 28f));
            using var iconPaint = new SKPaint { IsAntialias = true };
            canvas.DrawText(icon, colRect.MidX - 12f * sx, colRect.MidY + 6f * sy, SKTextAlign.Left, iconFont, iconPaint);

            string tempStr = FormatTemp(item.TempC, tempUnit);
            using var tempFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, Math.Clamp(12f * s, 8f, 16f));
            using var tempPaint = new SKPaint { Color = accentColor, IsAntialias = true };
            canvas.DrawText(tempStr, colRect.MidX - (tempFont.MeasureText(tempStr) / 2f), colRect.Bottom - 14f * sy, SKTextAlign.Left, tempFont, tempPaint);
        }
    }

    private void RenderCurrentOnly(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, string tempUnit, string speedUnit, float sx, float sy)
    {
        var (icon, desc) = MapWmoCode(_weatherCode);
        float s = Math.Min(sx, sy);
        float midY = bounds.MidY;
        float midX = bounds.MidX;

        float iconSize = Math.Clamp(88f * s, 40f, 120f);
        float tempSize = Math.Clamp(64f * s, 28f, 84f);
        float descSize = Math.Clamp(24f * s, 12f, 32f);

        using var iconFont = FontHelper.CreateFont("Segoe UI Emoji", SKFontStyle.Bold, iconSize);
        using var iconPaint = new SKPaint { IsAntialias = true };
        float iconW = iconFont.MeasureText(icon);

        string mainTempStr = FormatTemp(_currentTempC, tempUnit);
        using var tempFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, tempSize);
        using var tempPaint = new SKPaint { Color = textPrimary, IsAntialias = true };
        float tempW = tempFont.MeasureText(mainTempStr);

        using var descFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, descSize);
        using var descPaint = new SKPaint { Color = accentColor, IsAntialias = true };
        float descW = descFont.MeasureText(desc);

        float rightBlockW = Math.Max(tempW, descW);
        float gap = 24f * sx;
        float totalBlockW = iconW + gap + rightBlockW;
        float blockLeft = midX - totalBlockW / 2f;
        float rightX = blockLeft + iconW + gap;

        iconFont.GetFontMetrics(out var iconMetrics);
        float iconBaseline = midY - (iconMetrics.Ascent + iconMetrics.Descent) / 2f;
        canvas.DrawText(icon, blockLeft, iconBaseline, SKTextAlign.Left, iconFont, iconPaint);

        tempFont.GetFontMetrics(out var tempMetrics);
        descFont.GetFontMetrics(out var descMetrics);
        float tempH = tempMetrics.Descent - tempMetrics.Ascent;
        float descH = descMetrics.Descent - descMetrics.Ascent;
        float textStackTotalH = tempH + 6f * sy + descH;
        float textStackTop = midY - textStackTotalH / 2f;

        float tempBaseline = textStackTop - tempMetrics.Ascent;
        float descBaseline = tempBaseline + tempMetrics.Descent + 6f * sy - descMetrics.Ascent;

        canvas.DrawText(mainTempStr, rightX, tempBaseline, SKTextAlign.Left, tempFont, tempPaint);
        canvas.DrawText(desc, rightX, descBaseline, SKTextAlign.Left, descFont, descPaint);
    }

    private void RenderCompact(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, string tempUnit, string speedUnit, float sx, float sy)
    {
        var (icon, _) = MapWmoCode(_weatherCode);
        float s = Math.Min(sx, sy);

        using var iconFont = FontHelper.CreateFont("Segoe UI Emoji", SKFontStyle.Bold, Math.Clamp(26f * s, 14f, 32f));
        using var iconPaint = new SKPaint { IsAntialias = true };
        canvas.DrawText(icon, bounds.Left, bounds.MidY + 10f * sy, SKTextAlign.Left, iconFont, iconPaint);

        string mainTempStr = FormatTemp(_currentTempC, tempUnit);
        using var tempFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, Math.Clamp(20f * s, 12f, 26f));
        using var tempPaint = new SKPaint { Color = textPrimary, IsAntialias = true };
        canvas.DrawText(mainTempStr, bounds.Left + 36f * sx, bounds.MidY + 8f * sy, SKTextAlign.Left, tempFont, tempPaint);
    }

    private static (string Icon, string Description) MapWmoCode(int code)
    {
        return code switch
        {
            0 => ("☀️", "Clear Sky"),
            1 => ("🌤️", "Mainly Clear"),
            2 => ("⛅", "Partly Cloudy"),
            3 => ("☁️", "Overcast"),
            45 or 48 => ("🌫️", "Foggy"),
            51 or 53 or 55 => ("🌧️", "Drizzle"),
            56 or 57 => ("🌧️❄️", "Freezing Drizzle"),
            61 or 63 or 65 => ("🌧️", "Rainy"),
            66 or 67 => ("🌧️❄️", "Freezing Rain"),
            71 or 73 or 75 or 77 => ("❄️", "Snowy"),
            80 or 81 or 82 => ("🌦️", "Rain Showers"),
            85 or 86 => ("🌨️", "Snow Showers"),
            95 or 96 or 99 => ("🌩️", "Thunderstorm"),
            _ => ("☀️", "Fair")
        };
    }

    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType != TouchEventType.TouchUp) return;

        float sx = DefaultSize.Width / DesignWidth;
        float sy = DefaultSize.Height / DesignHeight;

        if (localPoint.Y < 44f * sy && localPoint.X > DefaultSize.Width - 64f * sx)
        {
            UnitSystem = UnitSystem.StartsWith("Fahrenheit", StringComparison.OrdinalIgnoreCase)
                ? "Celsius (°C, km/h)"
                : "Fahrenheit (°F, mph)";
            Context?.RequestRender();
            return;
        }

        if (localPoint.Y < 44f * sy && localPoint.X < 140f * sx)
        {
            LayoutMode = LayoutMode switch
            {
                "Detailed" => "Daily Forecast",
                "Daily Forecast" => "Hourly Forecast",
                "Hourly Forecast" => "Current Only",
                "Current Only" => "Compact",
                _ => "Detailed"
            };
            Context?.RequestRender();
            return;
        }

        _ = FetchLiveWeatherAsync(force: true);
    }

    private static (string tempUnit, string speedUnit) ParseUnitSystem(string unitSystem)
    {
        return unitSystem switch
        {
            "Fahrenheit (°F, mph)" => ("°F", "mph"),
            "Celsius (°C, km/h)" or "" or null => ("°C", "km/h"),
            "Celsius (°C, mph)" => ("°C", "mph"),
            "Celsius (°C, m/s)" => ("°C", "m/s"),
            "Kelvin (K, m/s)" => ("K", "m/s"),
            _ => ("°C", "km/h"),
        };
    }

    private static string FormatTemp(double tempC, string tempUnit, bool shortFormat = false)
    {
        return tempUnit switch
        {
            "°F" => shortFormat ? $"{(tempC * 9.0 / 5.0 + 32.0):F0}°" : $"{(tempC * 9.0 / 5.0 + 32.0):F0}°F",
            "K" => $"{tempC + 273.15:F0} K",
            _ => shortFormat ? $"{tempC:F0}°" : $"{tempC:F1}°C",
        };
    }

    private static string FormatSpeed(double kmh, string speedUnit)
    {
        return speedUnit switch
        {
            "mph" => $"{(kmh * 0.621371):F0} mph",
            "m/s" => $"{(kmh / 3.6):F0} m/s",
            _ => $"{kmh:F0} km/h",
        };
    }

    private static bool IsZipCode(string query)
    {
        string trimmed = query.Trim();
        if (trimmed.Length != 5) return false;
        foreach (char c in trimmed)
        {
            if (!char.IsDigit(c)) return false;
        }
        return true;
    }

    private static bool IsCoordinatePair(string query)
    {
        string[] parts = query.Split(',');
        if (parts.Length != 2) return false;
        return double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            && double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private async Task FetchLiveWeatherAsync(bool force = false)
    {
        if (_isFetching) return;
        if (StaticSnapshot && _lastFetchTime != DateTime.MinValue && !force) return;
        if (!force && (DateTime.Now - _lastFetchTime).TotalMinutes < 5 && _lat.HasValue) return;

        _isFetching = true;
        try
        {
            string currentQuery = $"{LocationType}_{Location}_{Latitude}_{Longitude}";
            if (!_lat.HasValue || _lastLocationQuery != currentQuery || force)
            {
                _lastLocationQuery = currentQuery;

                if (double.TryParse(Latitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var latVal)
                    && double.TryParse(Longitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lonVal))
                {
                    _lat = latVal;
                    _lon = lonVal;
                    _resolvedCityName = string.IsNullOrWhiteSpace(CustomLabel) ? $"{latVal:F2}, {lonVal:F2}" : CustomLabel;
                }
                else if (IsCoordinatePair(Location))
                {
                    string[] parts = Location.Split(',');
                    _lat = double.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    _lon = double.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    _resolvedCityName = $"{_lat:F2}, {_lon:F2}";
                }
                else if (IsZipCode(Location))
                {
                    await GeocodeZipCodeAsync(Location.Trim()).ConfigureAwait(false);
                }
                else
                {
                    await GeocodeCityLocationAsync(Location).ConfigureAwait(false);
                }
            }

            if (!_lat.HasValue) return;

            string forecastUrl = $"https://api.open-meteo.com/v1/forecast?latitude={_lat:F4}&longitude={_lon:F4}&current_weather=true&hourly=temperature_2m,relativehumidity_2m,weathercode&daily=weathercode,temperature_2m_max,temperature_2m_min&apparent_temperature=true&timezone=auto";
            string json = await SharedHttpClient.GetStringAsync(forecastUrl).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("current_weather", out var currentWeather))
            {
                if (currentWeather.TryGetProperty("temperature", out var tempEl))
                    _currentTempC = tempEl.GetDouble();
                if (currentWeather.TryGetProperty("windspeed", out var windEl))
                    _windSpeedKmH = windEl.GetDouble();
                if (currentWeather.TryGetProperty("weathercode", out var codeEl))
                    _weatherCode = codeEl.GetInt32();
            }

            if (root.TryGetProperty("hourly", out var hourly)
                && hourly.TryGetProperty("temperature_2m", out var temps)
                && temps.GetArrayLength() > 0)
            {
                _feelsLikeC = temps[0].GetDouble();

                if (hourly.TryGetProperty("relativehumidity_2m", out var hums) && hums.GetArrayLength() > 0)
                    _humidity = hums[0].GetDouble();

                _hourlyForecasts.Clear();
                if (hourly.TryGetProperty("time", out var times) && hourly.TryGetProperty("weathercode", out var codes))
                {
                    int hLen = Math.Min(times.GetArrayLength(), temps.GetArrayLength());
                    for (int i = 0; i < Math.Min(hLen, 12); i++)
                    {
                        string timeStr = times[i].GetString() ?? "";
                        string label = timeStr.Length >= 16 ? timeStr[11..16] : $"{i}:00";
                        _hourlyForecasts.Add(new HourlyForecastItem(label, temps[i].GetDouble(), codes[i].GetInt32()));
                    }
                }
            }

            if (root.TryGetProperty("daily", out var daily))
            {
                if (daily.TryGetProperty("temperature_2m_max", out var maxes) && maxes.GetArrayLength() > 0)
                    _highTempC = maxes[0].GetDouble();
                if (daily.TryGetProperty("temperature_2m_min", out var mins) && mins.GetArrayLength() > 0)
                    _lowTempC = mins[0].GetDouble();

                _dailyForecasts.Clear();
                if (daily.TryGetProperty("time", out var dTimes) && daily.TryGetProperty("weathercode", out var dCodes))
                {
                    int dLen = Math.Min(dTimes.GetArrayLength(), maxes.GetArrayLength());
                    for (int i = 0; i < Math.Min(dLen, 7); i++)
                    {
                        string dateStr = dTimes[i].GetString() ?? "";
                        string dayName = DateTime.TryParse(dateStr, out var parsedDate) ? parsedDate.ToString("ddd") : $"Day {i + 1}";
                        _dailyForecasts.Add(new DailyForecastItem(
                            i == 0 ? "Today" : dayName,
                            maxes[i].GetDouble(),
                            mins[i].GetDouble(),
                            dCodes[i].GetInt32()));
                    }
                }
            }

            _lastFetchTime = DateTime.Now;
            _ = SaveCacheAsync();
            Context?.RequestRender();
        }
        catch (Exception ex)
        {
            Context?.LogError($"Weather fetch failed: {ex.Message}", ex);
        }
        finally
        {
            _isFetching = false;
        }
    }

    private async Task GeocodeCityLocationAsync(string query)
    {
        try
        {
            string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=1&language=en&format=json";
            string json = await SharedHttpClient.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var first = results[0];
                _lat = first.GetProperty("latitude").GetDouble();
                _lon = first.GetProperty("longitude").GetDouble();
                _resolvedCityName = first.TryGetProperty("name", out var n) ? n.GetString() ?? query : query;
                return;
            }
        }
        catch (Exception ex)
        {
            Context?.LogError($"Geocoding failed for '{query}': {ex.Message}", ex);
        }

        _lat = 40.7128;
        _lon = -74.0060;
        _resolvedCityName = string.IsNullOrWhiteSpace(query) ? "New York" : query;
    }

    private async Task GeocodeZipCodeAsync(string zipCode)
    {
        try
        {
            string url = $"https://api.zippopotam.us/us/{Uri.EscapeDataString(zipCode)}";
            string json = await SharedHttpClient.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            _lat = root.GetProperty("latitude").GetDouble();
            _lon = root.GetProperty("longitude").GetDouble();
            string city = root.TryGetProperty("place name", out var place) ? place.GetString() ?? "" : "";
            string state = root.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
            _resolvedCityName = string.IsNullOrWhiteSpace(state) ? city : $"{city}, {state}";
            return;
        }
        catch (Exception ex)
        {
            Context?.LogError($"ZIP geocoding failed for '{zipCode}': {ex.Message}", ex);
        }

        await GeocodeCityLocationAsync(zipCode).ConfigureAwait(false);
    }

    private async Task LoadCacheAsync()
    {
        try
        {
            string path = CachePath;
            if (!File.Exists(path)) return;
            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<WeatherCacheData>(json);
            if (data == null) return;
            _currentTempC = data.CurrentTempC;
            _feelsLikeC = data.FeelsLikeC;
            _humidity = data.Humidity;
            _windSpeedKmH = data.WindSpeedKmH;
            _weatherCode = data.WeatherCode;
            _highTempC = data.HighTempC;
            _lowTempC = data.LowTempC;
            _resolvedCityName = data.ResolvedCityName ?? "New York";
            _lat = data.Lat;
            _lon = data.Lon;
            _lastFetchTime = DateTime.Now;
            _dailyForecasts.Clear();
            _dailyForecasts.AddRange(data.DailyForecasts.Select(d => new DailyForecastItem(d.DayName, d.MaxTempC, d.MinTempC, d.WeatherCode)));
            _hourlyForecasts.Clear();
            _hourlyForecasts.AddRange(data.HourlyForecasts.Select(h => new HourlyForecastItem(h.TimeLabel, h.TempC, h.WeatherCode)));
        }
        catch (Exception ex)
        {
            Context?.LogError($"Weather cache load failed: {ex.Message}", ex);
        }
    }

    private async Task SaveCacheAsync()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var data = new WeatherCacheData
            {
                CurrentTempC = _currentTempC,
                FeelsLikeC = _feelsLikeC,
                Humidity = _humidity,
                WindSpeedKmH = _windSpeedKmH,
                WeatherCode = _weatherCode,
                HighTempC = _highTempC,
                LowTempC = _lowTempC,
                ResolvedCityName = _resolvedCityName,
                Lat = _lat,
                Lon = _lon,
                DailyForecasts = _dailyForecasts.Select(d => new DailyForecastData { DayName = d.DayName, MaxTempC = d.MaxTempC, MinTempC = d.MinTempC, WeatherCode = d.WeatherCode }).ToList(),
                HourlyForecasts = _hourlyForecasts.Select(h => new HourlyForecastData { TimeLabel = h.TimeLabel, TempC = h.TempC, WeatherCode = h.WeatherCode }).ToList()
            };
            string json = JsonSerializer.Serialize(data);
            await File.WriteAllTextAsync(CachePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Context?.LogError($"Weather cache save failed: {ex.Message}", ex);
        }
    }

    private sealed class WeatherCacheData
    {
        public double CurrentTempC { get; set; }
        public double FeelsLikeC { get; set; }
        public double Humidity { get; set; }
        public double WindSpeedKmH { get; set; }
        public int WeatherCode { get; set; }
        public double HighTempC { get; set; }
        public double LowTempC { get; set; }
        public string? ResolvedCityName { get; set; }
        public double? Lat { get; set; }
        public double? Lon { get; set; }
        public List<DailyForecastData> DailyForecasts { get; set; } = [];
        public List<HourlyForecastData> HourlyForecasts { get; set; } = [];
    }

    private sealed class DailyForecastData
    {
        public string DayName { get; set; } = "";
        public double MaxTempC { get; set; }
        public double MinTempC { get; set; }
        public int WeatherCode { get; set; }
    }

    private sealed class HourlyForecastData
    {
        public string TimeLabel { get; set; } = "";
        public double TempC { get; set; }
        public int WeatherCode { get; set; }
    }

    private static string TruncateText(string text, SKFont font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || font.MeasureText(text) <= maxWidth)
            return text;

        string ellipsis = "…";
        float ellipsisW = font.MeasureText(ellipsis);
        if (ellipsisW >= maxWidth) return "";

        int len = text.Length;
        while (len > 0)
        {
            string sub = text[..len] + ellipsis;
            if (font.MeasureText(sub) <= maxWidth)
                return sub;
            len--;
        }
        return ellipsis;
    }
}
