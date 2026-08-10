namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// Runtime-loaded PresentMon API v3 interop (ADR-0003). Loads
/// <c>PresentMonAPI2.dll</c> from the PresentMon SDK install directory at
/// runtime — never ships its own copy, because the client↔service binary
/// protocol isn't backward-guaranteed (issue #383). All function pointers are
/// resolved at load time by <see cref="PresentMonApiProbe"/>; a missing
/// library, missing export, or a non-v3 API generation (checked via
/// <c>pmGetApiVersion</c>) marks the seam unavailable and the producer
/// degrades to the widget's graceful state. The query subsystem lives in
/// <see cref="PresentMonQueryRegistry"/>; this class owns the session and the
/// tracking surface.
/// </summary>
public sealed class PresentMonNative : IPresentMonNative
{
    private readonly IntPtr? _library;
    private readonly PmOpenSession? _openSessionFn;
    private readonly PmCloseSession? _closeSessionFn;
    private readonly PmStartTrackingProcess? _startTrackingFn;
    private readonly PmGetIntrospectionRoot? _getIntrospectionRootFn;
    private readonly PmFreeIntrospectionRoot? _freeIntrospectionRootFn;
    private readonly PresentMonQueryRegistry _queryRegistry;
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
        _getIntrospectionRootFn = probe.GetIntrospectionRootFn;
        _freeIntrospectionRootFn = probe.FreeIntrospectionRootFn;
        _queryRegistry = new PresentMonQueryRegistry(
            probe.RegisterDynamicQueryFn!,
            probe.FreeDynamicQueryFn!,
            probe.PollDynamicQueryFn!,
            probe.RegisterFrameQueryFn!,
            probe.ConsumeFramesFn!,
            probe.FreeFrameQueryFn!,
            ReadCatalog);
        _loadFailureReason = probe.FailureReason;
    }

    private IntPtr _session;

    public bool IsAvailable =>
        _library is not null && _loadFailureReason is null;

    public string? UnavailableReason { get; private set; }

    /// <summary>
    /// Parses the service's introspection tree into the metric catalog. The
    /// native root is freed immediately after parsing — the catalog is managed
    /// memory owned by the registry.
    /// </summary>
    private PresentMonMetricCatalog? ReadCatalog(IntPtr session)
    {
        if (_getIntrospectionRootFn is null || _freeIntrospectionRootFn is null)
        {
            return null;
        }
        if (_getIntrospectionRootFn(session, out IntPtr rootPtr) != PmStatus.Success)
        {
            return null;
        }
        try
        {
            return new PresentMonMetricCatalog(PresentMonIntrospection.ParseMetrics(rootPtr));
        }
        finally
        {
            _freeIntrospectionRootFn(rootPtr);
        }
    }

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

        if (!_queryRegistry.Register(session, out string? reason))
        {
            UnavailableReason = reason;
            CloseSession();
            return false;
        }

        UnavailableReason = null;
        return true;
    }

    public void CloseSession()
    {
        _queryRegistry.Free();
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

    public PresentMonPollResult PollDynamic(int processId) => _queryRegistry.PollDynamic(processId);

    public IReadOnlyList<double> DrainFrameTimes(int processId) => _queryRegistry.DrainFrameTimes(processId);

    public void Dispose() => CloseSession();
}
