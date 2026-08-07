using SkiaSharp;

namespace ModernWigiDash.Sdk;

/// <summary>
/// Result of handing a frame to a sink or the delivery pipeline. Truthful
/// where the old bool contract lied: <see cref="Queued"/> means the frame
/// entered the pipeline for delivery; <see cref="Dropped"/> means it did not
/// (sink not ready, pool exhausted, or no bytes produced).
/// </summary>
public enum FrameDeliveryResult
{
    Queued,
    Dropped
}

/// <summary>
/// A destination for composited frames. Each sink owns the full
/// encode→pool→coalesce→deliver lifecycle for one transport
/// (<see cref="FrameDelivery"/> instances bound to WCF or the USB engine).
/// </summary>
public interface IFrameSink : IDisposable
{
    /// <summary>
    /// True when the sink can currently deliver frames. A sink that is not
    /// ready must be skipped by the router (e.g. WCF unbound, USB not connected).
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Queues a composited frame for delivery to the sink's transport.
    /// Returns <see cref="FrameDeliveryResult.Queued"/> when the frame entered
    /// the pipeline; <see cref="FrameDeliveryResult.Dropped"/> when it could not
    /// (not ready, or the pipeline rejected it).
    /// </summary>
    FrameDeliveryResult SendFrame(SKBitmap frame);
}
