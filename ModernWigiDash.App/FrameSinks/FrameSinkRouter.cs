using SkiaSharp;

namespace ModernWigiDash.App.FrameSinks;

/// <summary>
/// Routes each composited frame to the first ready sink (WCF service over
/// direct USB), and owns the WCF-retry trigger: when neither sink can route
/// but the hardware engine has yielded to a running service, it invokes the
/// retry callback so service detection can be re-run (throttled upstream).
/// </summary>
public sealed class FrameSinkRouter : IDisposable
{
    private readonly IFrameSink _wcfSink;
    private readonly IFrameSink _usbSink;
    private readonly Action? _retryTrigger;
    private readonly Func<bool>? _isHardwareActive;

    /// <param name="wcfSink">Sink for the WCF service path (checked first).</param>
    /// <param name="usbSink">Sink for the direct-USB path.</param>
    /// <param name="retryTrigger">Invoked when no sink can route and the hardware
    /// has yielded to a service, to re-attempt service discovery.</param>
    /// <param name="isHardwareActive">Reports whether the USB engine has yielded
    /// to a running service (gate for the retry trigger).</param>
    public FrameSinkRouter(
        IFrameSink wcfSink,
        IFrameSink usbSink,
        Action? retryTrigger = null,
        Func<bool>? isHardwareActive = null)
    {
        _wcfSink = wcfSink;
        _usbSink = usbSink;
        _retryTrigger = retryTrigger;
        _isHardwareActive = isHardwareActive;
    }

    /// <summary>
    /// Sends <paramref name="frame"/> to the first ready sink. Returns true when
    /// a sink accepted the frame.
    /// </summary>
    public bool Send(SKBitmap frame)
    {
        if (_wcfSink.IsReady)
            return _wcfSink.SendFrame(frame);

        if (_usbSink.IsReady)
            return _usbSink.SendFrame(frame);

        // Neither sink can route. If the engine yielded to a service but our
        // one-shot WCF routing failed (e.g. the service was still starting),
        // retry detection (throttled) so frames don't get dropped forever.
        if (_isHardwareActive?.Invoke() == true)
            _retryTrigger?.Invoke();

        return false;
    }

    public void Dispose()
    {
        _wcfSink.Dispose();
        _usbSink.Dispose();
    }
}
