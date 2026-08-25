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
            var manager = await _source.GetManagerAsync().ConfigureAwait(false);
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

    public void SetRepeat(MediaRepeatMode mode) => _ = _session?.TryChangeAutoRepeatModeAsync(mode);

    public void Seek(TimeSpan position) => _ = _session?.TryChangePlaybackPositionAsync(position.Ticks);

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
            var props = await session.TryGetMediaPropertiesAsync().ConfigureAwait(false);
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
        var (title, artist, album, albumArtist, trackNumber, albumTrackCount, genres) = ExtractMeta(props);
        var (canPlay, canPause, canStop, canNext, canPrev, canSeek, canShuffle, canRepeat) = ExtractControls(info);

        return new MediaSnapshot
        {
            SourceAppId = session.SourceAppUserModelId ?? "",
            Title = title,
            Artist = artist,
            Album = album,
            AlbumArtist = albumArtist,
            TrackNumber = trackNumber,
            AlbumTrackCount = albumTrackCount,
            Genres = genres,
            Status = info?.PlaybackStatus ?? MediaPlaybackStatus.Closed,
            Position = timeline?.Position ?? TimeSpan.Zero,
            Duration = timeline?.EndTime ?? TimeSpan.Zero,
            LastUpdated = timeline?.LastUpdatedTime ?? Clock.GetUtcNow(),
            Shuffle = info?.IsShuffleActive ?? false,
            Repeat = info?.AutoRepeatMode ?? MediaRepeatMode.None,
            PlaybackRate = info?.PlaybackRate is > 0 ? info.PlaybackRate.Value : 1.0,
            CanPlay = canPlay,
            CanPause = canPause,
            CanStop = canStop,
            CanNext = canNext,
            CanPrev = canPrev,
            CanSeek = canSeek,
            CanShuffle = canShuffle,
            CanRepeat = canRepeat
        };
    }

    /// <summary>The metadata group of the snapshot, sanitized and defaulted in
    /// one place.</summary>
    private static (string Title, string Artist, string Album, string AlbumArtist, int TrackNumber, int AlbumTrackCount, string[] Genres) ExtractMeta(MediaPropertiesData? props)
        => (
            Sanitize(props?.Title, ""),
            Sanitize(props?.Artist, ""),
            Sanitize(props?.AlbumTitle, ""),
            Sanitize(props?.AlbumArtist, ""),
            props?.TrackNumber ?? 0,
            props?.AlbumTrackCount ?? 0,
            props?.Genres?.ToArray() ?? []);

    /// <summary>The transport-control capability group, defaulted to disabled
    /// when the session reports no controls.</summary>
    private static (bool Play, bool Pause, bool Stop, bool Next, bool Prev, bool Seek, bool Shuffle, bool Repeat) ExtractControls(PlaybackInfoData? info)
        => (
            info?.Controls.IsPlayEnabled ?? false,
            info?.Controls.IsPauseEnabled ?? false,
            info?.Controls.IsStopEnabled ?? false,
            info?.Controls.IsNextEnabled ?? false,
            info?.Controls.IsPreviousEnabled ?? false,
            info?.Controls.IsPlaybackPositionEnabled ?? false,
            info?.Controls.IsShuffleEnabled ?? false,
            info?.Controls.IsRepeatEnabled ?? false);

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

    public MediaPlaybackStatus Status { get; set; }

    public TimeSpan Position { get; set; }

    public TimeSpan Duration { get; set; }

    public DateTimeOffset LastUpdated { get; set; }

    public bool Shuffle { get; set; }

    public MediaRepeatMode Repeat { get; set; }

    public double PlaybackRate { get; set; } = 1.0;

    public bool CanPlay { get; set; }

    public bool CanPause { get; set; }

    public bool CanStop { get; set; }

    public bool CanNext { get; set; }

    public bool CanPrev { get; set; }

    public bool CanSeek { get; set; }

    public bool CanShuffle { get; set; }

    public bool CanRepeat { get; set; }

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
