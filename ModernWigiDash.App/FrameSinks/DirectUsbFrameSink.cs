using ModernWigiDash.Hardware.Transport;
using SkiaSharp;

namespace ModernWigiDash.App.FrameSinks;

/// <summary>
/// Thin frame sink that pushes composited frames straight at the USB device
/// through the hardware engine (App-scoped direct-USB mode). Readiness tracks
/// the engine's connection state; simulation mode is never routable so the
/// router falls through to the WCF sink or the retry path.
/// </summary>
public sealed class DirectUsbFrameSink(IFrameSendDevice device) : IFrameSink
{
    /// <summary>
    /// True when the device is connected and not in simulation mode.
    /// </summary>
    public bool IsReady => device.IsConnected && !device.IsSimulationMode;

    /// <inheritdoc />
    public bool SendFrame(SKBitmap frame) => device.SendFrameBuffer(frame);

    /// <summary>
    /// Nothing to dispose — the engine is owned by MainWindow.
    /// </summary>
    public void Dispose()
    {
    }
}
