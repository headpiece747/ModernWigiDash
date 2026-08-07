using SkiaSharp;

namespace ModernWigiDash.App.FrameSinks;

/// <summary>
/// A destination for composited frames. Each sink owns the full
/// encode→pool→coalesce→deliver lifecycle for one transport
/// (<see cref="WcfFrameSink"/>, <see cref="DirectUsbFrameSink"/>).
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
    /// Returns true when the frame was accepted; false when the sink is not
    /// ready or dropped it.
    /// </summary>
    bool SendFrame(SKBitmap frame);
}
