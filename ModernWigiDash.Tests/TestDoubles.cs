using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using Windows.Media;
using ModernWigiDash.App.LibreHardwareService;
using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
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

    public void LogInfo(string message) => Infos.Add(message);
    public void LogError(string message, Exception? ex = null) => Errors.Add(message);
    public void RequestRender() => Renders++;
    public void RequestInspectorRefresh() { }
    public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt) => AuthShown++;
    public void CloseDeviceAuthorization() => AuthClosed++;

    public virtual void PersistProperty(object widget, string propertyName, object? value) { }
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
            await _gate.Task;
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

    public bool ControlIn(byte request, byte[] buffer, ushort wValue = 0, ushort wIndex = 0)
    {
        ControlCalls.Add(new ControlCall("in", request, wValue));
        if (TouchResponse is not null && request == DisplayProtocolConstants.CmdGetTouch)
            TouchResponse.CopyTo(buffer, 0);
        return ControlInResult;
    }

    /// <summary>When set, reports a partial transfer (short write) — mirroring
    /// the real backends' full-transfer contract, a short write fails.</summary>
    public int? BulkWriteTransferred { get; set; }

    public bool BulkWrite(byte pipeId, byte[] data, out int transferred)
    {
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

    public IReadOnlyList<IMediaSessionSourceSession> GetSessions() => Sessions;

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

    public MediaPlaybackAutoRepeatMode LastRepeat { get; private set; }

    public long LastSeekTicks { get; private set; }

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

    public Task<bool> TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode mode)
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
                    EnsureApp(); // Cleanup detaches the App between tests — recreate if needed
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
        // `new App()` does not load App.xaml — the generated Main calls
        // InitializeComponent separately. Without it, Application resources
        // (e.g. the window's PrimaryFont StaticResource) are missing.
        var app = Application.Current as AppClass;
        if (app is null)
        {
            app = new AppClass();
        }
        app.Resources.Clear(); // a reused App (ThemeManagerTests) only holds theme keys
        app.InitializeComponent();
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
