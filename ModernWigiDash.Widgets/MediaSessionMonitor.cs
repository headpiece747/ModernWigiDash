using Windows.Storage.Streams;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Owns the System Media Transport Controls session subsystem: SMTC manager
/// bootstrap, current-session tracking, per-session media-properties /
/// playback / timeline events, and the version-token-guarded snapshot
/// refresh. Consumers read <see cref="CurrentSnapshot"/> for rendering and
/// react to <see cref="SnapshotChanged"/> (null payload = session lost). The
/// WinRT surface is hidden behind the internal <see cref="IMediaSessionSource"/>
/// seam, and the playback state the seam carries is the neutral
/// <see cref="MediaPlaybackStatus"/> / <see cref="MediaRepeatMode"/>
/// vocabulary: <c>WinRtMediaSessionSource</c> is the one file that maps the
/// SMTC enums to it (and back, for the repeat command). The tap actions
/// (<see cref="TogglePlayPause"/>, <see cref="ToggleShuffle"/>,
/// <see cref="CycleRepeat"/>, <see cref="SeekToRatio"/>) decide can-run and
/// argument from this monitor's own latest snapshot, so a tap can never send
/// a desired state computed from a snapshot a caller held; the raw commands
/// (<see cref="Play"/>, <see cref="Pause"/> and friends) are the routing
/// surface the policy dispatches onto.
/// </summary>
public sealed class MediaSessionMonitor : IAsyncDisposable
{
    private readonly IMediaSessionSource _source;
    private readonly Action<string, Exception?>? _logError;

    private readonly Lock _stateLock = new();
    private readonly Lock _snapshotPublishLock = new();
    private readonly HashSet<IMediaSessionSourceSession> _attaching = new(ReferenceEqualityComparer.Instance);
    private IMediaSessionSourceManager? _manager;
    private IMediaSessionSourceSession? _session;
    // Volatile: the render thread reads the snapshot while the SMTC
    // continuation thread publishes it — reference assignment is atomic, and
    // volatile makes the cross-thread publish intent explicit (the engine's
    // _state uses the same pattern).
    private volatile MediaSnapshot? _snapshot;
    private int _refreshVersion;
    private bool _disposed;

    private bool IsDisposed
    {
        get { lock (_stateLock) return _disposed; }
    }

    private IMediaSessionSourceSession? ActiveSession
    {
        get { lock (_stateLock) return _disposed ? null : _session; }
    }

    /// <summary>Test seam: injectable clock for the snapshot timestamp.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Raised after each completed refresh with the new snapshot payload, or
    /// with null when the current session is lost or SMTC bootstrap fails.
    /// </summary>
    public event Action<MediaSessionUpdate?>? SnapshotChanged;

    /// <summary>The monitor's latest published snapshot, or null when no session is held.</summary>
    public MediaSnapshot? CurrentSnapshot => _snapshot;

    /// <summary>Production constructor over the WinRT SMTC source.</summary>
    public MediaSessionMonitor(Action<string, Exception?>? logError = null)
        : this(new WinRtMediaSessionSource(), logError)
    {
    }

    internal MediaSessionMonitor(IMediaSessionSource source, Action<string, Exception?>? logError = null)
    {
        _source = source;
        _logError = logError;
    }

    /// <summary>
    /// Bootstraps the SMTC manager and attaches the current session. Runs
    /// synchronously up to the first await, so the manager is created in the
    /// calling apartment (the interactive session on the WPF UI thread).
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var manager = await _source.GetManagerAsync().ConfigureAwait(false);
            if (manager is null) return;

            if (IsDisposed)
            {
                SafeDisposeManager(manager);
                return;
            }

            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            manager.SessionsChanged += OnSessionsChanged;

            IMediaSessionSourceManager? previousManager = null;
            bool racedDispose;
            lock (_stateLock)
            {
                if (_disposed)
                {
                    racedDispose = true;
                }
                else
                {
                    previousManager = _manager;
                    _manager = manager;
                    racedDispose = false;
                }
            }

            if (racedDispose)
            {
                UnsubscribeAndDisposeManager(manager);
                return;
            }

            if (previousManager is not null && !ReferenceEquals(previousManager, manager))
            {
                UnsubscribeAndDisposeManager(previousManager);
            }

