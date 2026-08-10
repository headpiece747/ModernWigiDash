using ModernWigiDash.Sdk;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// Runtime-loaded PresentMon API v3 interop (ADR-0003). Loads
/// <c>PresentMonAPI2.dll</c> from the PresentMon SDK install directory at
/// runtime — never ships its own copy, because the client↔service binary
/// protocol isn't backward-guaranteed (issue #383). All function pointers are
/// resolved at load time by <see cref="PresentMonApiProbe"/>; a missing
/// library, missing export, or a non-v3 API generation (checked via
/// <c>pmGetApiVersion</c>) marks the seam unavailable and the producer
/// degrades to the widget's graceful state.
/// </summary>
public sealed class PresentMonNative : IPresentMonNative
{
    private readonly IntPtr? _library;
    private readonly PmOpenSession? _openSessionFn;
    private readonly PmCloseSession? _closeSessionFn;
    private readonly PmStartTrackingProcess? _startTrackingFn;
    private readonly PmRegisterDynamicQuery? _registerDynamicQueryFn;
    private readonly PmFreeDynamicQuery? _freeDynamicQueryFn;
    private readonly PmPollDynamicQuery? _pollDynamicQueryFn;
    private readonly PmRegisterFrameQuery? _registerFrameQueryFn;
    private readonly PmConsumeFrames? _consumeFramesFn;
    private readonly PmFreeFrameQuery? _freeFrameQueryFn;
    private readonly string? _loadFailureReason;

    /// <summary>Creates the runtime-loaded PresentMon interop with the real native probe.</summary>
    public PresentMonNative()
        : this(new PresentMonApiProbe(NativePresentMonLibraryLoader.Instance))
    {
    }

    /// <summary>Internal ctor for tests: any probe (e.g. a fake loader never touches the real DLL).</summary>
    internal PresentMonNative(PresentMonApiProbe probe)
    {
        _library = probe.Library;
        _openSessionFn = probe.OpenSessionFn;
        _closeSessionFn = probe.CloseSessionFn;
        _startTrackingFn = probe.StartTrackingFn;
        _registerDynamicQueryFn = probe.RegisterDynamicQueryFn;
        _freeDynamicQueryFn = probe.FreeDynamicQueryFn;
        _pollDynamicQueryFn = probe.PollDynamicQueryFn;
        _registerFrameQueryFn = probe.RegisterFrameQueryFn;
        _consumeFramesFn = probe.ConsumeFramesFn;
        _freeFrameQueryFn = probe.FreeFrameQueryFn;
        _loadFailureReason = probe.FailureReason;
    }

    private IntPtr _session;
    private IntPtr _dynamicQuery;
    private IntPtr _frameQuery;
    private PresentMonQueryElement[]? _dynamicElements;
    private PresentMonQueryElement[]? _frameElements;
    private int _chainStride;
    private int _frameBlobSize;

    public bool IsAvailable =>
        _library is not null && _loadFailureReason is null;

    public string? UnavailableReason { get; private set; }

