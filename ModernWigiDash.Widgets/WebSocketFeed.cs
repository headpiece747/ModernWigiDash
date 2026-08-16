using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The WebSocket seam behind the price feeds. The manager's Binance and Finnhub
/// loops only know this surface — connect, send text, receive one complete text
/// message per await, abort — so the loops are drivable by an in-memory feed in
/// tests instead of a live socket.
/// </summary>
internal interface IWebSocketFeed : IDisposable
{
    bool IsOpen { get; }

    Task ConnectAsync(Uri uri, CancellationToken ct);

    Task SendTextAsync(string payload, CancellationToken ct);

    /// <summary>Returns the next complete text message, or null when the feed closed.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken ct);

    void Abort();
}

/// <summary>
/// <see cref="ClientWebSocket"/> adapter for <see cref="IWebSocketFeed"/>.
/// Owned by the feed loop; text messages are reassembled from fragments so
/// callers receive one complete message per await.
/// </summary>
internal sealed class ClientWebSocketFeed : IWebSocketFeed
{
    private readonly ClientWebSocket _ws = new();
    private readonly byte[] _buffer = new byte[16384];
    // Reused across messages: receives are sequential per feed instance (the
    // loop awaits one message at a time), so one pooled list is safe and the
    // per-message List<byte> allocation disappears from the hot path.
    private readonly List<byte> _receiveBytes = new(1024);

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public Task ConnectAsync(Uri uri, CancellationToken ct) => _ws.ConnectAsync(uri, ct);

    public Task SendTextAsync(string payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        // Accumulate raw bytes across fragments and decode ONCE at the message
        // end — decoding per fragment would corrupt multi-byte UTF-8 sequences
        // (emoji, accents) that straddle a fragment boundary.
        _receiveBytes.Clear();
        while (_ws.State == WebSocketState.Open)
        {
            var result = await _ws.ReceiveAsync(_buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            _receiveBytes.AddRange(_buffer.AsSpan(0, result.Count));
            if (result.EndOfMessage) return Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_receiveBytes));
        }
        return null;
    }

    public void Abort() => _ws.Abort();

    public void Dispose() => _ws.Dispose();
}
