namespace ModernWigiDash.Widgets.Twitch;

using ModernWigiDash.Core.Rendering;
using SkiaSharp;

/// <summary>
/// Deep presentation canvas for the Twitch Chat Stream widget.
/// Encapsulates hoisted Skia paints, text wrapping cache, header string memoization,
/// and pixel layout for chat stream messages and connection status indicators.
/// </summary>
internal sealed class TwitchChatCanvas : IDisposable
{
    private readonly WrapCache _wrapCache = new();
    private readonly MemoSlot<string, string> _badgeMemo = new();
    private readonly MemoSlot<(ChatStatus Status, string Detail), string> _statusMemo = new();

    private readonly SKPaint _bgPaint = new() { IsAntialias = true };
    private readonly SKPaint _badgePaint = new() { IsAntialias = true };
    private readonly SKPaint _statusPaint = new() { IsAntialias = true };
    private readonly SKPaint _emptyPaint = new() { IsAntialias = true };
    private readonly SKPaint _userPaint = new() { Color = SKColors.White, IsAntialias = true };
    private readonly SKPaint _msgPaint = new() { IsAntialias = true };

    private string ChannelBadge(string channelName)
    {
        if (_badgeMemo.TryGet(channelName, out var value))
        {
            return value;
        }

        value = "#" + TwitchIrcMessages.NormalizeChannel(channelName).ToUpperInvariant();
        _badgeMemo.Set(channelName, value);
        return value;
    }

    private string StatusLine(TwitchChatPresentation.ChatState state)
    {
        var key = (state.Status, state.Detail);
        if (_statusMemo.TryGet(key, out var value))
        {
            return value;
        }

        value = TwitchChatPresentation.StatusText(state);
        _statusMemo.Set(key, value);
        return value;
    }

    /// <summary>
    /// Draws the chat: the rounded background, the channel badge and status
    /// header, and the message list. The visual facts arrive as one
    /// <see cref="TwitchChatFrame"/> (the widget's look); the connection's
    /// live state and messages keep their own places.
    /// </summary>
    public void Render(
        SKCanvas canvas,
        SKRect bounds,
        TwitchChatFrame frame,
        TwitchChatPresentation.ChatState state,
        IReadOnlyList<TwitchChatLine> messages)
    {
        var scale = Math.Clamp(Math.Min(bounds.Width / frame.DefaultSize.Width, bounds.Height / frame.DefaultSize.Height), 0.4f, 3f);
        if (float.IsNaN(scale) || scale <= 0) scale = 1f;

        var bg = SkColorCache.ColorOf(frame.BackgroundHex, WidgetPalette.ChatBackground);
        var headerColor = SkColorCache.ColorOf(frame.HeaderColorHex, SKColors.White);
        var msgColor = SkColorCache.ColorOf(frame.MessageColorHex, new SKColor(248, 250, 252));

        _bgPaint.Color = bg;
        canvas.DrawRoundRect(bounds, 14f * scale, 14f * scale, _bgPaint);

        float pad = 12f * scale;
        float baseFontSize = Math.Max(10f, Math.Min(32f, frame.FontSize));
        float titleSize = (baseFontSize + 2f) * scale;
        float statusSize = 13f * scale;

        var badgeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, titleSize);
        var statusFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, statusSize);
        _badgePaint.Color = headerColor;

        float top = bounds.Top + pad;
        string channelBadge = ChannelBadge(frame.ChannelName);
        canvas.DrawTextWithFallback(channelBadge, bounds.Left + pad, top + titleSize, badgeFont, _badgePaint, SKTextAlign.Left);

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

        float msgSize = baseFontSize * scale;
        float userSize = (Math.Max(10f, baseFontSize - 2f)) * scale;
        float lineHeight = msgSize * 1.4f;
        float userLineHeight = userSize * 1.35f;

        if (messages.Count == 0)
        {
            var emptyFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, msgSize);
            _emptyPaint.Color = headerColor.WithAlpha(130);
            var hint = TwitchChatPresentation.EmptyHint(state.Status, frame.AutoConnect);
            canvas.DrawTextWithFallback(hint, contentBounds.Left, contentBounds.Top + msgSize, emptyFont, _emptyPaint, SKTextAlign.Left);
            canvas.Restore();
            return;
        }

        float cursor = contentBounds.Bottom;

        var userFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, userSize);
        var msgFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, msgSize);
        _msgPaint.Color = msgColor;

        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
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

    /// <inheritdoc/>
    public void Dispose()
    {
        _bgPaint.Dispose();
        _badgePaint.Dispose();
        _statusPaint.Dispose();
        _emptyPaint.Dispose();
        _userPaint.Dispose();
        _msgPaint.Dispose();
    }
}
