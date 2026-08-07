using System.Threading.Channels;
using ModernWigiDash.Hardware;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.App.FrameSinks;

/// <summary>
/// Frame sink that delivers composited frames to the WigiDash Windows Service
/// over WCF. Owns the whole outbound pipeline that used to live in MainWindow:
/// encode (RGB565) → pooled exact-size buffer → bounded DropOldest channel →
/// background sender loop that coalesces to the latest frame → release.
/// </summary>
public sealed class WcfFrameSink : IFrameSink
{
    private readonly FrameBufferPool _pool;
    private readonly Channel<byte[]> _frameChannel =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private CancellationTokenSource? _frameSenderCts;
    private byte[]? _rgb565PoolBuffer;
    private volatile Func<byte[], bool>? _send;

    /// <summary>
    /// True when a WCF client is bound via <see cref="SetSend"/>.
    /// </summary>
    public bool IsReady => _send != null;

    /// <param name="pool">Exact-size buffer pool for the RGB565 frames. When
    /// omitted, a default 4-buffer pool sized for the display is created.</param>
    public WcfFrameSink(FrameBufferPool? pool = null)
    {
        _pool = pool ?? new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4);
        _frameSenderCts = new CancellationTokenSource();
        _ = Task.Run(() => FrameSenderLoop(_frameSenderCts.Token));
    }

    /// <summary>
    /// Binds (or unbinds, with null) the WCF send capability. Called when the
    /// service connects or faults; a null send makes the sink not ready.
    /// </summary>
    public void SetSend(Func<byte[], bool>? send)
    {
        _send = send;
    }

    /// <inheritdoc />
    public bool SendFrame(SKBitmap frame)
    {
        if (_send == null || frame == null)
            return false;

        FrameEncoder.ConvertToRgb565(frame, ref _rgb565PoolBuffer);
        byte[]? rgb565 = _rgb565PoolBuffer;
        byte[]? frameCopy = _pool.Acquire();
        if (rgb565 != null && frameCopy != null)
        {
            Buffer.BlockCopy(rgb565, 0, frameCopy, 0, rgb565.Length);
            if (!_frameChannel.Writer.TryWrite(frameCopy))
            {
                // Channel full — frame dropped; return the buffer to the pool
                _pool.Release(frameCopy);
            }
        }

        return true;
    }

    /// <summary>
    /// Drains the frame channel and sends the latest coalesced frame via WCF.
    /// Stale buffered frames are dropped and their pooled buffers returned,
    /// so the display always shows real-time content after a backlog.
    /// </summary>
    private async Task FrameSenderLoop(CancellationToken ct)
    {
        var reader = _frameChannel.Reader;
        int sentCount = 0;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                // Drain all queued frames, keep only the latest; return the
                // dropped frames' pooled buffers to the pool.
                byte[]? latestFrame = ChannelFrameCoalescer.DrainToLatest(reader, _pool.Release);

                if (latestFrame == null) continue;

                try
                {
                    bool ok = _send?.Invoke(latestFrame) == true;
                    sentCount++;
                    if (sentCount <= 5 || sentCount % 120 == 0)
                        FileLog.Write($"[WCF] Frame #{sentCount} sent ({latestFrame.Length} bytes) ok={ok}");
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[WCF] Frame send failed: {ex.Message}");
                }
                finally
                {
                    _pool.Release(latestFrame);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: frame sender loop cancelled during shutdown
            System.Diagnostics.Debug.WriteLine("Frame sender loop cancelled during shutdown");
        }
    }

    public void Dispose()
    {
        _frameSenderCts?.Cancel();
        _frameSenderCts?.Dispose();
        _frameSenderCts = null;
        _send = null;
    }
}
