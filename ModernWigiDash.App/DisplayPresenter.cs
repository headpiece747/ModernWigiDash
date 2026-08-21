using ModernWigiDash.Hardware.Transport;
using SkiaSharp;

namespace ModernWigiDash.App;

/// <summary>
/// Owns the frame-presentation pipeline: one FrameDelivery bound to the direct-USB
/// engine, with the encode→pool→coalesce→pace policy and the readiness gate.
/// MainWindow sends the freshly composed frame; the presenter decides delivery.
/// </summary>
internal sealed class DisplayPresenter : IDisposable
{
    private readonly FrameDelivery _delivery;

    public DisplayPresenter(Func<byte[], FrameSendResult> send, Func<bool> isReady, Action<string>? log = null)
    {
        _delivery = FrameDelivery.Create(
            encoder: new SkiaRgb565Encoder(),
            send: send,
            isReady: isReady,
            log: log);
    }

    /// <summary>Queues the freshly composed frame for encode→coalesce→pace→send.</summary>
    public void Send(SKBitmap frame) => _delivery.Push(frame);

    /// <summary>
    /// The compose-gate decision: true when a new frame may be composed — the
    /// previous send is finished and the transport is ready. The FramePump
    /// consults this each tick instead of re-deriving the delivery's send
    /// state at its wiring; composing during the write is dead CPU (the
    /// display cannot take another frame anyway).
    /// </summary>
    public bool ShouldCompose => !_delivery.IsSendInFlight;

    public void Dispose() => _delivery.Dispose();
}
