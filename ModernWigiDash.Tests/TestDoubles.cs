using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using LibUsbDotNet;
using LibUsbDotNet.Info;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using ModernWigiDash.App.LibreHardwareService;
using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Hardware.Transport;
using AppClass = ModernWigiDash.App.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// The shared test doubles every test file used to copy: the no-op widget
/// context (with optional counters), the placed-instance persisting variant,
/// the in-memory WebSocket feed, the HTTP stub, the PresentMon interop stub,
/// the SMTC media-session triple, the LHS map source, the STA App host, and
/// the one-shot STA runner. One double per seam — new widget tests start
/// from a one-line host.
/// </summary>
internal class TestContext : IModernWigiDashContext
{
    public int Renders { get; private set; }
    public int AuthShown { get; private set; }
    public int AuthClosed { get; private set; }
    public List<string> Errors { get; } = [];
    public List<string> Infos { get; } = [];
    /// <summary>The NavigatePage deltas fired through the context seam (the hotkey widget's page-flip routing pin).</summary>
    public List<int> NavigatePageCalls { get; } = [];

    public void LogInfo(string message) => Infos.Add(LogLine.Sanitize(message));
    public void LogError(string message, Exception? ex = null)
        => Errors.Add(LogLine.Sanitize(message) + (ex != null ? $": {LogLine.Sanitize(ex.ToString())}" : ""));
    public void RequestRender() => Renders++;
    public void RequestInspectorRefresh() { }
    public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt) => AuthShown++;
    public void CloseDeviceAuthorization() => AuthClosed++;

    public virtual void PersistProperty(object widget, string propertyName, object? value) { }

    public virtual void NavigatePage(int delta) => NavigatePageCalls.Add(delta);
}

/// <summary>
/// Context that resolves the owning placed instance like MainWindow does —
/// the companion to ModernWidgetBase.SetProperty (for tests asserting the
/// PropertyValues persistence path).
/// </summary>
internal sealed class PersistingContext(ProfileLayout profile) : TestContext
{
    public override void PersistProperty(object widget, string propertyName, object? value)
    {
        // The shared identity scan — same rule MainWindow.Context uses, so the
        // test double is not a copy that can drift.
        if (ProfileOps.FindPlacedWidget(profile, widget) is { } placed)
        {
            placed.PropertyValues[propertyName] = value;
        }
    }
}

/// <summary>
/// The shared minimal widget for tests that need a concrete instance — the
/// former per-file TestWidget copies: a [WidgetProperty] Label (inspector
/// write-back and rehydration round-trip tests), a small DefaultSize (profile
/// layout), the loader metadata profile tests register, and the one
/// protected-member accessor the color tests need.
/// </summary>
[WidgetMetadata("profile_test_widget", "Profile Test", DefaultGridSize = GridSizePreset.Size2x1)]
internal sealed class TestWidget : ModernWidgetBase
{
    [WidgetProperty("Label", WidgetPropertyType.Text, defaultValue: "seed")]
    public string Label { get; set; } = "seed";

    // The raw override (equal to the attribute's 2x1 preset) pins the
    // instance-escape-hatch: a widget may still override DefaultSize, and
    // placement reads the catalog while PlaceWidget sizes from the instance.
    public override SKSize DefaultSize => new(406, 148);

    public override void Render(SKCanvas canvas, SKRect bounds) { }

    public SKColor GetColor(string hex, SKColor fallback) => ColorOf(hex, fallback);

    public void SetPropertyForTest(string propertyName, object? value) => SetProperty(propertyName, value);
}

/// <summary>
/// In-memory <see cref="IWebSocketFeed"/>: queued messages feed the consumer,
/// sent payloads are recorded, and connect failures are injectable — the
/// feed loops (price, Twitch) are drivable without a network. With
/// <see cref="ParkConnect"/> set, ConnectAsync parks on an internal gate the
/// test releases via <see cref="ReleaseConnect"/> (the former per-file
/// BlockingFeed shape).
/// </summary>
internal sealed class FakeFeed : IWebSocketFeed
{
    private readonly Queue<string> _incoming = new();
    private readonly TaskCompletionSource _parkedConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Sent { get; } = [];
    public bool IsOpen { get; set; } = true;
    public int ConnectCount { get; private set; }
    public Exception? ConnectError { get; set; }
    public bool ParkConnect { get; set; }

