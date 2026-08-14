using System.Runtime.InteropServices;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// PM_METRIC / PM_STAT values as laid out in PresentMonAPI.h, plus the
/// dynamic-query window/offset tuning constants. Metric ids are stable across
/// API generations (they are enum values, not offsets); the installed
/// service's own introspection (see <see cref="PresentMonMetricCatalog"/>)
/// validates that a metric and stat are actually registrable before the
/// dynamic query is built.
/// </summary>
internal static class PresentMonProtocol
{
    public const uint MetricCpuFrameTime = 8;
    public const uint MetricDisplayedFps = 11;
    public const uint MetricPresentedFps = 12;
    public const uint MetricGpuTime = 13;
    public const uint MetricGpuBusy = 14;
    public const uint MetricDroppedFrames = 16;
    public const uint MetricPresentMode = 20;
    public const uint MetricBetweenPresents = 78;
    public const uint StatNone = 0;
    public const uint StatAvg = 1;
    public const uint StatPercentile01 = 5;
    public const uint StatNewestPoint = 12;

    /// <summary>Rolling measurement window for the dynamic query (ms).</summary>
    public const double DynamicQueryWindowMs = 1000;

    /// <summary>
    /// How far back from "now" the metric window's far edge sits (ms). Matches
    /// the PresentMon Capture app's convention of window + 20ms: the metric is
    /// evaluated on a window fully in the past, so the rolling statistic is
    /// always computable. An offset of 0 puts the far edge exactly at "now",
    /// where the window never closes and every metric returns 0.
    /// </summary>
    public const double DynamicQueryOffsetMs = 1020;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate PmStatus PmOpenSession(out IntPtr pHandle);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate PmStatus PmCloseSession(IntPtr handle);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate PmStatus PmStartTrackingProcess(IntPtr handle, uint processId);
internal delegate PmStatus PmStopTrackingProcess(IntPtr handle, uint processId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate PmStatus PmRegisterDynamicQuery(
    IntPtr sessionHandle, out IntPtr pHandle, [In, Out] PresentMonQueryElement[] pElements,
    ulong numElements, double windowSizeMs, double metricOffsetMs);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate PmStatus PmFreeDynamicQuery(IntPtr handle);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate PmStatus PmPollDynamicQuery(IntPtr handle, uint processId, [Out] byte[] pBlob, ref uint numSwapChains);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate PmStatus PmRegisterFrameQuery(
    IntPtr sessionHandle, out IntPtr pHandle, [In, Out] PresentMonQueryElement[] pElements,
    ulong numElements, out uint pBlobSize);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate PmStatus PmConsumeFrames(IntPtr handle, uint processId, [Out] byte[] pBlobs, ref uint pNumFramesToRead);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate PmStatus PmFreeFrameQuery(IntPtr handle);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate PmStatus PmGetApiVersion(out PmVersion version);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate PmStatus PmGetIntrospectionRoot(IntPtr handle, out IntPtr ppRoot);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate PmStatus PmFreeIntrospectionRoot(IntPtr pRoot);

/// <summary>
/// Mirror of the PresentMon PM_VERSION struct (PresentMonAPI.h).
///
/// Layout is critical: the native side fills the WHOLE struct — major, minor,
/// patch, plus a 22-byte tag, 8-byte hash and 4-byte config string. A mirror
/// that only declared the first three ushorts made pmGetApiVersion write 34
/// bytes past the marshalled buffer, which the JIT's stack-overrun check
/// caught at method return as fail-fast 0xC0000409 (STATUS_STACK_BUFFER_OVERRUN).
/// The full 40-byte layout below keeps the version gate reading major/minor/
/// patch from the correct offsets while giving the native write its full space.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PmVersion
{
    public ushort Major;
    public ushort Minor;
    public ushort Patch;

    /// <summary>Build/config tag string (not consumed by the version gate).</summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 22)]
    public byte[] Tag;

    /// <summary>Build hash string (not consumed by the version gate).</summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public byte[] Hash;

    /// <summary>Build config string (not consumed by the version gate).</summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public byte[] Config;
}
