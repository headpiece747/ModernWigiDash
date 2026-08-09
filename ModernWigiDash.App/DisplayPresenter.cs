using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.App;

/// <summary>
/// Owns the frame-presentation pipeline: one FrameDelivery bound to the direct-USB
/// engine, with the encode→pool→coalesce→pace policy and the readiness gate.
/// MainWindow sends the freshly composed frame; the presenter decides delivery.
/// </summary>
public sealed class DisplayPresenter : IDisposable
{
    private readonly FrameDelivery _delivery;

    public DisplayPresenter(Func<byte[], bool> send, Func<bool> isReady, Action<string>? log = null)
    {
        _delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            pool: new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4),
            send: send,
            isReady: isReady,
            log: log);
    }

    /// <summary>Forwards to the delivery: true when frames can currently route to the display.</summary>
    public bool IsReady => _delivery.IsReady;

    /// <summary>Frames successfully handed to the transport.</summary>
    public long FramesSent => _delivery.FramesSent;

    /// <summary>Queues the freshly composed frame for encode→coalesce→pace→send.</summary>
    public void Send(SKBitmap frame) => _delivery.SendFrame(frame);

    public void Dispose() => _delivery.Dispose();
}