    public void QueueMessage(string message) => _incoming.Enqueue(message);

    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        ConnectCount++;
        if (ParkConnect)
        {
            ct.Register(() => _parkedConnect.TrySetCanceled(ct));
            return _parkedConnect.Task;
        }
        return ConnectError is null ? Task.CompletedTask : Task.FromException(ConnectError);
    }

    /// <summary>Releases a parked connect (see <see cref="ParkConnect"/>).</summary>
    public void ReleaseConnect() => _parkedConnect.TrySetResult();

    public Task SendTextAsync(string payload, CancellationToken ct)
    {
        Sent.Add(payload);
        return Task.CompletedTask;
    }

    public Task<string?> ReceiveTextAsync(CancellationToken ct)
        => Task.FromResult(_incoming.Count > 0 ? _incoming.Dequeue() : null);

    public void Abort() => IsOpen = false;
    public void Dispose() { }
}

/// <summary>
/// <see cref="HttpMessageHandler"/> stub: responds per request via the
/// delegate, a canned body, or a queue (each request dequeues the next
/// response; an empty queue answers 400 Bad Request — the Twitch token-poll
/// pending/error shape). An optional gate parks the request until the test
/// releases it (the former per-file BlockingHandler shape). Use the static
/// factories for the common single-body and not-found shapes.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _respond;
    private readonly Queue<HttpResponseMessage>? _responses;
    private readonly TaskCompletionSource? _gate;
    public int Calls { get; private set; }
    public List<string> RequestUrls { get; } = [];

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, TaskCompletionSource? gate = null)
    {
        _respond = respond;
        _gate = gate;
    }

    public StubHttpHandler(string body)
        : this(_ => Ok(body))
    {
    }

    public StubHttpHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

    public void Enqueue(HttpResponseMessage response) => _responses?.Enqueue(response);

    public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        RequestUrls.Add(request.RequestUri?.ToString() ?? "");
        if (_gate is not null)
        {
            await _gate.Task.ConfigureAwait(false);
        }
        if (_responses is not null)
        {
            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
        return _respond!(request);
    }
}

/// <summary>
/// PresentMon interop stub — keeps the real PresentMonAPI2.dll (and its
/// load-time side effects) out of the test host. Scriptable: the unavailable
/// defaults match a missing library; available producers set
/// <see cref="IsAvailable"/> and the poll/track surfaces.
/// </summary>
internal sealed class StubPresentMonNative : IPresentMonNative
{
    public bool IsAvailable { get; set; }
    public string? UnavailableReason { get; set; } = "stub (test)";
    public bool OpenSessionResult { get; set; }
    public bool TrackProcessResult { get; set; } = true;
    public PresentMonDynamicSample? PollResult { get; set; }
    public PmStatus PollStatus { get; set; } = PmStatus.Success;
    public IReadOnlyList<double> FrameTimes { get; set; } = [];
    public Func<int, bool>? TrackHandler { get; set; }
    public Func<int, PresentMonPollResult>? PollHandler { get; set; }

    public int OpenSessionCalls { get; private set; }
    public int CloseSessionCalls { get; private set; }
    public bool Disposed { get; private set; }
    public List<int> TrackedProcessIds { get; } = [];
    public List<int> StoppedProcessIds { get; } = [];
    public List<int> PolledProcessIds { get; } = [];

    public bool OpenSession()
    {
        OpenSessionCalls++;
        return OpenSessionResult;
    }

    public void CloseSession() => CloseSessionCalls++;

    public bool TrackProcess(int processId)
    {
        TrackedProcessIds.Add(processId);
        return TrackHandler is null ? TrackProcessResult : TrackHandler(processId);
    }

    public bool StopTrackProcess(int processId)
    {
        StoppedProcessIds.Add(processId);
        return true;
    }

    public PresentMonPollResult PollDynamic(int processId)
    {
        PolledProcessIds.Add(processId);
        if (PollHandler is not null)
        {
            return PollHandler(processId);
        }
        return new PresentMonPollResult(
            PollStatus == PmStatus.Success ? PollResult : null, PollStatus);
    }

    public IReadOnlyList<double> DrainFrameTimes(int processId) => FrameTimes;

    public void Dispose() => Disposed = true;
}

/// <summary>Scriptable in-memory <see cref="ILhmMapSource"/>: settable map
/// bytes and error plus a call counter — drives the reader's poll policy in
/// tests (map present, unavailable with source error, null without error,
/// malformed).</summary>
internal sealed class StubLhmMapSource : ILhmMapSource
{
    public byte[]? Bytes { get; set; }
    public string? Error { get; set; }
    public int Calls { get; private set; }

