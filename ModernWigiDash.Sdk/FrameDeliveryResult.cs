namespace ModernWigiDash.Sdk;

/// <summary>
/// Result of handing a frame to the delivery pipeline. Truthful where the old
/// bool contract lied: <see cref="Queued"/> means the frame entered the
/// pipeline for delivery; <see cref="Dropped"/> means it did not (delivery not
/// ready, pool exhausted, or no bytes produced).
/// </summary>
public enum FrameDeliveryResult
{
    /// <summary>The frame entered the pipeline (encoded and queued for delivery).</summary>
    Queued,

    /// <summary>The frame did not enter the pipeline — delivery not ready,
    /// pool exhausted, or the encode failed (counted in the drop counters).</summary>
    Dropped
}
