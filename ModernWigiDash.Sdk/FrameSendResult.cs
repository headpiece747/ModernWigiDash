namespace ModernWigiDash.Sdk;

/// <summary>
/// The result of the frame send seam — the truthful vocabulary the delivery
/// pipeline uses to tell what happened to a frame handed to the transport:
/// <see cref="Sent"/> reached the device, <see cref="Refused"/> was declined
/// before the wire (no live connection, or the frame fails the transport's
/// size contract), and <see cref="Failed"/> was attempted but the framing or
/// bulk transfer failed. The refusal/failed split exists so the delivery's
/// drop accounting can tell a refusal from a broken pipe.
/// </summary>
public enum FrameSendResult
{
    /// <summary>The frame was written to the device.</summary>
    Sent,

    /// <summary>The transport declined the frame without touching the wire —
    /// no live connection, or the frame fails the transport's size contract.</summary>
    Refused,

    /// <summary>The frame reached the transport, but the framing or bulk
    /// transfer failed (a broken pipe).</summary>
    Failed
}
