using Windows.Media;
using Windows.Media.Control;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// Live-session behavior through the widget's monitor-factory seam: a real
/// monitor over a fake SMTC source drives the render path and the touch
/// handlers, which the idle-only smoke tests could never reach.
/// </summary>
[TestClass]
public class NowPlayingWidgetLiveSessionTests
{
    private sealed class FakeSource : IMediaSessionSource
    {
        public FakeManager Manager { get; } = new();
        public Task<IMediaSessionSourceManager?> GetManagerAsync() => Task.FromResult<IMediaSessionSourceManager?>(Manager);
    }

    private sealed class FakeManager : IMediaSessionSourceManager
    {
        private Action? _currentChanged;
        private Action? _sessionsChanged;
        public FakeSession Session { get; } = new();
        public event Action? CurrentSessionChanged { add => _currentChanged += value; remove => _currentChanged -= value; }
        public event Action? SessionsChanged { add => _sessionsChanged += value; remove => _sessionsChanged -= value; }
        public IMediaSessionSourceSession? GetCurrentSession() => Session;
        public IReadOnlyList<IMediaSessionSourceSession> GetSessions() => [Session];
    }

    private sealed class FakeSession : IMediaSessionSourceSession
    {
        private Action? _mediaPropsChanged;
        private Action? _playbackChanged;
        private Action? _timelineChanged;
        public object Identity => this;
        public string SourceAppUserModelId { get; set; } = "Spotify.exe";
        public MediaPropertiesData? Properties { get; set; }
        public PlaybackInfoData? PlaybackInfo { get; set; }
        public TimelinePropertiesData? Timeline { get; set; }
        public int RepeatCalls { get; private set; }
        public MediaPlaybackAutoRepeatMode LastRepeat { get; private set; }
        public int SeekCalls { get; private set; }
        public long LastSeekTicks { get; private set; }

        public event Action? MediaPropertiesChanged { add => _mediaPropsChanged += value; remove => _mediaPropsChanged -= value; }
        public event Action? PlaybackInfoChanged { add => _playbackChanged += value; remove => _playbackChanged -= value; }
        public event Action? TimelinePropertiesChanged { add => _timelineChanged += value; remove => _timelineChanged -= value; }

        public Task<MediaPropertiesData?> TryGetMediaPropertiesAsync() => Task.FromResult(Properties);
        public PlaybackInfoData? GetPlaybackInfo() => PlaybackInfo;
        public TimelinePropertiesData? GetTimelineProperties() => Timeline;
        public Task<bool> TryPlayAsync() => Task.FromResult(true);
        public Task<bool> TryPauseAsync() => Task.FromResult(true);
        public Task<bool> TrySkipNextAsync() => Task.FromResult(true);
        public Task<bool> TrySkipPreviousAsync() => Task.FromResult(true);
        public Task<bool> TryChangeShuffleActiveAsync(bool shuffle) => Task.FromResult(true);
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
    }

    private static (NowPlayingWidget Widget, FakeSession Session) CreateLiveWidget()
    {
        var source = new FakeSource();
        source.Manager.Session.Properties = new MediaPropertiesData
        {
            Title = "Test Song",
            Artist = "Test Artist",
            AlbumTitle = "Test Album",
            TrackNumber = 3,
            AlbumTrackCount = 12,
            Genres = ["Rock"],
        };
        source.Manager.Session.PlaybackInfo = new PlaybackInfoData
        {
            PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            AutoRepeatMode = MediaPlaybackAutoRepeatMode.None,
            PlaybackRate = 1.0,
            Controls = new MediaControlsData { IsPlayEnabled = true, IsPauseEnabled = true, IsRepeatEnabled = true, IsPlaybackPositionEnabled = true },
        };
        source.Manager.Session.Timeline = new TimelinePropertiesData
        {
            Position = TimeSpan.FromSeconds(30),
            EndTime = TimeSpan.FromSeconds(60),
            LastUpdatedTime = DateTimeOffset.UtcNow,
        };

        var monitor = new MediaSessionMonitor(source, (_, _) => { });
        monitor.InitializeAsync().GetAwaiter().GetResult();
        var widget = new NowPlayingWidget(() => monitor);
        widget.InitializeAsync(new TestContext()).GetAwaiter().GetResult();
        return (widget, source.Manager.Session);
    }

    [TestMethod]
    public void Render_LiveSession_DrawsActiveLayoutWithoutThrowing()
    {
        var (widget, _) = CreateLiveWidget();
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));

        widget.Render(surface.Canvas, new SKRect(0, 0, 1016, 592));

        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void OnTouch_TapOnRepeatButton_CyclesRepeatModeThroughMonitor()
    {
        var (widget, session) = CreateLiveWidget();
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var bounds = new SKRect(0, 0, 1016, 592);
        widget.Render(surface.Canvas, bounds);

        // The repeat button sits at the right end of the centered control row:
        // the row starts at art-right (~614) and the repeat button spans the
        // last 48px, roughly x 928-976, y 512-560 at 1016x592.
        var tap = new SKPoint(952f, 536f);
        widget.OnTouch(tap, TouchEventType.TouchDown);
        widget.OnTouch(tap, TouchEventType.TouchUp);

        Assert.AreEqual(1, session.RepeatCalls);
        Assert.AreEqual(MediaPlaybackAutoRepeatMode.List, session.LastRepeat,
            "repeat cycles None -> List on tap");
    }

    [TestMethod]
    public void OnTouch_TapOnProgressBar_SeeksToTappedPosition()
    {
        var (widget, session) = CreateLiveWidget();
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var bounds = new SKRect(0, 0, 1016, 592);
        widget.Render(surface.Canvas, bounds);

        // The bar spans x 598-992 at y 476 (right edge of the art column to
        // the right pad); tapping x 792 lands at ratio (792-598)/394 = 0.492,
        // so the seek targets ~29.5s of the 60s track.
        var tap = new SKPoint(792f, 476f);
        widget.OnTouch(tap, TouchEventType.TouchDown);
        widget.OnTouch(tap, TouchEventType.TouchUp);

        Assert.AreEqual(1, session.SeekCalls);
        Assert.AreEqual((long)(0.492 * 60 * TimeSpan.TicksPerSecond), session.LastSeekTicks, 500_000L);
    }
}
