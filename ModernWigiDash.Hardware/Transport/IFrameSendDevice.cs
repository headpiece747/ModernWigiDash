using SkiaSharp;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The minimum device surface a frame sink needs to push frames at the USB
/// display and report its current routable state. Implemented by
/// <see cref="DisplayDeviceEngine"/>; faked in tests so sink routing (direct
/// vs WCF) can be verified without hardware.
/// </summary>
public interface IFrameSendDevice
{
    /// <summary>True when the device is connected and frames can be queued.</summary>
    bool IsConnected { get; }

    /// <summary>True when the device is in simulation mode (no physical display).</summary>
    bool IsSimulationMode { get; }

    /// <summary>
    /// Queues a frame for delivery to the device. Returns true when the frame
    /// was queued; false when the device is not connected, disposed, or the
    /// bounded queue was full (frame dropped).
    /// </summary>
    bool SendFrameBuffer(SKBitmap frame);
}