    public byte[]? TryReadSensorsMap(out string? error)
    {
        Calls++;
        error = Error;
        return Bytes;
    }
}

/// <summary>SMTC source seam: hands out an injectable manager (null for the
/// no-manager path).</summary>
internal sealed class StubMediaSessionSource : IMediaSessionSource
{
    public StubMediaSessionSourceManager? Manager { get; set; } = new();

    public Task<IMediaSessionSourceManager?> GetManagerAsync()
        => Task.FromResult<IMediaSessionSourceManager?>(Manager);
}

/// <summary>One recorded vendor control transfer (direction + request +
/// wValue) — the raw transcript the transport-policy tests assert against.</summary>
internal sealed record ControlCall(string Direction, byte Request, ushort WValue);

/// <summary>
/// The transport-policy seam: an <see cref="ITransferBackend"/> that records
/// every control and bulk transfer, so the protocol framing the transport
/// owns (init sequence, frame header, touch parsing) is assertable exactly —
/// no hardware, no USB.
/// </summary>
internal sealed class RecordingBackend : ITransferBackend
{
    public List<ControlCall> ControlCalls { get; } = [];
    public List<byte[]> BulkWrites { get; } = [];
    public bool IsOpen { get; set; } = true;
    public bool ControlOutResult { get; set; } = true;
    public bool ControlInResult { get; set; } = true;
    public bool BulkWriteResult { get; set; } = true;
    public byte[]? TouchResponse { get; set; }

    public bool ControlOut(byte request, ushort wValue, byte[]? data)
    {
        ControlCalls.Add(new ControlCall("out", request, wValue));
        return ControlOutResult;
    }

    public bool ControlIn(byte request, byte[] buffer, out int transferred, ushort wValue = 0, ushort wIndex = 0)
    {
        ControlCalls.Add(new ControlCall("in", request, wValue));
        transferred = 0;
        if (TouchResponse is not null && request == DisplayProtocolConstants.CmdGetTouch)
        {
            TouchResponse.CopyTo(buffer, 0);
            transferred = TouchResponse.Length;
        }
        else if (ControlInResult)
        {
            // Success contract: a full-buffer transfer, matching the real
            // backends (which only succeed on transferred > 0).
            transferred = buffer.Length;
        }
        return ControlInResult && transferred > 0;
    }

    /// <summary>When set, reports a partial transfer (short write) — mirroring
    /// the real backends' full-transfer contract, a short write fails.</summary>
    public int? BulkWriteTransferred { get; set; }

    /// <summary>Called on the writing thread when a bulk write begins (before
    /// the gate below parks it) — the contention pin's "the write is in
    /// flight" signal.</summary>
    public Action? BulkWriteEntered { get; set; }

    /// <summary>When set, the write thread parks here until the test releases
    /// it (the slow-device seam, no sleeps in test code): the test holds a
    /// frame write in flight while driving the other hot paths.</summary>
    public ManualResetEventSlim? HoldBulkWriteUntil { get; set; }

    public bool BulkWrite(byte pipeId, byte[] data, out int transferred)
    {
        BulkWriteEntered?.Invoke();
        HoldBulkWriteUntil?.Wait();
        BulkWrites.Add(data);
        transferred = BulkWriteTransferred ?? data.Length;
        return BulkWriteResult && transferred == data.Length;
    }

    public void Dispose() => IsOpen = false;
}

/// <summary>
/// SMTC manager seam: settable current/sessions plus subscription counters
/// and raise methods, so the monitor's event wiring is assertable.
/// </summary>
internal sealed class StubMediaSessionSourceManager : IMediaSessionSourceManager
{
    private Action? _currentSessionChanged;
    private Action? _sessionsChanged;

    public StubMediaSession? Current { get; set; }

    public List<StubMediaSession> Sessions { get; set; } = [];

    public int CurrentSessionChangedSubscriptionCount { get; private set; }

    public int SessionsChangedSubscriptionCount { get; private set; }

    public int DisposalCount { get; private set; }

    public void Dispose() => DisposalCount++;

    public event Action? CurrentSessionChanged
    {
        add { _currentSessionChanged += value; CurrentSessionChangedSubscriptionCount++; }
        remove { _currentSessionChanged -= value; CurrentSessionChangedSubscriptionCount--; }
    }

    public event Action? SessionsChanged
    {
        add { _sessionsChanged += value; SessionsChangedSubscriptionCount++; }
        remove { _sessionsChanged -= value; SessionsChangedSubscriptionCount--; }
    }

    public IMediaSessionSourceSession? GetCurrentSession() => Current;

