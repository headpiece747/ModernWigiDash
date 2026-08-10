namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// The PresentMon query subsystem: registers the dynamic + frame queries
/// against the installed service's introspection, owns the query handles,
/// elements, field map, and blob strides, and performs the poll/drain loops.
/// Pure policy over injected delegates, so the capacity-growth and drain
/// loops are unit-testable without the DLL. The session handle itself stays
/// with <see cref="PresentMonNative"/>.
/// </summary>
internal sealed class PresentMonQueryRegistry
{
    /// <summary>
    /// The wanted dynamic-query metrics. Each spec names the field slot the
    /// producer reads and the preferred stat; the query builder validates both
    /// against the installed service's introspection and drops unsupported
    /// metrics with a named reason (the field then reads 0).
    /// </summary>
    internal static readonly PresentMonQuerySpec[] DynamicQuerySpecs =
    [
        new(DynamicField.Fps, PresentMonProtocol.MetricPresentedFps, PresentMonProtocol.StatAvg),
        new(DynamicField.Low1PercentFps, PresentMonProtocol.MetricPresentedFps, PresentMonProtocol.StatPercentile01),
        new(DynamicField.GpuBusyMs, PresentMonProtocol.MetricGpuBusy, PresentMonProtocol.StatAvg),
        new(DynamicField.CpuFrameTimeMs, PresentMonProtocol.MetricCpuFrameTime, PresentMonProtocol.StatAvg),
        new(DynamicField.DisplayedFps, PresentMonProtocol.MetricDisplayedFps, PresentMonProtocol.StatAvg),
        new(DynamicField.GpuTimeMs, PresentMonProtocol.MetricGpuTime, PresentMonProtocol.StatAvg),
        new(DynamicField.DroppedFrames, PresentMonProtocol.MetricDroppedFrames, PresentMonProtocol.StatAvg),
        // PRESENT_MODE is a DYNAMIC_FRAME enum metric: the service accepts only
        // NEWEST_POINT / MID_LERP stats (AVG and NONE both fail registration).
        new(DynamicField.PresentModeId, PresentMonProtocol.MetricPresentMode, PresentMonProtocol.StatNewestPoint),
    ];

    private readonly PmRegisterDynamicQuery _registerDynamic;
    private readonly PmFreeDynamicQuery _freeDynamic;
    private readonly PmPollDynamicQuery _pollDynamic;
    private readonly PmRegisterFrameQuery _registerFrame;
    private readonly PmConsumeFrames _consumeFrames;
    private readonly PmFreeFrameQuery _freeFrame;
    private readonly Func<IntPtr, PresentMonMetricCatalog?> _readCatalog;

    private IntPtr _dynamicQuery;
    private IntPtr _frameQuery;
    private PresentMonQueryElement[]? _dynamicElements;
    private int[] _fieldIndexes = [];
    private PresentMonQueryElement[]? _frameElements;
    private int _chainStride;
    private int _frameBlobSize;

    public PresentMonQueryRegistry(
        PmRegisterDynamicQuery registerDynamic,
        PmFreeDynamicQuery freeDynamic,
        PmPollDynamicQuery pollDynamic,
        PmRegisterFrameQuery registerFrame,
        PmConsumeFrames consumeFrames,
        PmFreeFrameQuery freeFrame,
        Func<IntPtr, PresentMonMetricCatalog?> readCatalog)
    {
        _registerDynamic = registerDynamic;
        _freeDynamic = freeDynamic;
        _pollDynamic = pollDynamic;
        _registerFrame = registerFrame;
        _consumeFrames = consumeFrames;
        _freeFrame = freeFrame;
        _readCatalog = readCatalog;
    }

