using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ModernWigiDash.Core.Telemetry;
using ModernWigiDash.Service.Contracts;

namespace ModernWigiDash.Service.Services;

// TimeStampQPC is deliberately used instead of TimeStampRelativeMSec: the
// DxgKrnl "GPU busy" payload and Stopwatch.GetTimestamp() are all QPC-based,
// so frame deltas and GPU-busy sums must be computed in QPC ticks.
#pragma warning disable CS0618

/// <summary>
/// In-process ETW frame-time capture for the ModernWigiDash service.
///
/// Subscribes to the same Windows tracing providers PresentMon consumes —
/// Microsoft-Windows-DXGI, Microsoft-Windows-D3D9 (present events) and
/// Microsoft-Windows-DxgKrnl (GPU-busy timestamps) — so no external tool is
/// needed: the service (running as LocalSystem) measures the running game
/// directly and widgets render the metrics it publishes over WCF.
///
/// Metrics:
///   Frame time (ms)   — delta between consecutive Present/Start events per swapchain
///   Live FPS          — frames presented over the rolling measurement window
///   1% / 0.1% low FPS — 1000 / 99th / 99.9th percentile frame time
///   CPU frame time    — Present/Stop - Present/Start per present (CPU-side duration)
///   GPU busy (%)      — accumulated DxgKrnl present QueuePacket/Start->Stop
///                       duration over the window, as a fraction of wall time
///
/// ETW requires an elevated context (LocalSystem when installed as a service).
/// If the real-time session cannot be opened the reader degrades gracefully:
/// snapshots report <see cref="FrameTimeSnapshotDto.IsAvailable"/> = false and
/// the rest of the service keeps running.
/// </summary>
public sealed class FrameTimeReader : BackgroundService
{
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    private static readonly double WindowSeconds = 2.0;
    private const int MaxSamplesPerProcess = 4096;
    private const int SparklineSamples = 120;

    private readonly ILogger<FrameTimeReader> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, ProcessFrameState> _processes = new();
    private readonly Dictionary<int, string> _processNames = new();

    // In-flight DxgKrnl present queue packets, keyed by (hContext, submitSequence).
    // QueuePacket/Stop is emitted from kernel context (PID 0), so the owning
    // process and enqueue QPC captured at QueuePacket/Start must be carried here.
    private readonly Dictionary<(ulong HContext, uint SubmitSequence), (int ProcessId, long StartQpc)> _pendingGpuPackets = new();

    private bool _running;
    private string _error = string.Empty;
    private DateTime _lastUpdate = DateTime.MinValue;