    /// <summary>When set, GetSessions() hands back a fresh wrapper per session
    /// per call (the production WinRT seam's shape: a new adapter, each
    /// subscribing to the session's events). Null keeps the held instances
    /// (the in-memory seam's shape).</summary>
    public Func<StubMediaSession, IMediaSessionSourceSession>? FreshWrapperFor { get; set; }

    public IReadOnlyList<IMediaSessionSourceSession> GetSessions()
        => FreshWrapperFor is null ? Sessions : Sessions.Select(FreshWrapperFor).ToList();

    public void RaiseCurrentSessionChanged() => _currentSessionChanged?.Invoke();

    public void RaiseSessionsChanged() => _sessionsChanged?.Invoke();
}

/// <summary>
/// SMTC session seam: settable properties/playback/timeline (or a func for
/// async-resolution paths), subscription counters, control-call counters, and
/// raise methods — the monitor's per-session wiring is assertable.
/// </summary>
internal sealed class StubMediaSession : IMediaSessionSourceSession
{
    private Action? _mediaPropertiesChanged;
    private Action? _playbackInfoChanged;
    private Action? _timelinePropertiesChanged;

    public object Identity => this;

    public string SourceAppUserModelId { get; set; } = "fake.app";

    public MediaPropertiesData? Properties { get; set; }

    public PlaybackInfoData? PlaybackInfo { get; set; }

    public TimelinePropertiesData? Timeline { get; set; }

    public Func<Task<MediaPropertiesData?>>? PropertiesFunc { get; set; }

    public int MediaPropertiesSubscriptionCount { get; private set; }

    public int PlaybackInfoSubscriptionCount { get; private set; }

    public int TimelineSubscriptionCount { get; private set; }

    public int PlayCalls { get; private set; }

    public int PauseCalls { get; private set; }

    public int NextCalls { get; private set; }

    public int PreviousCalls { get; private set; }

    public int ShuffleCalls { get; private set; }

    public int RepeatCalls { get; private set; }

    public int SeekCalls { get; private set; }

    public bool LastShuffle { get; private set; }

    public MediaRepeatMode LastRepeat { get; private set; }

    public long LastSeekTicks { get; private set; }

    public int DisposalCount { get; private set; }

    public void Dispose() => DisposalCount++;

    public event Action? MediaPropertiesChanged
    {
        add { _mediaPropertiesChanged += value; MediaPropertiesSubscriptionCount++; }
        remove { _mediaPropertiesChanged -= value; MediaPropertiesSubscriptionCount--; }
    }

    public event Action? PlaybackInfoChanged
    {
        add { _playbackInfoChanged += value; PlaybackInfoSubscriptionCount++; }
        remove { _playbackInfoChanged -= value; PlaybackInfoSubscriptionCount--; }
    }

    public event Action? TimelinePropertiesChanged
    {
        add { _timelinePropertiesChanged += value; TimelineSubscriptionCount++; }
        remove { _timelinePropertiesChanged -= value; TimelineSubscriptionCount--; }
    }

    public Task<MediaPropertiesData?> TryGetMediaPropertiesAsync()
        => PropertiesFunc is not null ? PropertiesFunc() : Task.FromResult(Properties);

    /// <summary>How many times GetPlaybackInfo was called — the signal that a
    /// refresh resumed past its awaited properties fetch (used to wait out a
    /// stale refresh's continuation without a fixed delay).</summary>
    public int PlaybackInfoCalls { get; private set; }

    public PlaybackInfoData? GetPlaybackInfo()
    {
        PlaybackInfoCalls++;
        return PlaybackInfo;
    }

    public TimelinePropertiesData? GetTimelineProperties() => Timeline;

    public Task<bool> TryPlayAsync() { PlayCalls++; return Task.FromResult(true); }

    public Task<bool> TryPauseAsync() { PauseCalls++; return Task.FromResult(true); }

    public Task<bool> TrySkipNextAsync() { NextCalls++; return Task.FromResult(true); }

    public Task<bool> TrySkipPreviousAsync() { PreviousCalls++; return Task.FromResult(true); }

    public Task<bool> TryChangeShuffleActiveAsync(bool shuffle)
    {
        ShuffleCalls++;
        LastShuffle = shuffle;
        return Task.FromResult(true);
    }

    public Task<bool> TryChangeAutoRepeatModeAsync(MediaRepeatMode mode)
    {
        RepeatCalls++;
        LastRepeat = mode;
        return Task.FromResult(true);
    }

