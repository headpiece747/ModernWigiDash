using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Owns the System Media Transport Controls session subsystem: SMTC manager
/// bootstrap, current-session tracking, per-session media-properties /
/// playback / timeline events, and the version-token-guarded snapshot refresh.
/// Consumers read <see cref="CurrentSnapshot"/> for rendering and react to
/// <see cref="SnapshotChanged"/> (null payload = session lost). The WinRT
/// surface is hidden behind the internal <see cref="IMediaSessionSource"/>
/// seam — a real adapter projects the SMTC manager/session types, and tests
/// drive a fake through the same interface.
/// </summary>
public sealed class MediaSessionMonitor : IAsyncDisposable
{
    private readonly IMediaSessionSource _source;
    private readonly Action<string, Exception?>? _logError;

    private IMediaSessionSourceManager? _manager;
    private IMediaSessionSourceSession? _session;
    // Volatile: the render thread reads the snapshot while the SMTC
    // continuation thread publishes it — reference assignment is atomic, and
    // volatile makes the cross-thread publish intent explicit (the engine's
    // _state uses the same pattern).
    private volatile MediaSnapshot? _snapshot;
    private int _refreshVersion;
    private bool _disposed;

    /// <summary>Test seam: injectable clock for the snapshot timestamp.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Raised after each completed refresh with the new snapshot payload, or
    /// with null when the current session is lost or SMTC bootstrap fails.
    /// </summary>
    public event Action<MediaSessionUpdate?>? SnapshotChanged;

    public MediaSnapshot? CurrentSnapshot => _snapshot;

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
            var manager = await _source.GetManagerAsync();
            if (_disposed || manager is null) return;