            AttachSession(manager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _logError?.Invoke($"SMTC init failed: {ex.Message}", ex);
                SnapshotChanged?.Invoke(null);
            }
        }
    }

    /// <summary>Advances to the next SMTC session (tap the source badge).</summary>
    public void CycleSession()
    {
        IMediaSessionSourceManager? manager;
        lock (_stateLock)
        {
            if (_disposed || _manager is null) return;
            manager = _manager;
        }

        IReadOnlyList<IMediaSessionSourceSession> sessions;
        try
        {
            sessions = manager.GetSessions();
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _logError?.Invoke($"SMTC cycle failed: {ex.Message}", ex);
            return;
        }

        IMediaSessionSourceSession? current = ActiveSession;
        if (IsDisposed || sessions.Count <= 1)
        {
            // GetSessions() may have built fresh WinRT adapters even for 0 or 1 sessions.
            // Dispose any wrapper we are not holding.
            for (int i = 0; i < sessions.Count; i++)
            {
                if (!ReferenceEquals(sessions[i], current)) SafeDisposeSession(sessions[i]);
            }
            return;
        }

        int idx = -1;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (IsSameSession(sessions[i], current))
            {
                idx = i;
                break;
            }
        }

        int nextIdx = (idx + 1) % sessions.Count;
        // GetSessions() wraps every session in a fresh subscription (the
        // WinRT seam builds one adapter per session per call). AttachSession
        // releases only the PREVIOUSLY HELD adapter, so the departing
        // session's fresh wrapper (sessions[idx]) would otherwise stay
        // rooted with its live subscriptions: dispose every fresh wrapper
        // that is neither the arriving one nor the held instance the
        // attach releases.
        for (int i = 0; i < sessions.Count; i++)
        {
            if (i != nextIdx && !ReferenceEquals(sessions[i], current)) SafeDisposeSession(sessions[i]);
        }
        AttachSession(sessions[nextIdx]);
    }

    private static bool IsSameSession(IMediaSessionSourceSession? a, IMediaSessionSourceSession? b)
    {
        if (a is null || b is null) return false;
        if (ReferenceEquals(a, b)) return true;
        if (a.Identity is not null && b.Identity is not null)
        {
            return ReferenceEquals(a.Identity, b.Identity) || a.Identity.Equals(b.Identity);
        }
        return false;
    }

    /// <summary>Sends the play command to the current session.</summary>
    public void Play() => DispatchControl(static s => s.TryPlayAsync());

    /// <summary>Sends the pause command to the current session.</summary>
    public void Pause() => DispatchControl(static s => s.TryPauseAsync());

    /// <summary>Sends the skip-next command to the current session.</summary>
    public void Next() => DispatchControl(static s => s.TrySkipNextAsync());

    /// <summary>Sends the skip-previous command to the current session.</summary>
    public void Previous() => DispatchControl(static s => s.TrySkipPreviousAsync());

    /// <summary>Sends the shuffle command to the current session.</summary>
    /// <param name="enabled">The desired shuffle state.</param>
    public void SetShuffle(bool enabled) => DispatchControl(s => s.TryChangeShuffleActiveAsync(enabled));

    /// <summary>Sends the repeat-mode command to the current session.</summary>
    /// <param name="mode">The desired repeat mode.</param>
    public void SetRepeat(MediaRepeatMode mode) => DispatchControl(s => s.TryChangeAutoRepeatModeAsync(mode));

    /// <summary>Sends the seek command to the current session.</summary>
    /// <param name="position">The desired playback position.</param>
    public void Seek(TimeSpan position) => DispatchControl(s => s.TryChangePlaybackPositionAsync(position.Ticks));

    private void DispatchControl(Func<IMediaSessionSourceSession, Task<bool>> command)
    {
        var session = ActiveSession;
        if (session is null) return;
        _ = RunControlAsync(session, command);
    }

    private async Task RunControlAsync(
        IMediaSessionSourceSession session,
        Func<IMediaSessionSourceSession, Task<bool>> command)
    {
        try
        {
            await command(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _logError?.Invoke($"SMTC control failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The play/pause tap policy: a playing session pauses, a non-playing one
    /// plays, and a session that reports neither capability is a no-op. The
    /// decision reads this monitor's latest snapshot, so the command targets
    /// the state the monitor last saw, not a snapshot a caller held.
    /// </summary>
    public void TogglePlayPause()
    {
        var snap = _snapshot;
        if (snap is null) return;

        if (snap.IsPlaying && snap.CanPause) Pause();
        else if (!snap.IsPlaying && snap.CanPlay) Play();
    }

    /// <summary>
    /// The shuffle tap policy: inverts the monitor's live shuffle state when
    /// the session reports shuffleable, otherwise a no-op.
    /// </summary>
    public void ToggleShuffle()
    {
        var snap = _snapshot;
        if (snap is null || !snap.CanShuffle) return;

        SetShuffle(!snap.Shuffle);
    }

    /// <summary>
    /// The repeat tap policy: steps the presentation's cycle
    /// (<see cref="NowPlayingPresentation.NextRepeatMode"/>, the one spelling)
    /// from the monitor's live repeat mode when the session reports
    /// repeatable, otherwise a no-op.
    /// </summary>
    public void CycleRepeat()
    {
        var snap = _snapshot;
        if (snap is null || !snap.CanRepeat) return;

        SetRepeat(NowPlayingPresentation.NextRepeatMode(snap.Repeat));
    }

    /// <summary>
    /// The seek tap policy: a ratio of the monitor's live duration, resolved
    /// and gated (the presentation's <see cref="NowPlayingPresentation.CanSeekNow"/>,
    /// the one spelling) against the monitor's own snapshot. The caller
    /// supplies the ratio (a pure layout measurement); pixels never cross
    /// this seam.
    /// </summary>
    public void SeekToRatio(double ratio)
    {
        var snap = _snapshot;
        if (snap is null || !NowPlayingPresentation.CanSeekNow(snap)) return;

        Seek(TimeSpan.FromSeconds(ratio * snap.Duration.TotalSeconds));
    }

    /// <summary>Unsubscribes, disposes the WinRT manager/session adapters, and clears the snapshot.</summary>
    public ValueTask DisposeAsync()
    {
        IMediaSessionSourceManager? manager;
        IMediaSessionSourceSession? session;
        lock (_stateLock)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            Interlocked.Increment(ref _refreshVersion);
            manager = _manager;
            session = _session;
            _manager = null;
            _session = null;
            _snapshot = null;
        }

        // The adapters own the WinRT event subscriptions the monitor's
        // neutral unsubscription does not reach; release them so the WinRT
        // manager/session do not outlive the monitor.
        if (manager is not null)
        {
            UnsubscribeAndDisposeManager(manager);
        }

        if (session is not null)
        {
            DetachAndDisposeSession(session);
        }

        return ValueTask.CompletedTask;
    }

    private void OnCurrentSessionChanged()
    {
        IMediaSessionSourceManager? manager;
        lock (_stateLock)
        {
            if (_disposed) return;
            manager = _manager;
        }

        if (manager is null) return;
        if (TryGetCurrentSession(manager, out var next))
        {
            AttachSession(next);
        }
    }

    private void OnSessionsChanged()
    {
        IMediaSessionSourceManager? manager;
        bool hasSession;
        lock (_stateLock)
        {
            if (_disposed) return;
            manager = _manager;
            hasSession = _session is not null;
        }

        if (hasSession)
        {
            _ = RefreshAsync();
        }
        else if (manager is not null && TryGetCurrentSession(manager, out var next))
        {
            AttachSession(next);
        }
    }

    private void OnMediaPropertiesChanged() => _ = RefreshAsync();

    private void OnPlaybackInfoChanged() => _ = RefreshAsync();

    private void OnTimelinePropertiesChanged() => _ = RefreshAsync();

    private bool TryGetCurrentSession(
        IMediaSessionSourceManager manager,
        out IMediaSessionSourceSession? session)
    {
        try
        {
            session = manager.GetCurrentSession();
            return true;
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _logError?.Invoke($"SMTC session lookup failed: {ex.Message}", ex);
            session = null;
            return false;
        }
    }

    private void AttachSession(IMediaSessionSourceSession? session)
    {
        if (session is null)
        {
            IMediaSessionSourceSession? oldToClear;
            lock (_stateLock)
            {
                if (_disposed || _session is null) return;
                oldToClear = _session;
                _session = null;
            }

            DetachAndDisposeSession(oldToClear);
            _ = RefreshAsync();
            return;
        }

        while (true)
        {
            IMediaSessionSourceSession? currentSnapshot;
            bool disposedNow;
            lock (_stateLock)
            {
                disposedNow = _disposed;
                currentSnapshot = _session;
            }

            if (disposedNow)
            {
                SafeDisposeSession(session);
                return;
            }

            // The in-memory seam hands back the very instance it holds, so a
            // same-reference attach is a no-op.
            if (ReferenceEquals(currentSnapshot, session)) return;

            // A fresh wrapper for a session we already hold (the WinRT seam
            // builds one per call): tear down its subscriptions and keep the
            // adapter we already own.
            if (currentSnapshot is not null && IsSameSession(currentSnapshot, session))
            {
                SafeDisposeSession(session);
                return;
            }

            bool readyToSubscribe = false;
            bool disposeIncoming = false;
            lock (_stateLock)
            {
                if (_disposed)
                {
                    disposeIncoming = true;
                }
                else if (ReferenceEquals(_session, currentSnapshot))
                {
                    if (!_attaching.Add(session)) return;
                    readyToSubscribe = true;
                }
            }

            if (disposeIncoming)
            {
                SafeDisposeSession(session);
                return;
            }

            if (!readyToSubscribe) continue;

            if (!TrySubscribeSessionEvents(session)) return;

            IMediaSessionSourceSession? displaced = null;
            bool commit = false;
            bool rejectDisposed = false;
            lock (_stateLock)
            {
                _attaching.Remove(session);
                if (_disposed)
                {
                    rejectDisposed = true;
                }
                else if (ReferenceEquals(_session, currentSnapshot))
                {
                    displaced = _session;
                    _session = session;
                    commit = true;
                }
            }

            if (rejectDisposed)
            {
                DetachAndDisposeSession(session);
                return;
            }

            if (commit)
            {
                if (displaced is not null)
                {
                    DetachAndDisposeSession(displaced);
                }
                _ = RefreshAsync();
                return;
            }

            // Another transition updated _session while we subscribed; undo our
            // subscriptions and re-evaluate against the new _session.
            DetachSessionEvents(session);
        }
    }

    private bool TrySubscribeSessionEvents(IMediaSessionSourceSession session)
    {
        try
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            return true;
        }
        catch (Exception ex)
        {
            lock (_stateLock) _attaching.Remove(session);
            if (!IsDisposed) _logError?.Invoke($"SMTC event attach failed: {ex.Message}", ex);
            DetachAndDisposeSession(session);
            return false;
        }
    }

    private void SafeDetachEvent(Action unsubscribe)
    {
        try
        {
            unsubscribe();
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _logError?.Invoke($"SMTC event detach failed: {ex.Message}", ex);
        }
    }

    private void DetachSessionEvents(IMediaSessionSourceSession session)
    {
        SafeDetachEvent(() => session.MediaPropertiesChanged -= OnMediaPropertiesChanged);
        SafeDetachEvent(() => session.PlaybackInfoChanged -= OnPlaybackInfoChanged);
        SafeDetachEvent(() => session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged);
    }

    private void DetachAndDisposeSession(IMediaSessionSourceSession session)
    {
        DetachSessionEvents(session);
        SafeDisposeSession(session);
    }

    private void UnsubscribeAndDisposeManager(IMediaSessionSourceManager manager)
    {
        SafeDetachEvent(() => manager.CurrentSessionChanged -= OnCurrentSessionChanged);
        SafeDetachEvent(() => manager.SessionsChanged -= OnSessionsChanged);
        SafeDisposeManager(manager);
    }

    private void SafeDisposeManager(IMediaSessionSourceManager manager)
    {
        try
        {
            manager.Dispose();
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"SMTC manager dispose failed: {ex.Message}", ex);
        }
    }

    private void SafeDisposeSession(IMediaSessionSourceSession session)
    {
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"SMTC session dispose failed: {ex.Message}", ex);
        }
    }

    private async Task RefreshAsync()
    {
        // The SMTC events fire on arbitrary threads, so several refreshes can
        // be in flight at once. Only the newest may publish a snapshot: the
        // version bump is atomic and the stale check below reads the latest
        // value, so a refresh overtaken after its await bails out.
        int refreshVersion = Interlocked.Increment(ref _refreshVersion);
        IMediaSessionSourceSession? session;
        bool disposed;
        lock (_stateLock)
        {
            disposed = _disposed;
            session = _session;
        }

        if (disposed) return;

        if (session is null)
        {
            lock (_snapshotPublishLock)
            {
                lock (_stateLock)
                {
                    if (_disposed || refreshVersion != Volatile.Read(ref _refreshVersion)) return;
                    _snapshot = null;
                }
                SnapshotChanged?.Invoke(null);
            }
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync().ConfigureAwait(false);
            var info = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            if (IsDisposed || refreshVersion != Volatile.Read(ref _refreshVersion)) return;

            lock (_snapshotPublishLock)
            {
                MediaSnapshot? previous;
                MediaSnapshot snapshot;
                lock (_stateLock)
                {
                    if (_disposed || refreshVersion != Volatile.Read(ref _refreshVersion)) return;
                    previous = _snapshot;
                    snapshot = BuildSnapshot(session, props, info, timeline);
                    _snapshot = snapshot;
                }

                string artKey = $"{session.SourceAppUserModelId}:{props?.Title}:{props?.Artist}:{props?.AlbumTitle}";
                if (previous is not null && previous.Position > TimeSpan.FromSeconds(3) &&
                    snapshot.Position < TimeSpan.FromSeconds(2.5))
                {
                    // Some SMTC providers reuse metadata across track transitions.
                    // A timeline reset is still a reliable signal to reload artwork.
                    artKey += $":track{refreshVersion}";
                }

                SnapshotChanged?.Invoke(new MediaSessionUpdate(snapshot, props?.Thumbnail, artKey));
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _logError?.Invoke($"SMTC refresh failed: {ex.Message}", ex);
        }
    }

    private MediaSnapshot BuildSnapshot(
        IMediaSessionSourceSession session,
        MediaPropertiesData? props,
        PlaybackInfoData? info,
        TimelinePropertiesData? timeline)
        => MediaSnapshotBuilder.Build(session.SourceAppUserModelId, props, info, timeline, Clock);

    /// <summary>Strips control characters (space kept) and caps at 256 chars;
    /// falls back when nothing survives. Internal so the rule is testable.</summary>
    internal static string Sanitize(string? value, string fallback)
        => MediaSnapshotBuilder.Sanitize(value, fallback);
}

