using System.IO;
using System.Runtime.InteropServices;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// Runtime-loaded PresentMon API v3.4 interop (ADR-0003). Loads
/// <c>PresentMonAPI2.dll</c> from the PresentMon SDK install directory at
/// runtime — never ships its own copy, because the client↔service binary
/// protocol isn't backward-guaranteed (issue #383). All function pointers are
/// resolved at load time; a missing library or missing export marks the seam
/// unavailable and the producer degrades to the widget's graceful state.
/// </summary>
public sealed class PresentMonNative : IPresentMonNative
{
    // PresentMon status codes (PresentMonAPI.h — success is zero, first in
    // enum; the rest are positional, NOT named-assigned).
    internal enum PmStatus
    {
        Success = 0,
        Failure = 1,
        BadArgument = 2,
        BadHandle = 3,
        ServiceError = 4,
        InvalidEtlFile = 5,
        InvalidPid = 6,
        AlreadyTrackingProcess = 7,
        UnableToCreateNsm = 8,
        InvalidAdapterId = 9,
        OutOfRange = 10,
        InsufficientBuffer = 11,
        PipeError = 12,
        SessionNotOpen = 13,
        MiddlewareMissingPath = 14,
        NonexistentFilePath = 15,
        MiddlewareInvalidSignature = 16,
        MiddlewareMissingEndpoint = 17,
        MiddlewareVersionLow = 18,
        MiddlewareVersionHigh = 19,
        MiddlewareServiceMismatch = 20,
        QueryMalformed = 21,
        ModeMismatch = 22,
        FeatureDisabled = 23,
    }

    // PM_METRIC / PM_STAT values as laid out in PresentMonAPI.h v2.5.1.
    private const uint MetricCpuFrameTime = 8;
    private const uint MetricPresentedFps = 12;
    private const uint MetricGpuBusy = 14;
    private const uint MetricBetweenPresents = 78;
    private const uint StatNone = 0;
    private const uint StatAvg = 1;
    private const uint StatPercentile01 = 5;

    /// <summary>Rolling measurement window for the dynamic query (ms).</summary>
    private const double DynamicQueryWindowMs = 1000;