    public Task<bool> TryChangePlaybackPositionAsync(long positionTicks)
    {
        SeekCalls++;
        LastSeekTicks = positionTicks;
        return Task.FromResult(true);
    }

    public void RaiseMediaPropertiesChanged() => _mediaPropertiesChanged?.Invoke();

    public void RaisePlaybackInfoChanged() => _playbackInfoChanged?.Invoke();

    public void RaiseTimelinePropertiesChanged() => _timelinePropertiesChanged?.Invoke();
}

/// <summary>
/// A disposable wrapper around a <see cref="StubMediaSession"/> that shares
/// the wrapped session's identity, modeling the production WinRT seam: every
/// GetSessions() call builds a fresh adapter per session (each subscribing to
/// the session's events), so the held adapter and the returned wrappers are
/// different instances for the same session. The monitor's CycleSession
/// fresh-wrapper disposal is pinned against this shape.
/// </summary>
internal sealed class FreshSessionWrapper : IMediaSessionSourceSession
{
    private readonly StubMediaSession _inner;

    public FreshSessionWrapper(StubMediaSession inner) => _inner = inner;

    public object Identity => _inner.Identity;

    public string SourceAppUserModelId => _inner.SourceAppUserModelId;

    public int DisposalCount { get; private set; }

    public void Dispose() => DisposalCount++;

    public event Action? MediaPropertiesChanged
    {
        add => _inner.MediaPropertiesChanged += value;
        remove => _inner.MediaPropertiesChanged -= value;
    }

    public event Action? PlaybackInfoChanged
    {
        add => _inner.PlaybackInfoChanged += value;
        remove => _inner.PlaybackInfoChanged -= value;
    }

    public event Action? TimelinePropertiesChanged
    {
        add => _inner.TimelinePropertiesChanged += value;
        remove => _inner.TimelinePropertiesChanged -= value;
    }

    public Task<MediaPropertiesData?> TryGetMediaPropertiesAsync() => _inner.TryGetMediaPropertiesAsync();

    public PlaybackInfoData? GetPlaybackInfo() => _inner.GetPlaybackInfo();

    public TimelinePropertiesData? GetTimelineProperties() => _inner.GetTimelineProperties();

    public Task<bool> TryPlayAsync() => _inner.TryPlayAsync();

    public Task<bool> TryPauseAsync() => _inner.TryPauseAsync();

    public Task<bool> TrySkipNextAsync() => _inner.TrySkipNextAsync();

    public Task<bool> TrySkipPreviousAsync() => _inner.TrySkipPreviousAsync();

    public Task<bool> TryChangeShuffleActiveAsync(bool shuffle) => _inner.TryChangeShuffleActiveAsync(shuffle);

    public Task<bool> TryChangeAutoRepeatModeAsync(MediaRepeatMode mode) => _inner.TryChangeAutoRepeatModeAsync(mode);

    public Task<bool> TryChangePlaybackPositionAsync(long positionTicks) => _inner.TryChangePlaybackPositionAsync(positionTicks);
}

/// <summary>
/// The in-memory <see cref="IAudioCaptureSource"/>: emission, start/stop
/// truth, disposal counting, and the half-opened-source path (FailStart) —
/// the visualizer's capture-lifecycle tests and the widget's
/// render/capture interplay drive the same double.
/// </summary>
internal sealed class FakeAudioCaptureSource : IAudioCaptureSource
{
    private volatile bool _isCapturing;

    public bool IsCapturing => _isCapturing;

    /// <summary>When set, Start throws — the half-opened-source path the
    /// lifecycle must dispose and retry.</summary>
    public bool FailStart { get; set; }

    public int DisposalCount { get; private set; }

    public List<float[]> Delivered { get; } = [];

    public event Action<float[]>? SamplesAvailable;

    public void Start()
    {
        if (FailStart) throw new InvalidOperationException("capture start failed");
        _isCapturing = true;
    }

    public void Stop()
    {
        _isCapturing = false;
        SamplesAvailable = null;
    }

    public void Emit(float[] samples)
    {
        Delivered.Add(samples);
        SamplesAvailable?.Invoke(samples);
    }

    public void Dispose()
    {
        DisposalCount++;
        Stop();
    }
}

/// <summary>
/// Owns the App on a dedicated STA thread (DialogHostTests and
/// MainWindowConstructionTests used to copy this): WPF object creation
/// requires STA, Application.Current is process-wide (created once), and
/// pack://application resources only resolve while an Application lives on
/// this thread.
/// </summary>
internal sealed class StaHost
{
    private readonly object _gate = new();
    private readonly Thread _thread;
    private Func<object?>? _work;
    private object? _result;
    private Exception? _workError;
    private bool _done;