/// <summary>
/// A point-in-time snapshot of one SMTC session's playback state, consumed by
/// the Now Playing widget's renderer.
/// </summary>
public sealed class MediaSnapshot
{
    /// <summary>The source app's user-model ID.</summary>
    public string SourceAppId { get; set; } = "";

    /// <summary>The track title (sanitized).</summary>
    public string Title { get; set; } = "";

    /// <summary>The track artist (sanitized).</summary>
    public string Artist { get; set; } = "";

    /// <summary>The album title (sanitized).</summary>
    public string Album { get; set; } = "";

    /// <summary>The album artist (sanitized).</summary>
    public string AlbumArtist { get; set; } = "";

    /// <summary>The track's number within the album.</summary>
    public int TrackNumber { get; set; }

    /// <summary>The album's total track count.</summary>
    public int AlbumTrackCount { get; set; }

    /// <summary>The track's genres (empty when the session reports none).</summary>
    public string[] Genres { get; set; } = [];

    /// <summary>The playback status in the neutral vocabulary.</summary>
    public MediaPlaybackStatus Status { get; set; }

    /// <summary>The playback position.</summary>
    public TimeSpan Position { get; set; }

    /// <summary>The total playback duration.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>When the timeline properties were last updated (UTC).</summary>
    public DateTimeOffset LastUpdated { get; set; }

