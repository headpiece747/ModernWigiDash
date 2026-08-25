using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The one file that knows the WinRT SMTC surface. Projects the
/// GlobalSystemMediaTransportControlsSessionManager/Session types into the
/// neutral <see cref="IMediaSessionSource"/> seam and maps the SMTC enums to
/// the neutral vocabulary exactly once, here:
/// <see cref="ToNeutralStatus"/> / <see cref="ToNeutralRepeat"/> on the way
/// in, <see cref="ToWinRtRepeat"/> on the way out. The mapping is by NAME,
/// not by ordinal: the neutral members mirror the SMTC member names, and
/// anything the OS reports outside the named set lands on the neutral
/// <c>Unknown</c> (and the repeat command's <c>Unknown</c> arm, which the
/// monitor's tap policy never produces, maps back to <c>None</c>). The old
/// cast-based mapping assumed ordinal agreement; the status enum does not
/// share its ordinals with the neutral one (the repeat enum happens to), so
/// the name-based mapping is pinned against the real enums by
/// <c>WinRtMediaSessionSourceTests</c>. The thumbnail stream reference stays
/// WinRT by design: it is
/// the artwork pipeline's blob, a separate consumer of the seam.
/// </summary>
internal sealed class WinRtMediaSessionSource : IMediaSessionSource
{
    public async Task<IMediaSessionSourceManager?> GetManagerAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return manager is null ? null : new WinRtManagerAdapter(manager);
    }

    /// <summary>The WinRT to neutral status mapping, the one place it is
    /// spelled, by name: each named SMTC value projects to the same-named
    /// neutral member; anything outside the named set is
    /// <see cref="MediaPlaybackStatus.Unknown"/>.</summary>
    internal static MediaPlaybackStatus ToNeutralStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        => status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackStatus.Closed,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackStatus.Opened,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackStatus.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
            _ => MediaPlaybackStatus.Unknown
        };

    /// <summary>The WinRT to neutral repeat mapping, the one place it is
    /// spelled, by name: each named SMTC value projects to the same-named
    /// neutral member; anything outside the named set is
    /// <see cref="MediaRepeatMode.Unknown"/>.</summary>
    internal static MediaRepeatMode ToNeutralRepeat(MediaPlaybackAutoRepeatMode mode)
        => mode switch
        {
            MediaPlaybackAutoRepeatMode.None => MediaRepeatMode.None,
            MediaPlaybackAutoRepeatMode.Track => MediaRepeatMode.Track,
            MediaPlaybackAutoRepeatMode.List => MediaRepeatMode.List,
            _ => MediaRepeatMode.Unknown
        };

    /// <summary>The neutral to WinRT repeat mapping for the command path.
    /// The monitor's tap policy only ever produces the three named modes;
    /// the <c>Unknown</c> arm is total-mapping insurance and degrades to
    /// <c>None</c>.</summary>
    internal static MediaPlaybackAutoRepeatMode ToWinRtRepeat(MediaRepeatMode mode)
        => mode switch
        {
            MediaRepeatMode.None => MediaPlaybackAutoRepeatMode.None,
            MediaRepeatMode.Track => MediaPlaybackAutoRepeatMode.Track,
            MediaRepeatMode.List => MediaPlaybackAutoRepeatMode.List,
            _ => MediaPlaybackAutoRepeatMode.None
        };

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

        /// <summary>Releases the WinRT event subscriptions taken in the
        /// constructor. The monitor disposes the adapter it holds so the
        /// WinRT manager does not keep its handlers (and the adapter) alive
        /// past the monitor's lifetime.</summary>
        public void Dispose()
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, object _)
            => CurrentSessionChanged?.Invoke();

        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, object _)
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

        /// <summary>Releases the WinRT event subscriptions taken in the
        /// constructor. The monitor disposes the adapter it holds (and every
        /// fresh wrapper it discards) so the WinRT session does not
        /// accumulate dead handlers.</summary>
        public void Dispose()
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object _)
            => MediaPropertiesChanged?.Invoke();

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object _)
            => PlaybackInfoChanged?.Invoke();

        private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object _)
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
                PlaybackStatus = ToNeutralStatus(info.PlaybackStatus),
                IsShuffleActive = info.IsShuffleActive ?? false,
                AutoRepeatMode = info.AutoRepeatMode is { } repeat ? ToNeutralRepeat(repeat) : null,
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

        public Task<bool> TryChangeAutoRepeatModeAsync(MediaRepeatMode mode) => TryControlAsync(() => _session.TryChangeAutoRepeatModeAsync(ToWinRtRepeat(mode)));

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
