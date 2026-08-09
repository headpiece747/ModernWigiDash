using System.IO;
using System.Runtime.InteropServices;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// PM_METRIC / PM_STAT values as laid out in PresentMonAPI.h v2.5.1, plus the
/// dynamic-query window/offset tuning constants.
/// </summary>
internal static class PresentMonProtocol
{
    public const uint MetricCpuFrameTime = 8;
    public const uint MetricPresentedFps = 12;
    public const uint MetricGpuBusy = 14;
    public const uint MetricBetweenPresents = 78;
    public const uint StatNone = 0;
    public const uint StatAvg = 1;
    public const uint StatPercentile01 = 5;

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

/// <summary>
/// Mirror of the PresentMon PM_VERSION struct (uint16 major/minor/patch).
/// The API fills a struct, NOT three separate fields — marshaling it as
/// three ints misreads the alignment and the version check fails (the
/// widget then shows "Install the PresentMon Service").
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PmVersion
{
    public ushort Major;
    public ushort Minor;
    public ushort Patch;
}

/// <summary>
/// Platform seam for <see cref="PresentMonApiProbe"/>: finds and loads the
/// native library and resolves exports. Tests substitute a fake to drive the
/// unavailable-reason branches without the real PresentMonAPI2.dll.
/// </summary>
internal interface IPresentMonLibraryLoader
{
    /// <summary>
    /// Loads the first existing candidate path. Returns the module handle, or
    /// null with <paramref name="failureReason"/> set when a candidate exists
    /// but cannot be loaded, or null with a null reason when no candidate
    /// exists at all.
    /// </summary>
    IntPtr? LoadLibrary(string[] candidatePaths, out string? failureReason);

    /// <summary>Resolves a named export; null when the export is missing.</summary>
    IntPtr? GetExport(IntPtr library, string name);
}

/// <summary>Default loader over <see cref="NativeLibrary"/>.</summary>
internal sealed class NativePresentMonLibraryLoader : IPresentMonLibraryLoader
{
    public static readonly NativePresentMonLibraryLoader Instance = new();

    private NativePresentMonLibraryLoader()
    {
    }

    public IntPtr? LoadLibrary(string[] candidatePaths, out string? failureReason)
    {
        failureReason = null;
        foreach (string path in candidatePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                return NativeLibrary.Load(path);
            }
            catch (Exception)
            {
                failureReason = $"PresentMonAPI2.dll at '{path}' could not be loaded.";
                return null;
            }
        }
        return null;
    }

    public IntPtr? GetExport(IntPtr library, string name)
    {
        try
        {
            return NativeLibrary.GetExport(library, name);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// One-shot load of the PresentMon API v3 interop surface. Owns the
/// unavailable-reason policy: a missing library, a missing export, or a
/// non-v3 API generation (checked via <c>pmGetApiVersion</c>) each map to the
/// reason string the producer surfaces. Extracted from <see cref="PresentMonNative"/>
/// so those load-failure branches are plain unit tests against a fake loader
/// (ADR-0003) instead of an STA/WPF harness.
/// </summary>
internal sealed class PresentMonApiProbe
{
    private const string NotFoundReason =
        "PresentMonAPI2.dll not found. Install the PresentMon Service (C:\\Program Files\\Intel\\PresentMonSharedService).";
    private const string MissingExportsReason =
        "PresentMonAPI2.dll is missing required exports (incompatible version).";

    public IntPtr? Library { get; }
    public string? FailureReason { get; }

    public PmOpenSession? OpenSessionFn { get; }
    public PmCloseSession? CloseSessionFn { get; }
    public PmStartTrackingProcess? StartTrackingFn { get; }
    public PmRegisterDynamicQuery? RegisterDynamicQueryFn { get; }
    public PmFreeDynamicQuery? FreeDynamicQueryFn { get; }
    public PmPollDynamicQuery? PollDynamicQueryFn { get; }
    public PmRegisterFrameQuery? RegisterFrameQueryFn { get; }
    public PmConsumeFrames? ConsumeFramesFn { get; }
    public PmFreeFrameQuery? FreeFrameQueryFn { get; }
    public PmGetApiVersion? GetApiVersionFn { get; }

    public PresentMonApiProbe(IPresentMonLibraryLoader loader)
    {
        if (loader.LoadLibrary(PresentMonLibraryCandidates(), out string? loadFailure) is not { } lib)
        {
            FailureReason = loadFailure ?? NotFoundReason;
            return;
        }

        Library = lib;
        OpenSessionFn = Resolve<PmOpenSession>(loader, lib, "pmOpenSession");
        CloseSessionFn = Resolve<PmCloseSession>(loader, lib, "pmCloseSession");
        StartTrackingFn = Resolve<PmStartTrackingProcess>(loader, lib, "pmStartTrackingProcess");
        RegisterDynamicQueryFn = Resolve<PmRegisterDynamicQuery>(loader, lib, "pmRegisterDynamicQuery");
        FreeDynamicQueryFn = Resolve<PmFreeDynamicQuery>(loader, lib, "pmFreeDynamicQuery");
        PollDynamicQueryFn = Resolve<PmPollDynamicQuery>(loader, lib, "pmPollDynamicQuery");
        RegisterFrameQueryFn = Resolve<PmRegisterFrameQuery>(loader, lib, "pmRegisterFrameQuery");
        ConsumeFramesFn = Resolve<PmConsumeFrames>(loader, lib, "pmConsumeFrames");
        FreeFrameQueryFn = Resolve<PmFreeFrameQuery>(loader, lib, "pmFreeFrameQuery");
        GetApiVersionFn = Resolve<PmGetApiVersion>(loader, lib, "pmGetApiVersion");

        bool anyMissing = OpenSessionFn is null || CloseSessionFn is null || StartTrackingFn is null
            || RegisterDynamicQueryFn is null || FreeDynamicQueryFn is null || PollDynamicQueryFn is null
            || RegisterFrameQueryFn is null || ConsumeFramesFn is null || FreeFrameQueryFn is null
            || GetApiVersionFn is null;
        if (anyMissing)
        {
            FailureReason = MissingExportsReason;
            return;
        }

        // The PmStatus enum and PM_QUERY_ELEMENT layout this code targets are
        // v3-shaped; the file version (3.0.3) is the service protocol version —
        // require the API generation, not a patch match.
        if (GetApiVersionFn!(out PmVersion version) != PmStatus.Success || version.Major != 3)
        {
            FailureReason = $"PresentMonAPI2.dll version {version.Major}.{version.Minor}.{version.Patch} is not supported (v3.x required).";
        }
    }

    internal static string[] PresentMonLibraryCandidates() =>
    [
        // Shared-service layout used by the MSI since v2.3.1: the client API
        // ships next to PresentMonService.exe.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMonSharedService", "PresentMonAPI2.dll"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Intel", "PresentMonSharedService", "PresentMonAPI2.dll"),
        // SDK layout: header + loader live here; some installs also drop the API dll.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMon", "SDK", "PresentMonAPI2.dll"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Intel", "PresentMon", "SDK", "PresentMonAPI2.dll"),
    ];

    private static T? Resolve<T>(IPresentMonLibraryLoader loader, IntPtr library, string name) where T : Delegate
    {
        if (loader.GetExport(library, name) is not { } pointer)
        {
            return null;
        }
        try
        {
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