    public bool OpenSession()
    {
        if (_session != IntPtr.Zero)
        {
            return true;
        }
        if (!IsAvailable)
        {
            UnavailableReason = _loadFailureReason;
            return false;
        }

        if (_openSessionFn!(out IntPtr session) != PmStatus.Success)
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
            _freeFrameQueryFn?.Invoke(_frameQuery);
            _frameQuery = IntPtr.Zero;
        }
        if (_dynamicQuery != IntPtr.Zero)
        {
            _freeDynamicQueryFn?.Invoke(_dynamicQuery);
            _dynamicQuery = IntPtr.Zero;
        }
        if (_session != IntPtr.Zero)
        {
            _closeSessionFn?.Invoke(_session);
            _session = IntPtr.Zero;
        }
    }

    public bool TrackProcess(int processId)
    {
        if (_session == IntPtr.Zero || _startTrackingFn is null)
        {
            return false;
        }

        PmStatus status = _startTrackingFn(_session, (uint)processId);
        return status == PmStatus.Success || status == PmStatus.AlreadyTrackingProcess;
    }

    public PresentMonPollResult PollDynamic(int processId)
    {
        if (_session == IntPtr.Zero || _dynamicQuery == IntPtr.Zero
            || _pollDynamicQueryFn is null || _dynamicElements is null)
        {
            return new PresentMonPollResult(null, PmStatus.Success);
        }

        // Swap-chain count is unknown up front; start generous and grow on
        // PM_STATUS_INSUFFICIENT_BUFFER. numSwapChains is in/out: it declares
        // the blob's capacity on entry and receives the actual chain count on
        // return. Only swap chain 0 is consumed — the primary swap chain is
        // what the FPS widget should report. Growth is capped: a service that
        // keeps refusing capacity beyond the cap is misbehaving, so the poll
        // fails instead of allocating unbounded blobs.
        const int MaxSwapChainCapacity = 8192;
        int capacity = 32;
        while (true)
        {
            byte[] blob = new byte[_chainStride * capacity];
            uint numSwapChains = (uint)capacity;
            PmStatus status = _pollDynamicQueryFn(_dynamicQuery, (uint)processId, blob, ref numSwapChains);

            if (status == PmStatus.InsufficientBuffer)
            {
                if (capacity >= MaxSwapChainCapacity)
                {
                    return new PresentMonPollResult(null, PmStatus.ServiceError);
                }
                capacity *= 2;
                continue;
            }
            if (status != PmStatus.Success)
            {
                // A session-level failure (SessionNotOpen / PipeError /
                // ServiceError) means the service restarted or the pipe broke —
                // the caller must re-establish the session.
                return new PresentMonPollResult(null, status);
            }
            if (numSwapChains == 0)
            {
                return new PresentMonPollResult(null, PmStatus.Success);
            }

            var sample = new PresentMonDynamicSample(
                Fps: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[0]),
                Low1PercentFps: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[1]),
                GpuBusyMs: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[2]),
                CpuFrameTimeMs: PresentMonBlobReader.ReadDynamicDouble(blob, 0, _chainStride, _dynamicElements[3]));
            return new PresentMonPollResult(sample, PmStatus.Success);
        }
    }

    public IReadOnlyList<double> DrainFrameTimes(int processId)
    {
        if (_session == IntPtr.Zero || _frameQuery == IntPtr.Zero
            || _consumeFramesFn is null || _frameElements is null || _frameBlobSize <= 0)
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
            PmStatus status = _consumeFramesFn(_frameQuery, (uint)processId, buffer, ref framesToRead);
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
        if (_registerDynamicQueryFn is null || _registerFrameQueryFn is null)
        {
            return false;
        }

        var dynamicElements = new[]
        {
            new PresentMonQueryElement(PresentMonProtocol.MetricPresentedFps, PresentMonProtocol.StatAvg, 0, 0, 0, 0),
            new PresentMonQueryElement(PresentMonProtocol.MetricPresentedFps, PresentMonProtocol.StatPercentile01, 0, 0, 0, 0),
            new PresentMonQueryElement(PresentMonProtocol.MetricGpuBusy, PresentMonProtocol.StatAvg, 0, 0, 0, 0),
            new PresentMonQueryElement(PresentMonProtocol.MetricCpuFrameTime, PresentMonProtocol.StatAvg, 0, 0, 0, 0),
        };

        // dataOffset/dataSize are filled in by the service during registration
        // — that is why the element array must be the same one used for parsing.
        PmStatus dynamicStatus = _registerDynamicQueryFn(
            _session, out _dynamicQuery, dynamicElements, (ulong)dynamicElements.Length,
            PresentMonProtocol.DynamicQueryWindowMs, PresentMonProtocol.DynamicQueryOffsetMs);
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
            new PresentMonQueryElement(PresentMonProtocol.MetricBetweenPresents, PresentMonProtocol.StatNone, 0, 0, 0, 0),
        };
        PmStatus frameStatus = _registerFrameQueryFn(
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
}