    /// <summary>
    /// How far back from "now" the metric window's far edge sits (ms). Matches
    /// the PresentMon Capture app's convention of window + 20ms: the metric is
    /// evaluated on a window fully in the past, so the rolling statistic is
    /// always computable. An offset of 0 puts the far edge exactly at "now",
    /// where the window never closes and every metric returns 0.
    /// </summary>
    private const double DynamicQueryOffsetMs = 1020;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate PmStatus PmOpenSession(out IntPtr pHandle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate PmStatus PmCloseSession(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate PmStatus PmStartTrackingProcess(IntPtr handle, uint processId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate PmStatus PmRegisterDynamicQuery(
        IntPtr sessionHandle, out IntPtr pHandle, [In, Out] PresentMonQueryElement[] pElements,
        ulong numElements, double windowSizeMs, double metricOffsetMs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate PmStatus PmFreeDynamicQuery(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate PmStatus PmPollDynamicQuery(IntPtr handle, uint processId, [Out] byte[] pBlob, ref uint numSwapChains);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate PmStatus PmRegisterFrameQuery(
        IntPtr sessionHandle, out IntPtr pHandle, [In, Out] PresentMonQueryElement[] pElements,
        ulong numElements, out uint pBlobSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate PmStatus PmConsumeFrames(IntPtr handle, uint processId, [Out] byte[] pBlobs, ref uint pNumFramesToRead);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate PmStatus PmFreeFrameQuery(IntPtr handle);

    private static readonly IntPtr? Library;
    private static readonly PmOpenSession? OpenSessionFn;
    private static readonly PmCloseSession? CloseSessionFn;
    private static readonly PmStartTrackingProcess? StartTrackingFn;
    private static readonly PmRegisterDynamicQuery? RegisterDynamicQueryFn;
    private static readonly PmFreeDynamicQuery? FreeDynamicQueryFn;
    private static readonly PmPollDynamicQuery? PollDynamicQueryFn;
    private static readonly PmRegisterFrameQuery? RegisterFrameQueryFn;
    private static readonly PmConsumeFrames? ConsumeFramesFn;
    private static readonly PmFreeFrameQuery? FreeFrameQueryFn;
    private static readonly string? LoadFailureReason;

    private IntPtr _session;
    private IntPtr _dynamicQuery;
    private IntPtr _frameQuery;
    private PresentMonQueryElement[]? _dynamicElements;
    private PresentMonQueryElement[]? _frameElements;
    private int _chainStride;
    private int _frameBlobSize;

    static PresentMonNative()
    {
        Library = LoadLibraryFromSdk(out string? loadFailure);
        LoadFailureReason = loadFailure;

        if (Library is { } lib)
        {
            OpenSessionFn = Resolve<PmOpenSession>(lib, "pmOpenSession");
            CloseSessionFn = Resolve<PmCloseSession>(lib, "pmCloseSession");
            StartTrackingFn = Resolve<PmStartTrackingProcess>(lib, "pmStartTrackingProcess");
            RegisterDynamicQueryFn = Resolve<PmRegisterDynamicQuery>(lib, "pmRegisterDynamicQuery");
            FreeDynamicQueryFn = Resolve<PmFreeDynamicQuery>(lib, "pmFreeDynamicQuery");
            PollDynamicQueryFn = Resolve<PmPollDynamicQuery>(lib, "pmPollDynamicQuery");
            RegisterFrameQueryFn = Resolve<PmRegisterFrameQuery>(lib, "pmRegisterFrameQuery");
            ConsumeFramesFn = Resolve<PmConsumeFrames>(lib, "pmConsumeFrames");
            FreeFrameQueryFn = Resolve<PmFreeFrameQuery>(lib, "pmFreeFrameQuery");

            bool anyMissing = OpenSessionFn is null || CloseSessionFn is null || StartTrackingFn is null
                || RegisterDynamicQueryFn is null || FreeDynamicQueryFn is null || PollDynamicQueryFn is null
                || RegisterFrameQueryFn is null || ConsumeFramesFn is null || FreeFrameQueryFn is null;
            if (anyMissing)
            {
                LoadFailureReason = "PresentMonAPI2.dll is missing required exports (incompatible version).";
            }
        }
    }

    public bool IsAvailable =>
        Library is not null && LoadFailureReason is null;

    public string? UnavailableReason { get; private set; }

    public bool OpenSession()
    {
        if (_session != IntPtr.Zero)
        {
            return true;
        }
        if (!IsAvailable)
        {
            UnavailableReason = LoadFailureReason;
            return false;
        }

        if (OpenSessionFn!(out IntPtr session) != PmStatus.Success)
        {
            UnavailableReason = "Could not connect to the PresentMon Service.";
            return false;
        }
        _session = session;

        if (!RegisterQueries())
        {
            CloseSession();
            return false;
        }

        UnavailableReason = null;
        return true;
    }

    public void CloseSession()
    {
        if (_frameQuery != IntPtr.Zero)
        {
            FreeFrameQueryFn?.Invoke(_frameQuery);
            _frameQuery = IntPtr.Zero;
        }
        if (_dynamicQuery != IntPtr.Zero)
        {
            FreeDynamicQueryFn?.Invoke(_dynamicQuery);
            _dynamicQuery = IntPtr.Zero;
        }
        if (_session != IntPtr.Zero)
        {
            CloseSessionFn?.Invoke(_session);
            _session = IntPtr.Zero;
        }
    }

    public bool TrackProcess(int processId)
    {
        if (_session == IntPtr.Zero || StartTrackingFn is null)
        {
            return false;
        }

        PmStatus status = StartTrackingFn(_session, (uint)processId);
        return status == PmStatus.Success || status == PmStatus.AlreadyTrackingProcess;
    }

    public PresentMonDynamicSample? PollDynamic(int processId)
    {
        if (_session == IntPtr.Zero || _dynamicQuery == IntPtr.Zero
            || PollDynamicQueryFn is null || _dynamicElements is null)
        {
            return null;
        }

        // Swap-chain count is unknown up front; start generous and grow on
        // PM_STATUS_INSUFFICIENT_BUFFER. numSwapChains is in/out: it declares
        // the blob's capacity on entry and receives the actual chain count on
        // return. Only swap chain 0 is consumed — the primary swap chain is
        // what the FPS widget should report.
        int capacity = 32;
        while (true)
        {
            byte[] blob = new byte[_chainStride * capacity];
            uint numSwapChains = (uint)capacity;
            PmStatus status = PollDynamicQueryFn(_dynamicQuery, (uint)processId, blob, ref numSwapChains);

            if (status == PmStatus.InsufficientBuffer)
            {
                capacity *= 2;
                continue;
            }
            if (status != PmStatus.Success || numSwapChains == 0)
            {
                return null;
            }

            var sample = new PresentMonDynamicSample(
                Fps: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[0]),
                Low1PercentFps: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[1]),
                GpuBusyMs: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[2]),
                CpuFrameTimeMs: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[3]));
            return sample;
        }
    }

    public IReadOnlyList<double> DrainFrameTimes(int processId)
    {
        if (_session == IntPtr.Zero || _frameQuery == IntPtr.Zero
            || ConsumeFramesFn is null || _frameElements is null || _frameBlobSize <= 0)
        {
            return [];
        }

        const uint MaxFramesPerCall = 256;
        List<double> frameTimes = [];
        byte[] buffer = new byte[_frameBlobSize * MaxFramesPerCall];

        // pmConsumeFrames drains the queue; loop until it returns fewer than
        // the requested capacity so a burst of pending frames is fully consumed.
        while (true)
        {
            uint framesToRead = MaxFramesPerCall;
            PmStatus status = ConsumeFramesFn(_frameQuery, (uint)processId, buffer, ref framesToRead);
            if (status != PmStatus.Success || framesToRead == 0)
            {
                break;
            }

            for (uint i = 0; i < framesToRead; i++)
            {
                frameTimes.Add(PresentMonBlobReader.ReadFrameDouble(
                    buffer.AsSpan((int)i * _frameBlobSize), _frameElements[0]));
            }

            if (framesToRead < MaxFramesPerCall)
            {
                break;
            }
        }

        return frameTimes;
    }

    public void Dispose() => CloseSession();

    private bool RegisterQueries()
    {
        if (RegisterDynamicQueryFn is null || RegisterFrameQueryFn is null)
        {
            return false;
        }

        var dynamicElements = new[]
        {
            new PresentMonQueryElement(MetricPresentedFps, StatAvg, 0, 0, 0, 0),
            new PresentMonQueryElement(MetricPresentedFps, StatPercentile01, 0, 0, 0, 0),
            new PresentMonQueryElement(MetricGpuBusy, StatAvg, 0, 0, 0, 0),
            new PresentMonQueryElement(MetricCpuFrameTime, StatAvg, 0, 0, 0, 0),
        };

        // dataOffset/dataSize are filled in by the service during registration
        // — that is why the element array must be the same one used for parsing.
        PmStatus dynamicStatus = RegisterDynamicQueryFn(
            _session, out _dynamicQuery, dynamicElements, (ulong)dynamicElements.Length,
            DynamicQueryWindowMs, DynamicQueryOffsetMs);
        if (dynamicStatus != PmStatus.Success)
        {
            UnavailableReason = $"Failed to register the PresentMon dynamic query (status {dynamicStatus}).";
            return false;
        }
        _dynamicElements = dynamicElements;
        _chainStride = PresentMonBlobReader.ChainStrideBytes(dynamicElements);

        var frameElements = new[]
        {
            // Frame-event metrics carry one raw value per frame — the stat must
            // be NONE (AVG rejects registration). "Between Presents" is the
            // frame-event form of "Presented Frame Time", which is dynamic-query
            // only and cannot be registered on a frame query.
            new PresentMonQueryElement(MetricBetweenPresents, StatNone, 0, 0, 0, 0),
        };
        PmStatus frameStatus = RegisterFrameQueryFn(
            _session, out _frameQuery, frameElements, (ulong)frameElements.Length, out uint blobSize);
        if (frameStatus != PmStatus.Success || blobSize == 0)
        {
            UnavailableReason = $"Failed to register the PresentMon frame query (status {frameStatus}, blobSize {blobSize}).";
            return false;
        }
        _frameElements = frameElements;
        _frameBlobSize = (int)blobSize;

        return true;
    }

    private static IntPtr? LoadLibraryFromSdk(out string? failureReason)
    {
        failureReason = null;
        string[] candidates =
        [
            // Shared-service layout used by the MSI since v2.3.1: the client API
            // ships next to PresentMonService.exe.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMonSharedService", "PresentMonAPI2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Intel", "PresentMonSharedService", "PresentMonAPI2.dll"),
            // SDK layout: header + loader live here; some installs also drop the API dll.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMon", "SDK", "PresentMonAPI2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Intel", "PresentMon", "SDK", "PresentMonAPI2.dll"),
        ];

        foreach (string path in candidates)
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

        failureReason = "PresentMonAPI2.dll not found. Install the PresentMon Service (C:\\Program Files\\Intel\\PresentMonSharedService).";
        return null;
    }

    private static T? Resolve<T>(IntPtr library, string name) where T : Delegate
    {
        try
        {
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
