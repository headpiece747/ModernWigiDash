using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The frame path's wire: the reused header buffer, the two control writes that
/// bracket the payload, and the abort on a failed bulk write.
///
/// One method, and the 30 FPS allocation story behind it: the zero-alloc array
/// fast path, the reused header buffer, and the in-place header byte writes all
/// live here rather than in the transport, so "what does a frame cost?" is
/// answerable by reading one short file instead of one long one (the architecture
/// review's transport candidate, 2026-09-27).
/// </summary>
internal sealed class DisplayFrameSender(DisplayWire wire, ILogger logger)
{
    // The send-skipped log rides the ILogger, not FileLog, so it keeps a bare
    // LogCadence instead of a DiagLog.
    private readonly LogCadence _sendFrameSkippedLog = new(60);

    // Reused header buffer: the header belongs to the one sender thread (the
    // touch buffer is the poll thread's, a distinct array), so no per-frame
    // allocation on the 30 FPS path and no lock to hold. The wire format is
    // owned once by DisplayProtocolConstants.BuildFrameHeader (the cold blank-
    // framebuffer path shares it); the in-place byte writes avoid
    // BitConverter's per-field byte[4] allocations.
    private readonly byte[] _frameHeader = new byte[DisplayProtocolConstants.FrameHeaderDataSize];

    /// <summary>
    /// Sends one frame: header control write, then the bulk payload, then the
    /// abort command if the payload failed so the device does not sit waiting on
    /// a frame that will never arrive.
    /// </summary>
    /// <param name="frameBuffer">The RGB565 payload. The transport forwards what
    /// it is handed; the size rule has one owner here, where
    /// <see cref="DisplayProtocolConstants.FrameBufferSize"/> lives.</param>
    /// <returns>The frame's verdict.</returns>
    internal FrameSendResult Send(ReadOnlyMemory<byte> frameBuffer)
    {
        if (!wire.IsConnected)
        {
            if (_sendFrameSkippedLog.Due())
                logger.LogWarning("SendFrame SKIPPED: not connected");
            return FrameSendResult.Refused;
        }

        if (frameBuffer.Length < DisplayProtocolConstants.FrameBufferSize)
        {
            logger.LogWarning("Frame buffer too small: {Len} < {Req}",
                frameBuffer.Length, DisplayProtocolConstants.FrameBufferSize);
            return FrameSendResult.Refused;
        }

        try
        {
            // Zero-alloc fast path: the frame pipeline hands us byte[] buffers,
            // so the memory is almost always an array segment already — reuse it
            // instead of copying 1.2 MB per frame. Fall back to a copy only for
            // genuine non-array memory.
            byte[] frameArray;
            if (!MemoryMarshal.TryGetArray(frameBuffer, out ArraySegment<byte> segment) ||
                segment.Offset != 0 ||
                segment.Count != frameBuffer.Length)
            {
                frameArray = frameBuffer.ToArray();
            }
            else
            {
                frameArray = segment.Array!;
            }

            DisplayProtocolConstants.BuildFrameHeader(_frameHeader, frameArray.Length);

            if (!wire.ControlOut(DisplayProtocolConstants.CmdFrameHeader, wValue: 0, _frameHeader))
            {
                return FrameSendResult.Failed;
            }

            if (!wire.WriteBulkData(frameArray))
            {
                wire.ControlOut(DisplayProtocolConstants.CmdFrameAbort, 0, null);
                return FrameSendResult.Failed;
            }

            return FrameSendResult.Sent;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SendFrame FAILED");
            return FrameSendResult.Failed;
        }
    }
}
