namespace ModernWigiDash.Sdk;

/// <summary>
/// Result of handing a frame to the delivery pipeline. Truthful where the old
/// bool contract lied: <see cref="Queued"/> means the frame entered the
/// pipeline for delivery; <see cref="Dropped"/> means it did not (delivery not
/// ready, pool exhausted, or no bytes produced).
/// </summary>
public enum FrameDeliveryResult
{
    Queued,
    Dropped
}