            _manager = manager;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            manager.SessionsChanged += OnSessionsChanged;
            AttachSession(manager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"SMTC init failed: {ex.Message}", ex);
            SnapshotChanged?.Invoke(null);
        }
    }

    /// <summary>Advances to the next SMTC session (tap the source badge).</summary>
    public void CycleSession()
    {
        if (_manager is null) return;

        var sessions = _manager.GetSessions();
        if (sessions.Count <= 1) return;

        int idx = -1;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (ReferenceEquals(sessions[i].Identity, _session?.Identity))
            {
                idx = i;
                break;
            }
        }

        int nextIdx = (idx + 1) % sessions.Count;
        AttachSession(sessions[nextIdx]);
    }

    public void Play() => _ = _session?.TryPlayAsync();

    public void Pause() => _ = _session?.TryPauseAsync();

    public void Next() => _ = _session?.TrySkipNextAsync();

    public void Previous() => _ = _session?.TrySkipPreviousAsync();

    public void SetShuffle(bool enabled) => _ = _session?.TryChangeShuffleActiveAsync(enabled);

    public void SetRepeat(MediaPlaybackAutoRepeatMode mode) => _ = _session?.TryChangeAutoRepeatModeAsync(mode);

    public void Seek(TimeSpan position) => _ = _session?.TryChangePlaybackPositionAsync(position.Ticks);

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }
        DetachSessionEvents();
        _manager = null;
        _session = null;
        _snapshot = null;

        return ValueTask.CompletedTask;
    }

    private void OnCurrentSessionChanged() => AttachSession(_manager?.GetCurrentSession());

    private void OnSessionsChanged()
    {
        if (_session is null)
            AttachSession(_manager?.GetCurrentSession());
        else
            _ = RefreshAsync();
    }

    private void OnMediaPropertiesChanged() => _ = RefreshAsync();

    private void OnPlaybackInfoChanged() => _ = RefreshAsync();

    private void OnTimelinePropertiesChanged() => _ = RefreshAsync();

    private void AttachSession(IMediaSessionSourceSession? session)
    {
        if (ReferenceEquals(_session?.Identity, session?.Identity)) return;

        DetachSessionEvents();
        _session = session;

        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }

        _ = RefreshAsync();
    }

    private void DetachSessionEvents()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
    }

    private async Task RefreshAsync()
    {
        int refreshVersion = ++_refreshVersion;
        var session = _session;
        if (session is null)
        {
            _snapshot = null;
            SnapshotChanged?.Invoke(null);
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var info = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            if (_disposed || refreshVersion != _refreshVersion) return;

            var previous = _snapshot;
            var snapshot = BuildSnapshot(session, props, info, timeline);
            _snapshot = snapshot;

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
        catch (Exception ex)
        {
            _logError?.Invoke($"SMTC refresh failed: {ex.Message}", ex);
        }
    }

    private MediaSnapshot BuildSnapshot(
        IMediaSessionSourceSession session,
        MediaPropertiesData? props,
        PlaybackInfoData? info,
        TimelinePropertiesData? timeline)
    {
        return new MediaSnapshot
        {
            SourceAppId = session.SourceAppUserModelId ?? "",
            Title = Sanitize(props?.Title, ""),
            Artist = Sanitize(props?.Artist, ""),
            Album = Sanitize(props?.AlbumTitle, ""),
            AlbumArtist = Sanitize(props?.AlbumArtist, ""),
            TrackNumber = props?.TrackNumber ?? 0,
            AlbumTrackCount = props?.AlbumTrackCount ?? 0,
            Genres = props?.Genres?.ToArray() ?? [],
            Status = info?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed,
            Position = timeline?.Position ?? TimeSpan.Zero,
            Duration = timeline?.EndTime ?? TimeSpan.Zero,
            LastUpdated = timeline?.LastUpdatedTime ?? Clock.GetUtcNow(),
            Shuffle = info?.IsShuffleActive ?? false,
            Repeat = info?.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None,
            PlaybackRate = info?.PlaybackRate is > 0 ? info.PlaybackRate.Value : 1.0,
            CanPlay = info?.Controls.IsPlayEnabled ?? false,
            CanPause = info?.Controls.IsPauseEnabled ?? false,
            CanStop = info?.Controls.IsStopEnabled ?? false,
            CanNext = info?.Controls.IsNextEnabled ?? false,
            CanPrev = info?.Controls.IsPreviousEnabled ?? false,
            CanSeek = info?.Controls.IsPlaybackPositionEnabled ?? false,
            CanShuffle = info?.Controls.IsShuffleEnabled ?? false,
            CanRepeat = info?.Controls.IsRepeatEnabled ?? false
        };
    }

    /// <summary>Strips control characters (space kept) and caps at 256 chars;
    /// falls back when nothing survives. Internal so the rule is testable.</summary>
    internal static string Sanitize(string? input, string fallback)
    {
        if (string.IsNullOrEmpty(input)) return fallback;
        string clean = new string(input.Where(c => !char.IsControl(c) || c == ' ').Take(256).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
    }
}

/// <summary>
/// A point-in-time snapshot of one SMTC session's playback state, consumed by
/// the Now Playing widget's renderer.
/// </summary>
public sealed class MediaSnapshot
{
    public string SourceAppId { get; set; } = "";

    public string Title { get; set; } = "";

    public string Artist { get; set; } = "";

    public string Album { get; set; } = "";

    public string AlbumArtist { get; set; } = "";

    public int TrackNumber { get; set; }

    public int AlbumTrackCount { get; set; }

    public string[] Genres { get; set; } = [];

    public GlobalSystemMediaTransportControlsSessionPlaybackStatus Status { get; set; }

    public TimeSpan Position { get; set; }

    public TimeSpan Duration { get; set; }

    public DateTimeOffset LastUpdated { get; set; }

    public bool Shuffle { get; set; }

    public MediaPlaybackAutoRepeatMode Repeat { get; set; }

    public double PlaybackRate { get; set; } = 1.0;

    public bool CanPlay { get; set; }

    public bool CanPause { get; set; }

    public bool CanStop { get; set; }

    public bool CanNext { get; set; }

    public bool CanPrev { get; set; }

    public bool CanSeek { get; set; }

    public bool CanShuffle { get; set; }

    public bool CanRepeat { get; set; }

    public bool IsPlaying => Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
}

/// <summary>
/// Payload of <see cref="MediaSessionMonitor.SnapshotChanged"/>: the snapshot
/// plus everything the artwork pipeline needs to react (the thumbnail stream
/// reference and the derived art key, which must match the snapshot exactly).
/// </summary>
public sealed record MediaSessionUpdate(MediaSnapshot Snapshot, IRandomAccessStreamReference? Thumbnail, string ArtKey);

/// <summary>
/// Test seam over the WinRT SMTC surface. The real adapter projects
/// GlobalSystemMediaTransportControlsSessionManager/Session into this shape;
/// tests drive a fake through the same interface without WinRT.
/// </summary>
internal interface IMediaSessionSource
{
    Task<IMediaSessionSourceManager?> GetManagerAsync();
}

internal interface IMediaSessionSourceManager
{
    event Action? CurrentSessionChanged;

    event Action? SessionsChanged;

    IMediaSessionSourceSession? GetCurrentSession();

    IReadOnlyList<IMediaSessionSourceSession> GetSessions();
}

internal interface IMediaSessionSourceSession
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

    Task<bool> TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode mode);

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
    public GlobalSystemMediaTransportControlsSessionPlaybackStatus? PlaybackStatus { get; set; }

    public bool IsShuffleActive { get; set; }

    public MediaPlaybackAutoRepeatMode? AutoRepeatMode { get; set; }

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