    /// <summary>
    /// Registers both queries for the session: reads the installed service's
    /// introspection, builds the dynamic query via
    /// <see cref="PresentMonQueryBuilder"/>, and registers the frame query.
    /// False with <paramref name="unavailableReason"/> on any failure.
    /// </summary>
    public bool Register(IntPtr session, out string? unavailableReason)
    {
        unavailableReason = null;

        if (_readCatalog(session) is not { } catalog)
        {
            unavailableReason = "Could not read the PresentMon metric catalog (pmGetIntrospectionRoot failed).";
            return false;
        }

        var build = PresentMonQueryBuilder.Build(DynamicQuerySpecs, catalog);
        if (build.Elements.Length == 0)
        {
            unavailableReason = "No PresentMon dynamic-query metrics are registrable on the installed service.";
            return false;
        }

        // dataOffset/dataSize are filled in by the service during registration
        // — that is why the element array must be the same one used for parsing.
        PmStatus dynamicStatus = _registerDynamic(
            session, out _dynamicQuery, build.Elements, (ulong)build.Elements.Length,
            PresentMonProtocol.DynamicQueryWindowMs, PresentMonProtocol.DynamicQueryOffsetMs);
        if (dynamicStatus != PmStatus.Success)
        {
            string dropped = build.DroppedMetrics.Count > 0 ? $" Dropped: {string.Join("; ", build.DroppedMetrics)}." : string.Empty;
            unavailableReason = $"Failed to register the PresentMon dynamic query (status {dynamicStatus}).{dropped}";
            return false;
        }
        _dynamicElements = build.Elements;
        _fieldIndexes = build.FieldIndexes;
        _chainStride = PresentMonBlobReader.ChainStrideBytes(build.Elements);

        var frameElements = new[]
        {
            // Frame-event metrics carry one raw value per frame — the stat must
            // be NONE (AVG rejects registration). "Between Presents" is the
            // frame-event form of "Presented Frame Time", which is dynamic-query
            // only and cannot be registered on a frame query.
            new PresentMonQueryElement(PresentMonProtocol.MetricBetweenPresents, PresentMonProtocol.StatNone, 0, 0, 0, 0),
        };
        PmStatus frameStatus = _registerFrame(session, out _frameQuery, frameElements, (ulong)frameElements.Length, out uint blobSize);
        if (frameStatus != PmStatus.Success || blobSize == 0)
        {
            unavailableReason = $"Failed to register the PresentMon frame query (status {frameStatus}, blobSize {blobSize}).";
            return false;
        }
        _frameElements = frameElements;
        _frameBlobSize = (int)blobSize;

        return true;
    }

    /// <summary>Frees both query handles. Safe to call before any registration.</summary>
    public void Free()
    {
        if (_frameQuery != IntPtr.Zero)
        {
            _freeFrame(_frameQuery);
            _frameQuery = IntPtr.Zero;
        }
        if (_dynamicQuery != IntPtr.Zero)
        {
            _freeDynamic(_dynamicQuery);
            _dynamicQuery = IntPtr.Zero;
        }
    }

    public PresentMonPollResult PollDynamic(int processId)
    {
        if (_dynamicQuery == IntPtr.Zero || _dynamicElements is null)
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
            PmStatus status = _pollDynamic(_dynamicQuery, (uint)processId, blob, ref numSwapChains);

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
                Fps: ReadField(blob, DynamicField.Fps),
                Low1PercentFps: ReadField(blob, DynamicField.Low1PercentFps),
                GpuBusyMs: ReadField(blob, DynamicField.GpuBusyMs),
                CpuFrameTimeMs: ReadField(blob, DynamicField.CpuFrameTimeMs),
                DisplayedFps: ReadField(blob, DynamicField.DisplayedFps),
                GpuTimeMs: ReadField(blob, DynamicField.GpuTimeMs),
                DroppedFrames: (int)ReadField(blob, DynamicField.DroppedFrames),
                PresentModeId: (int)ReadField(blob, DynamicField.PresentModeId));
            return new PresentMonPollResult(sample, PmStatus.Success);
        }
    }

    /// <summary>
    /// Reads one named metric slot from a polled blob via the field→element map
    /// built at registration. A field whose metric was dropped (map entry -1)
    /// reads as 0 — the producer's no-data value.
    /// </summary>
    private double ReadField(byte[] blob, DynamicField field)
    {
        int elementIndex = _fieldIndexes[(int)field];
        return elementIndex < 0 ? 0 : PresentMonBlobReader.ReadDynamicElement(blob, 0, _chainStride, _dynamicElements![elementIndex]);
    }

    public IReadOnlyList<double> DrainFrameTimes(int processId)
    {
        if (_frameQuery == IntPtr.Zero || _frameElements is null || _frameBlobSize <= 0)
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
            PmStatus status = _consumeFrames(_frameQuery, (uint)processId, buffer, ref framesToRead);
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
}