    /// <summary>Whether shuffle is active.</summary>
    public bool Shuffle { get; set; }

    /// <summary>The repeat mode in the neutral vocabulary.</summary>
    public MediaRepeatMode Repeat { get; set; }

    /// <summary>The playback rate (1.0 when the session reports none).</summary>
    public double PlaybackRate { get; set; } = 1.0;

    /// <summary>Whether the session reports play enabled.</summary>
    public bool CanPlay { get; set; }

    /// <summary>Whether the session reports pause enabled.</summary>
    public bool CanPause { get; set; }

    /// <summary>Whether the session reports stop enabled.</summary>
    public bool CanStop { get; set; }

    /// <summary>Whether the session reports skip-next enabled.</summary>
    public bool CanNext { get; set; }

    /// <summary>Whether the session reports skip-previous enabled.</summary>
    public bool CanPrev { get; set; }

    /// <summary>Whether the session reports position seeking enabled.</summary>
    public bool CanSeek { get; set; }

    /// <summary>Whether the session reports shuffle enabled.</summary>
    public bool CanShuffle { get; set; }

    /// <summary>Whether the session reports repeat enabled.</summary>
    public bool CanRepeat { get; set; }

    /// <summary>Whether the status is Playing.</summary>
    public bool IsPlaying => Status == MediaPlaybackStatus.Playing;
}