/// <summary>Real <see cref="IMediaSessionSource"/> adapter over the WinRT SMTC APIs.</summary>
internal sealed class WinRtMediaSessionSource : IMediaSessionSource
{
    public async Task<IMediaSessionSourceManager?> GetManagerAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return manager is null ? null : new WinRtManagerAdapter(manager);
    }

    private sealed class WinRtManagerAdapter : IMediaSessionSourceManager
    {
        private readonly GlobalSystemMediaTransportControlsSessionManager _manager;

        public event Action? CurrentSessionChanged;

        public event Action? SessionsChanged;

        public WinRtManagerAdapter(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            _manager = manager;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            manager.SessionsChanged += OnSessionsChanged;
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
            => CurrentSessionChanged?.Invoke();

        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
            => SessionsChanged?.Invoke();

        public IMediaSessionSourceSession? GetCurrentSession()
        {
            var session = _manager.GetCurrentSession();
            return session is null ? null : new WinRtSessionAdapter(session);
        }

        public IReadOnlyList<IMediaSessionSourceSession> GetSessions()
        {
            var sessions = _manager.GetSessions();
            List<IMediaSessionSourceSession> adapters = new(sessions.Count);
            foreach (var session in sessions)
                adapters.Add(new WinRtSessionAdapter(session));
            return adapters;
        }
    }

    private sealed class WinRtSessionAdapter : IMediaSessionSourceSession
    {
        private readonly GlobalSystemMediaTransportControlsSession _session;

        public event Action? MediaPropertiesChanged;

        public event Action? PlaybackInfoChanged;

        public event Action? TimelinePropertiesChanged;

        public WinRtSessionAdapter(GlobalSystemMediaTransportControlsSession session)
        {
            _session = session;
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
            => MediaPropertiesChanged?.Invoke();

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args)
            => PlaybackInfoChanged?.Invoke();

        private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
            => TimelinePropertiesChanged?.Invoke();

        public object Identity => _session;

        public string SourceAppUserModelId => _session.SourceAppUserModelId ?? "";

        public async Task<MediaPropertiesData?> TryGetMediaPropertiesAsync()
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            if (props is null) return null;
            return new MediaPropertiesData
            {
                Title = props.Title,
                Artist = props.Artist,
                AlbumTitle = props.AlbumTitle,
                AlbumArtist = props.AlbumArtist,
                TrackNumber = props.TrackNumber,
                AlbumTrackCount = props.AlbumTrackCount,
                Genres = props.Genres,
                Thumbnail = props.Thumbnail
            };
        }

        public PlaybackInfoData? GetPlaybackInfo()
        {
            var info = _session.GetPlaybackInfo();
            if (info is null) return null;
            return new PlaybackInfoData
            {
                PlaybackStatus = info.PlaybackStatus,
                IsShuffleActive = info.IsShuffleActive ?? false,
                AutoRepeatMode = info.AutoRepeatMode,
                PlaybackRate = info.PlaybackRate,
                Controls = new MediaControlsData
                {
                    IsPlayEnabled = info.Controls.IsPlayEnabled,
                    IsPauseEnabled = info.Controls.IsPauseEnabled,
                    IsStopEnabled = info.Controls.IsStopEnabled,
                    IsNextEnabled = info.Controls.IsNextEnabled,
                    IsPreviousEnabled = info.Controls.IsPreviousEnabled,
                    IsPlaybackPositionEnabled = info.Controls.IsPlaybackPositionEnabled,
                    IsShuffleEnabled = info.Controls.IsShuffleEnabled,
                    IsRepeatEnabled = info.Controls.IsRepeatEnabled
                }
            };
        }

        public TimelinePropertiesData? GetTimelineProperties()
        {
            var timeline = _session.GetTimelineProperties();
            if (timeline is null) return null;
            return new TimelinePropertiesData
            {
                Position = timeline.Position,
                EndTime = timeline.EndTime,
                LastUpdatedTime = timeline.LastUpdatedTime
            };
        }

        public Task<bool> TryPlayAsync() => TryControlAsync(() => _session.TryPlayAsync());

        public Task<bool> TryPauseAsync() => TryControlAsync(() => _session.TryPauseAsync());

        public Task<bool> TrySkipNextAsync() => TryControlAsync(() => _session.TrySkipNextAsync());

        public Task<bool> TrySkipPreviousAsync() => TryControlAsync(() => _session.TrySkipPreviousAsync());

        public Task<bool> TryChangeShuffleActiveAsync(bool shuffle) => TryControlAsync(() => _session.TryChangeShuffleActiveAsync(shuffle));

        public Task<bool> TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode mode) => TryControlAsync(() => _session.TryChangeAutoRepeatModeAsync(mode));

        public Task<bool> TryChangePlaybackPositionAsync(long positionTicks) => TryControlAsync(() => _session.TryChangePlaybackPositionAsync(positionTicks));

        private static async Task<bool> TryControlAsync(Func<IAsyncOperation<bool>> operation)
        {
            try
            {
                return await operation();
            }
            catch
            {
                return false;
            }
        }
    }
}