    public FrameTimeReader(ILogger<FrameTimeReader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Get the latest frame-time snapshot. Safe to call from any thread.
    /// </summary>
    /// <param name="preferredProcessId">
    /// &gt; 0: prefer this process when it has recent presents (fallback: no target).
    /// -1: never target a process (idle/monitor view).
    /// 0: select the most active presenting process.
    /// </param>
    public FrameTimeSnapshotDto GetSnapshot(int preferredProcessId = 0)
    {
        lock (_gate)
        {
            if (!_running)
            {
                return new FrameTimeSnapshotDto
                {
                    IsAvailable = false,
                    ErrorMessage = _error,
                    LastUpdate = _lastUpdate
                };
            }

            ProcessFrameState? target = SelectTargetProcess(preferredProcessId);
            if (target == null)
            {
                return new FrameTimeSnapshotDto
                {
                    IsAvailable = true,
                    ProcessId = 0,
                    LastUpdate = _lastUpdate,
                    RecentFrameTimesMs = []
                };
            }

            return BuildDto(target);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string sessionName = $"ModernWigiDash-Perf-{Guid.NewGuid():N}";

        try
        {
            using var session = new TraceEventSession(sessionName);
            session.StopOnDispose = true;

            EnableOrLog(session, DxgiProvider, "Microsoft-Windows-DXGI");
            EnableOrLog(session, D3D9Provider, "Microsoft-Windows-D3D9");
            EnableOrLog(session, DxgKrnlProvider, "Microsoft-Windows-DxgKrnl");

            session.Source.Dynamic.All += HandleEtwEvent;

            lock (_gate)
            {
                _running = true;
                _error = string.Empty;
                _lastUpdate = DateTime.UtcNow;
            }

            _logger.LogInformation("FrameTimeReader: ETW capture session '{SessionName}' started.", sessionName);

            await Task.Run(() => session.Source.Process(), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("FrameTimeReader: ETW capture session cancelled (normal shutdown).");
            // Normal shutdown
        }
        catch (Exception ex)
        {
            string message = ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception
                ? "ETW frame capture requires the service to run with admin/SYSTEM rights."
                : ex.Message;

            _logger.LogError(ex, "FrameTimeReader: ETW capture unavailable: {Message}", message);
            lock (_gate)
            {
                _running = false;
                _error = message;
                _lastUpdate = DateTime.UtcNow;
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                _processes.Clear();
            }
        }
    }

    private void EnableOrLog(TraceEventSession session, Guid providerGuid, string providerName)
    {
        try
        {
            session.EnableProvider(providerGuid, TraceEventLevel.Informational, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FrameTimeReader: failed to enable provider {Provider}: {Message}", providerName, ex.Message);
        }
    }

    private void HandleEtwEvent(TraceEvent data)
    {
        try
        {
            switch (data.ProviderName)
            {
                case "Microsoft-Windows-DXGI":
                case "Microsoft-Windows-D3D9":
                    HandlePresentEvent(data);
                    break;
                case "Microsoft-Windows-DxgKrnl":
                    if (data.EventName is "QueuePacket/Start" or "QueuePacket/Stop")
                    {
                        HandleQueuePacket(data);
                    }
                    break;
            }
        }
        catch (Exception)
        {
            _logger.LogDebug("FrameTimeReader: dropped malformed ETW event.");
            // A single malformed event must never break the capture loop.
        }
    }

    private void HandlePresentEvent(TraceEvent data)
    {
        bool isStart = data.TaskName == "Present" && data.Opcode == TraceEventOpcode.Start
            || data.EventName is "Present_Start" or "Present/Start";
        bool isStop = data.TaskName == "Present" && data.Opcode == TraceEventOpcode.Stop
            || data.EventName is "Present_Stop" or "Present/Stop";

        if (!isStart && !isStop)
        {
            return;
        }

        long qpc = data.TimeStampQPC;
        int pid = data.ProcessID;
        if (pid <= 0 || qpc == 0)
        {
            return;
        }

        long swapchain = ToInt64OrZero(data.PayloadByName("pIDXGISwapChain") ?? data.PayloadByName("swapchain"));

        lock (_gate)
        {
            if (!_processes.TryGetValue(pid, out ProcessFrameState? state))
            {
                state = new ProcessFrameState { ProcessId = pid, Name = ResolveProcessName(pid) };
                _processes[pid] = state;
            }

            if (isStart)
            {
                RecordPresentStart(state, data.ThreadID, swapchain, qpc);
            }
            else if (isStop)
            {
                RecordPresentStop(state, data.ThreadID, qpc);
            }
        }
    }

    private static void RecordPresentStart(ProcessFrameState state, int threadId, long swapchain, long qpc)
    {
        double freq = Stopwatch.Frequency;

        if (state.LastSwapchainPresent.TryGetValue(swapchain, out long previousQpc) && previousQpc > 0 && qpc > previousQpc)
        {
            double frameMs = (qpc - previousQpc) / freq * 1000.0;
            state.RecentFrames.Add((qpc, frameMs));
            state.LastFrameMs = frameMs;
        }
        else if (!state.LastSwapchainPresent.Any() && state.LastPresentQpc > 0 && qpc > state.LastPresentQpc)
        {
            double frameMs = (qpc - state.LastPresentQpc) / freq * 1000.0;
            state.RecentFrames.Add((qpc, frameMs));
            state.LastFrameMs = frameMs;
        }

        state.LastSwapchainPresent[swapchain] = qpc;
        state.LastPresentQpc = qpc;
        if (qpc > state.NewestPresentQpc)
        {
            state.NewestPresentQpc = qpc;
        }
        state.PresentCount++;
        state.PresentStartByThread[threadId] = qpc;

        if (state.PresentStartByThread.Count > 1024)
        {
            foreach (int stale in state.PresentStartByThread
                .OrderBy(kv => kv.Value)
                .Take(state.PresentStartByThread.Count - 512)
                .Select(kv => kv.Key)
                .ToList())
            {
                state.PresentStartByThread.Remove(stale);
            }
        }

        Prune(state.RecentFrames);
    }

    private static bool RecordPresentStop(ProcessFrameState state, int threadId, long qpc)
    {
        if (!state.PresentStartByThread.TryGetValue(threadId, out long startQpc) || startQpc == 0 || qpc <= startQpc)
        {
            return false;
        }

        double cpuMs = (qpc - startQpc) / (double)Stopwatch.Frequency * 1000.0;
        state.RecentCpuFrames.Add((qpc, cpuMs));
        state.PresentStartByThread.Remove(threadId);
        Prune(state.RecentCpuFrames);
        return true;
    }

    private void HandleQueuePacket(TraceEvent data)
    {
        long qpc = data.TimeStampQPC;
        ulong hContext = ToUInt64OrZero(data.PayloadByName("hContext"));
        uint submitSequence = ToUInt32OrZero(data.PayloadByName("SubmitSequence"));
        if (hContext == 0 || qpc == 0)
        {
            return;
        }

        var key = (hContext, submitSequence);

        lock (_gate)
        {
            if (data.Opcode == TraceEventOpcode.Start)
            {
                if (!IsPresentQueuePacket(data))
                {
                    return;
                }

                if (_pendingGpuPackets.Count > 16_384)
                {
                    long cutoff = qpc - (long)(5.0 * Stopwatch.Frequency);
                    foreach (var stale in _pendingGpuPackets.Where(kv => kv.Value.StartQpc < cutoff).Select(kv => kv.Key).ToList())
                    {
                        _pendingGpuPackets.Remove(stale);
                    }
                }

                int pid = data.ProcessID;
                if (pid > 0)
                {
                    _pendingGpuPackets[key] = (pid, qpc);
                }
            }
            else if (data.Opcode == TraceEventOpcode.Stop)
            {
                if (!_pendingGpuPackets.TryGetValue(key, out (int ProcessId, long StartQpc) pending) || qpc <= pending.StartQpc)
                {
                    return;
                }

                _pendingGpuPackets.Remove(key);

                if (pending.ProcessId <= 0 || !_processes.TryGetValue(pending.ProcessId, out ProcessFrameState? state))
                {
                    return;
                }

                double busyMs = (qpc - pending.StartQpc) / (double)Stopwatch.Frequency * 1000.0;
                state.GpuBusy.Add((qpc, busyMs));
                Prune(state.GpuBusy);
            }
        }
    }

    private bool IsPresentQueuePacket(TraceEvent data)
    {
        if (IsTruthy(data.PayloadByName("bPresent")))
        {
            return true;
        }

        // Fallback: the packet was submitted by a thread with an open Present.
        lock (_gate)
        {
            foreach (ProcessFrameState state in _processes.Values)
            {
                if (state.PresentStartByThread.ContainsKey(data.ThreadID))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsTruthy(object? value) => value switch
    {
        bool b => b,
        byte by => by != 0,
        sbyte sb => sb != 0,
        short s => s != 0,
        ushort us => us != 0,
        int i => i != 0,
        uint ui => ui != 0,
        long l => l != 0,
        ulong ul => ul != 0,
        _ => false
    };

    private static long ToInt64OrZero(object? value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            return value switch
            {
                ulong u => unchecked((long)u),
                _ => Convert.ToInt64(value)
            };
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static ulong ToUInt64OrZero(object? value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            return Convert.ToUInt64(value);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static uint ToUInt32OrZero(object? value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            return Convert.ToUInt32(value);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private ProcessFrameState? SelectTargetProcess(int preferredProcessId = 0)
    {
        long windowTicks = (long)(WindowSeconds * Stopwatch.Frequency);

        // A preference (foreground-window PID or the explicit -1 "no game"
        // sentinel) means the caller wants exactly that process, or nothing at
        // all — never fall back to the most active presenter (e.g. dwm).
        if (preferredProcessId != 0)
        {
            if (preferredProcessId > 0
                && _processes.TryGetValue(preferredProcessId, out ProcessFrameState? preferred))
            {
                long preferredRef = preferred.NewestPresentQpc > 0 ? preferred.NewestPresentQpc : Stopwatch.GetTimestamp();
                if (preferred.RecentFrames.Count(f => preferredRef - (long)f.Qpc <= windowTicks) > 0)
                {
                    return preferred;
                }
            }

            return null;
        }

        ProcessFrameState? best = null;
        long bestCount = -1;

        foreach (ProcessFrameState state in _processes.Values)
        {
            // Real-time ETW events are processed with a buffering lag, so the
            // newest event's QPC is used as the recency reference rather than
            // Stopwatch.GetTimestamp() (which would see nothing as "recent").
            long reference = state.NewestPresentQpc > 0 ? state.NewestPresentQpc : Stopwatch.GetTimestamp();
            long count = state.RecentFrames.Count(f => reference - (long)f.Qpc <= windowTicks);
            if (count > bestCount)
            {
                best = state;
                bestCount = count;
            }
        }

        return bestCount > 0 ? best : null;
    }

    private FrameTimeSnapshotDto BuildDto(ProcessFrameState target)
    {
        long windowTicks = (long)(WindowSeconds * Stopwatch.Frequency);
        long reference = target.NewestPresentQpc > 0 ? target.NewestPresentQpc : Stopwatch.GetTimestamp();

        List<(long Qpc, double Ms)> windowFrames = target.RecentFrames
            .Where(f => reference - (long)f.Qpc <= windowTicks)
            .ToList();
        if (windowFrames.Count == 0)
        {
            windowFrames = target.RecentFrames.ToList();
        }

        double[] frameTimes = windowFrames.Select(f => f.Ms).ToArray();

        double lastMs = windowFrames.Count > 0 ? windowFrames[^1].Ms : target.LastFrameMs;
        double avgMs = frameTimes.Length > 0 ? frameTimes.Average() : 0;

        double fps;
        if (windowFrames.Count >= 2 && windowFrames[^1].Qpc > windowFrames[0].Qpc)
        {
            double elapsedSeconds = (windowFrames[^1].Qpc - windowFrames[0].Qpc) / (double)Stopwatch.Frequency;
            fps = elapsedSeconds > 0 ? (windowFrames.Count - 1) / elapsedSeconds : 0;
        }
        else
        {
            fps = FrameTimeStatistics.FpsFromFrameTimeMs(lastMs > 0 ? lastMs : avgMs);
        }

        double low1 = FrameTimeStatistics.Low1PercentFps(frameTimes);
        double low01 = FrameTimeStatistics.Low01PercentFps(frameTimes);

        List<(long Qpc, double Ms)> cpuFrames = target.RecentCpuFrames
            .Where(c => reference - (long)c.Qpc <= windowTicks)
            .ToList();
        double cpuMs = cpuFrames.Count > 0 ? cpuFrames.Average(c => c.Ms) : 0;

        List<(long Qpc, double Ms)> busy = target.GpuBusy
            .Where(g => reference - (long)g.Qpc <= windowTicks)
            .ToList();

        double gpuBusyPercent = 0;
        if (busy.Count > 0)
        {
            double windowSeconds = windowFrames.Count >= 2 && windowFrames[^1].Qpc > windowFrames[0].Qpc
                ? (windowFrames[^1].Qpc - windowFrames[0].Qpc) / (double)Stopwatch.Frequency
                : WindowSeconds;
            if (windowSeconds > 0)
            {
                double busySeconds = busy.Sum(g => g.Ms) / 1000.0;
                gpuBusyPercent = Math.Clamp(busySeconds / windowSeconds * 100.0, 0, 100);
            }
        }

        _lastUpdate = DateTime.UtcNow;
        return new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            LastUpdate = _lastUpdate,
            ProcessId = target.ProcessId,
            ProcessName = target.Name,
            Fps = Math.Round(fps, 1),
            FrameTimeMs = Math.Round(lastMs > 0 ? lastMs : avgMs, 2),
            Low1PercentFps = Math.Round(low1, 1),
            Low01PercentFps = Math.Round(low01, 1),
            GpuBusyPercent = Math.Round(gpuBusyPercent, 1),
            CpuFrameTimeMs = Math.Round(cpuMs, 2),
            RecentFrameTimesMs = Downsample(frameTimes, SparklineSamples).ToList()
        };
    }

    private string ResolveProcessName(int pid)
    {
        if (_processNames.TryGetValue(pid, out string? cached))
        {
            return cached;
        }

        string name = string.Empty;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            name = process.ProcessName + ".exe";
        }
        catch (Exception)
        {
            // Process may have exited; fall through to the pid
            name = $"pid-{pid}";
        }

        _processNames[pid] = name;
        return name;
    }

    private static void Prune(List<(long Qpc, double Ms)> samples)
    {
        if (samples.Count <= MaxSamplesPerProcess)
        {
            return;
        }

        int excess = samples.Count - MaxSamplesPerProcess;
        samples.RemoveRange(0, excess);
    }

    private static IEnumerable<double> Downsample(double[] values, int maxSamples)
    {
        if (values.Length <= maxSamples)
        {
            return values;
        }

        double step = values.Length / (double)maxSamples;
        var result = new double[maxSamples];
        for (int i = 0; i < maxSamples; i++)
        {
            result[i] = values[(int)Math.Min(values.Length - 1, i * step)];
        }

        return result;
    }

    /// <summary>
    /// Rolling per-process capture state. ETW handlers mutate this under the
    /// reader's gate; snapshots read it under the same gate.
    /// </summary>
    private sealed class ProcessFrameState
    {
        public int ProcessId { get; init; }

        public string Name { get; init; } = string.Empty;

        /// <summary>Frame time samples (ms) keyed by QPC timestamp, newest last.</summary>
        public List<(long Qpc, double Ms)> RecentFrames { get; } = [];

        /// <summary>CPU-side present durations (ms) keyed by QPC timestamp.</summary>
        public List<(long Qpc, double Ms)> RecentCpuFrames { get; } = [];

        /// <summary>GPU-busy durations (ms) keyed by completion QPC timestamp.</summary>
        public List<(long Qpc, double Ms)> GpuBusy { get; } = [];

        /// <summary>Last present QPC per swapchain address (key 0 = unknown).</summary>
        public Dictionary<long, long> LastSwapchainPresent { get; } = [];

        /// <summary>Open Present_Start QPC per presenting thread.</summary>
        public Dictionary<int, long> PresentStartByThread { get; } = [];

        public long LastPresentQpc { get; set; }

        /// <summary>Newest present QPC seen for this process (recency reference).</summary>
        public long NewestPresentQpc { get; set; }

        public double LastFrameMs { get; set; }

        public long PresentCount { get; set; }
    }
}