    public StaHost(string threadName)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = threadName };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        while (true)
        {
            lock (_gate)
            {
                // STA pump: no exit — the loop dies with the test process.
                // (S2190 intentionally suppressed: this is a message-pump loop,
                // not recursion.)
#pragma warning disable S2190
                while (_work is null) Monitor.Wait(_gate);
#pragma warning restore S2190
                var work = _work ?? throw new InvalidOperationException("work was signaled without a delegate");
                _work = null;
                try
                {
                    EnsureApp(); // one fresh App per invoke (see EnsureApp)
                    _result = work();
                    _workError = null;
                }
                catch (Exception ex)
                {
                    _workError = ex;
                }
                _done = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void EnsureApp()
    {
        // One fresh App per invoke, never a reused Application.Current. The
        // process-wide Application instance and its Resources dictionary must
        // not be shared across invokes or classes: a reused App's Clear +
        // re-initialize enumerates a dictionary other threads can still
        // mutate (WPF-internal resource work from a live window, another
        // class's theme apply), and "Collection was modified" mid-Clear used
        // to abort the invoke BEFORE the caller's window.Close — leaking the
        // window with its live engine and telemetry loops. A fresh App owns
        // an empty dictionary only its creating thread touches.
        // `new App()` does not load App.xaml — the generated Main calls
        // InitializeComponent separately. Without it, Application resources
        // (e.g. the window's PrimaryFont StaticResource) are missing.
        ResetApplicationState();
        var app = new AppClass();
        app.InitializeComponent();
        // The test App must never auto-shutdown: a test window's close (the
        // last shown window of the test's Application) would otherwise begin
        // the Application's shutdown through OnLastWindowClose, and a
        // half-shut-down Application's static state makes the NEXT invoke's
        // App.InitializeComponent FailFast (the single-instance guard's
        // second-launch Shutdown was the recorded case; the same trap
        // through a window close). Production keeps the default
        // OnLastWindowClose; only the re-created-per-invoke test App is
        // pinned to explicit shutdown.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // The Application constructor queues DoStartup as a dispatcher
        // operation; any later pump on this thread (a nested ShowDialog, a
        // Dispatcher.Run) runs it and would construct + show the StartupUri
        // window — a production MainWindow with a real USB engine and
        // telemetry loops, which no test ever closes. The StartupUri
        // property setter rejects null, so the BAML-set value is cleared
        // through the private backing field DoStartup reads: window-less
        // DoStartup (OnStartup still runs — the theme apply, on this thread).
        var startupUriField = typeof(Application).GetField("_startupUri", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Application._startupUri field not found (WPF internal changed; the test-host orphan-window guard is off)");
        startupUriField.SetValue(app, null);
    }

    /// <summary>
    /// Resets the process-wide WPF Application state the test host must never
    /// inherit: the singleton fields (so a later class can create its own
    /// Application) and the shutdown flag that <see cref="Window.Close"/>
    /// sets when the closed window was the Application's last one —
    /// <see cref="Window.Show"/> silently no-ops while the flag is set, so
    /// any later window test would show nothing at all.
    /// </summary>
    public static void ResetApplicationState()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        FieldInfo appInstance = typeof(Application).GetField("_appInstance", flags)
            ?? throw new InvalidOperationException("Application._appInstance field not found");
        FieldInfo createdHere = typeof(Application).GetField("_appCreatedInThisAppDomain", flags)
            ?? throw new InvalidOperationException("Application._appCreatedInThisAppDomain field not found");
        appInstance.SetValue(null, null);
        createdHere.SetValue(null, false);

        PropertyInfo shuttingDown = typeof(Application).GetProperty("IsShuttingDown", flags)
            ?? throw new InvalidOperationException("Application.IsShuttingDown property not found");
        shuttingDown.SetValue(null, false);
    }

    /// <summary>
    /// Nulls the private Application._appInstance / _appCreatedInThisAppDomain
    /// fields so a later class can create its own Application instance, and
    /// clears the shutdown flag Window.Close may have left set.
    /// </summary>
    public void DetachApplication() => ResetApplicationState();

    /// <summary>Raw invocation (MainWindowConstructionTests): returns the
    /// result and any exception for the caller to assert.</summary>
    public (object? Result, Exception? Error) Invoke(Func<object?> work)
    {
        lock (_gate)
        {
            _result = null;
            _workError = null;
            _done = false;
            _work = work;
            Monitor.PulseAll(_gate);
            while (!_done) Monitor.Wait(_gate);

            return (_result, _workError);
        }
    }

    /// <summary>Fail-fast invocation (DialogHostTests): returns the result,
    /// Assert.Fails when the STA work threw.</summary>
    public T Run<T>(Func<T> work)
    {
        var (result, error) = Invoke(() => work());
        if (error is not null)
        {
            Assert.Fail($"STA work failed: {error}");
        }
        return (T)result!;
    }
}

/// <summary>
/// One-shot STA invocation for tests that need WPF objects but no Application
/// host: runs the work on a fresh background STA thread and fails the test
/// when the work threw. <see cref="StaHost"/> stays for tests whose WPF
/// objects need a live Application (pack:// resources, dialog pumps) — these
/// tests only need the apartment, so each call pays for one throwaway thread.
/// </summary>
internal static class StaRunner
{
    /// <summary>Runs <paramref name="work"/> on a fresh STA thread, failing the
    /// test when it throws.</summary>
    public static void Run(Action work) => Run(() => { work(); return true; });

    /// <summary>Runs <paramref name="work"/> on a fresh STA thread, returning
    /// its result, failing the test when it throws.</summary>
    public static T Run<T>(Func<T> work)
    {
        Exception? error = null;
        T result = default!;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
            Name = "StaRunner"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            Assert.Fail($"STA work failed: {error}");
        }
        return result;
    }
}

