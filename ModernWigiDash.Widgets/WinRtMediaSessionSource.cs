using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The one file that knows the WinRT SMTC surface. Projects the
/// GlobalSystemMediaTransportControlsSessionManager/Session types into the
/// neutral <see cref="IMediaSessionSource"/> seam and maps the SMTC enums to
/// the neutral vocabulary exactly once, here:
/// <see cref="MediaPlaybackStatus"/> / <see cref="MediaRepeatMode"/> on the
/// way in, <see cref="ToWinRtRepeat"/> on the way out. The named SMTC values
/// share their ordinals with the neutral enums, so each mapping is a range
/// check plus a cast; anything the OS reports outside the named set lands on
/// the neutral <c>Unknown</c> (and the repeat command's <c>Unknown</c> arm,
/// which the monitor's tap policy never produces, maps back to
/// <c>None</c>). The thumbnail stream reference stays WinRT by design: it is
/// the artwork pipeline's blob, a separate consumer of the seam.
/// </summary>
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

        /// <summary>The WinRT → neutral status mapping, the one place it is
        /// spelled: the six named SMTC values share their ordinals with the
        /// neutral enum; everything else is <see cref="MediaPlaybackStatus.Unknown"/>.</summary>
        private static MediaPlaybackStatus ToNeutralStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
            => (int)status is >= 0 and <= 5 ? (MediaPlaybackStatus)(int)status : MediaPlaybackStatus.Unknown;

        /// <summary>The WinRT → neutral repeat mapping, the one place it is
        /// spelled: the three named SMTC values share their ordinals with the
        /// neutral enum; everything else is <see cref="MediaRepeatMode.Unknown"/>.</summary>
        private static MediaRepeatMode ToNeutralRepeat(MediaPlaybackAutoRepeatMode mode)
            => (int)mode is >= 0 and <= 2 ? (MediaRepeatMode)(int)mode : MediaRepeatMode.Unknown;

        /// <summary>The neutral → WinRT repeat mapping for the command path.
        /// The monitor's tap policy only ever produces the three named modes;
        /// the <c>Unknown</c> arm is total-mapping insurance and degrades to
        /// <c>None</c>.</summary>
        private static MediaPlaybackAutoRepeatMode ToWinRtRepeat(MediaRepeatMode mode)
            => (int)mode is >= 0 and <= 2 ? (MediaPlaybackAutoRepeatMode)(int)mode : MediaPlaybackAutoRepeatMode.None;
    }
}