/// <summary>
/// Payload of <see cref="MediaSessionMonitor.SnapshotChanged"/>: the snapshot
/// plus everything the artwork pipeline needs to react (the thumbnail stream
/// reference and the derived art key, which must match the snapshot exactly).
/// </summary>
public sealed record MediaSessionUpdate(MediaSnapshot Snapshot, IRandomAccessStreamReference? Thumbnail, string ArtKey);

/// <summary>
/// Test seam over the SMTC surface in the neutral vocabulary. The real
/// adapter (<c>WinRtMediaSessionSource</c>) projects the WinRT
/// manager/session types into this shape and maps their enums to
/// <see cref="MediaPlaybackStatus"/> / <see cref="MediaRepeatMode"/>; tests
/// drive a fake through the same interface without WinRT.
/// </summary>
internal interface IMediaSessionSource
{
    Task<IMediaSessionSourceManager?> GetManagerAsync();
}

internal interface IMediaSessionSourceManager : IDisposable
{
    event Action? CurrentSessionChanged;

    event Action? SessionsChanged;

    IMediaSessionSourceSession? GetCurrentSession();

    IReadOnlyList<IMediaSessionSourceSession> GetSessions();
}

internal interface IMediaSessionSourceSession : IDisposable
{
    /// <summary>Stable identity of the underlying SMTC session for equality checks.</summary>
    object Identity { get; }