/// <summary>
/// Scriptable in-memory <see cref="IUsbDevice"/> for the LibUsb connect leg:
/// the open/claim/endpoint outcomes are settable and every Close is counted,
/// so the leg's teardown paths are drivable without hardware (the
/// <see cref="ModernWigiDash.Hardware.Transport.DisplayHidTransport.LibUsbDeviceProvider"/>
/// seam). Only the members the leg touches are functional; the rest throw
/// <see cref="NotSupportedException"/> (a third-party boundary — fakes are
/// appropriate).
/// </summary>
internal sealed class FakeLibUsbDevice : IUsbDevice
{
    public bool OpenThrows { get; set; }
    public bool ClaimResult { get; set; } = true;
    public bool WriterThrows { get; set; }
    public int CloseCalls { get; private set; }

    public ushort VendorId => 0x28DA;
    public ushort ProductId => 0xEF01;
    public bool IsOpen { get; private set; }
    public UsbDeviceInfo? Info => null; // the endpoint scan falls back on a null descriptor
    public DeviceHandle? DeviceHandle => null;
    public int Configuration { get; private set; }
    public ReadOnlyCollection<UsbConfigInfo> Configs { get; } = new([]);
    public LocationId LocationId { get; } = default;

    public void Open()
    {
        if (OpenThrows) throw new InvalidOperationException("fake open failure");
        IsOpen = true;
    }

    public bool TryOpen()
    {
        Open();
        return IsOpen;
    }

    public void SetConfiguration(int config) => Configuration = (byte)config;

    public bool ClaimInterface(int interfaceID) => ClaimResult;

    public bool ReleaseInterface(int interfaceID) => true;

    public void Close()
    {
        IsOpen = false;
        CloseCalls++;
    }

    public void ResetDevice() { }

    public bool SetAltInterface(int alternateID) => true;

    public void GetAltInterfaceSetting(byte interfaceID, out byte selectedAltInterfaceID) => selectedAltInterfaceID = 0;

    public bool GetAltInterface(out int alternateID)
    {
        alternateID = 0;
        return true;
    }

    public IUsbDevice Clone() => throw new NotSupportedException();

    public void Dispose() => Close();

    public UsbEndpointWriter OpenEndpointWriter(WriteEndpointID writeEndpointID)
        => OpenEndpointWriter(writeEndpointID, EndpointType.Bulk);

    public UsbEndpointWriter OpenEndpointWriter(WriteEndpointID writeEndpointID, EndpointType endpointType)
    {
        if (WriterThrows) throw new InvalidOperationException("fake endpoint-writer failure");
        return null!; // the tests only exercise the failure paths; a success needs a real device
    }

    public UsbEndpointReader OpenEndpointReader(ReadEndpointID readEndpointID) => throw new NotSupportedException();
    public UsbEndpointReader OpenEndpointReader(ReadEndpointID readEndpointID, int readBufferSize) => throw new NotSupportedException();
    public UsbEndpointReader OpenEndpointReader(ReadEndpointID readEndpointID, int readBufferSize, EndpointType endpointType) => throw new NotSupportedException();
    public UsbEndpointTransferQueueReader OpenEndpointTransferQueueReader(ReadEndpointID readEndpointId, int readBufferSize, CancellationToken token, int transferQueueSize = 1) => throw new NotSupportedException();

