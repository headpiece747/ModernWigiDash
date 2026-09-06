using System.Runtime.InteropServices;

namespace ModernWigiDash.Sdk;

/// <summary>
/// One coherent snapshot of the frame delivery's accounting: the sent/dropped/
/// refused/failed trichotomy as one record. A new drop reason is added in one
/// place (this record + the counter field it reads), and a reader who wants the
/// verdict reads one value, not eight counters recombined into a judgment.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct FrameDeliveryStats(
    long FramesSent,
    long DroppedCount,
    long DroppedUnconfiguredCount,
    long DroppedNotReadyCount,
    long DroppedPoolCount,
    long DroppedCoalescedCount,
    long DroppedEncodeCount,
    long SendFailedCount,
    long SendRefusedCount);