    string SourceAppUserModelId { get; }

    event Action? MediaPropertiesChanged;

    event Action? PlaybackInfoChanged;

    event Action? TimelinePropertiesChanged;

    Task<MediaPropertiesData?> TryGetMediaPropertiesAsync();

    PlaybackInfoData? GetPlaybackInfo();

    TimelinePropertiesData? GetTimelineProperties();

    Task<bool> TryPlayAsync();

    Task<bool> TryPauseAsync();

    Task<bool> TrySkipNextAsync();

    Task<bool> TrySkipPreviousAsync();

    Task<bool> TryChangeShuffleActiveAsync(bool shuffle);

    Task<bool> TryChangeAutoRepeatModeAsync(MediaRepeatMode mode);

    Task<bool> TryChangePlaybackPositionAsync(long positionTicks);
}

internal sealed class MediaPropertiesData
{
    public string? Title { get; set; }

    public string? Artist { get; set; }

    public string? AlbumTitle { get; set; }

    public string? AlbumArtist { get; set; }

    public int TrackNumber { get; set; }

    public int AlbumTrackCount { get; set; }

    public IReadOnlyList<string>? Genres { get; set; }

    public IRandomAccessStreamReference? Thumbnail { get; set; }
}

internal sealed class PlaybackInfoData
{
    public MediaPlaybackStatus? PlaybackStatus { get; set; }

    public bool IsShuffleActive { get; set; }

    public MediaRepeatMode? AutoRepeatMode { get; set; }

    public double? PlaybackRate { get; set; }

    public MediaControlsData Controls { get; set; } = new();
}

internal sealed class MediaControlsData
{
    public bool IsPlayEnabled { get; set; }

    public bool IsPauseEnabled { get; set; }

    public bool IsStopEnabled { get; set; }

    public bool IsNextEnabled { get; set; }

    public bool IsPreviousEnabled { get; set; }

    public bool IsPlaybackPositionEnabled { get; set; }

    public bool IsShuffleEnabled { get; set; }

    public bool IsRepeatEnabled { get; set; }
}

internal sealed class TimelinePropertiesData
{
    public TimeSpan Position { get; set; }

    public TimeSpan EndTime { get; set; }

    public DateTimeOffset LastUpdatedTime { get; set; }
}