    public int ControlTransfer(UsbSetupPacket setupPacket) => throw new NotSupportedException();
    public int ControlTransfer(UsbSetupPacket setupPacket, byte[] buffer, int offset, int length) => throw new NotSupportedException();
    public int ControlTransfer(UsbSetupPacket setupPacket, byte[] buffer, int offset, out int transferLength) => throw new NotSupportedException();
    public Task<int> ControlTransferAsync(UsbSetupPacket setupPacket) => throw new NotSupportedException();
    public Task<int> ControlTransferAsync(UsbSetupPacket setupPacket, byte[] buffer, int offset, int length) => throw new NotSupportedException();

    public bool GetDescriptor(byte descriptorType, byte index, short langId, IntPtr buffer, int bufferLength, out int transferLength) => throw new NotSupportedException();
    public bool GetDescriptor(byte descriptorType, byte index, short langId, object buffer, int bufferLength, out int transferLength) => throw new NotSupportedException();
    public bool GetLangIDs(out short[] langIDs) => throw new NotSupportedException();
    public bool GetString(out string stringData, short langId, byte stringIndex) => throw new NotSupportedException();
    public string GetStringDescriptor(byte descriptorIndex, bool failSilently = false) => throw new NotSupportedException();
    public bool TryGetConfigDescriptor(byte configIndex, out UsbConfigInfo descriptor) => throw new NotSupportedException();
}

/// <summary>
/// Async polling helper for tests: waits until <paramref name="condition"/> holds
/// or <paramref name="timeout"/> elapses, then asserts the condition. Replaces
/// Thread.Sleep-based polling loops (S2925) with Task.Delay-based async waits so
/// the test thread is released instead of blocked.
/// </summary>
internal static class TestWait
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        bool met = condition();
        while (!met && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5).ConfigureAwait(false);
            met = condition();
        }

        // Assert the last evaluation result — never re-evaluate: conditions may
        // be stateful (e.g. a poll that acquires a pooled buffer on success).
        Assert.IsTrue(met, $"Condition was not met within {timeout}.");
    }

    /// <summary>
    /// Waits for the APPLIED state of the widget's post-await continuation —
    /// the snapshot rows, the resolved label, the pending write-back — not the
    /// client's fetch-completion counter: the counter increments inside the
    /// client's finally BEFORE the continuation applies the result, so a
    /// counter-based wait can race the apply on a busy scheduler (flush early,
    /// read stale state). An idempotent flush belongs inside the predicate —
    /// re-checked on every poll until it lands.
    /// </summary>
    public static Task WaitForApplied(Func<bool> appliedState, TimeSpan? timeout = null)
        => WaitUntilAsync(appliedState, timeout ?? DefaultTimeout);
}

/// <summary>
/// The WPF owner-window rule the dialog tests share: a window can only be
/// taken as another window's Owner while it is already shown, so the owner's
/// Show happens once with this one spelling of the requirement.
/// </summary>
internal static class WpfWindow
{
    public static void ShowOwner(Window owner) => owner.Show();
}

/// <summary>
/// The in-memory tray surface the tray-controller and the window
/// close-intercept tests drive: records the show/hide/dispose calls, tracks
/// the live state the N1 guard reads, and re-raises the seam's events on
/// demand. A <c>showBringsUp: false</c> instance mirrors the production
/// surface's refused Show (the ico file missing from the output), so the
/// controller's honest verdict line is drivable without an OS notification
/// area.
/// </summary>
internal sealed class FakeTraySurface(bool showBringsUp = true) : ITrayIconSurface
{
    public int ShowCount { get; private set; }
    public bool HideCalled { get; private set; }
    public bool Disposed { get; private set; }
    public bool IsLive { get; private set; }

    public event Action? SingleClicked;
    public event Action<TrayMenuCommand>? MenuSelected;

    public void Show()
    {
        ShowCount++;
        if (showBringsUp)
        {
            IsLive = true;
        }
    }

    public void Hide()
    {
        HideCalled = true;
        IsLive = false;
    }

    public void RaiseSingleClick() => SingleClicked?.Invoke();

    public void RaiseMenu(TrayMenuCommand command) => MenuSelected?.Invoke(command);

    public void Dispose() => Disposed = true;
}
