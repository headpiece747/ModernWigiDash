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
    private static (NowPlayingWidget Widget, StubMediaSession Session) CreateLiveWidget()
    {
        var source = new StubMediaSessionSource();
        var session = source.Manager!.Current = new StubMediaSession
        {
            SourceAppUserModelId = "Spotify.exe",
        };
        source.Manager.Sessions.Add(session);
        session.Properties = new MediaPropertiesData
        {
            Title = "Test Song",
            Artist = "Test Artist",
            AlbumTitle = "Test Album",
            TrackNumber = 3,
            AlbumTrackCount = 12,
            Genres = ["Rock"],
        };
        session.PlaybackInfo = new PlaybackInfoData
        {
            PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            AutoRepeatMode = MediaPlaybackAutoRepeatMode.None,
            PlaybackRate = 1.0,
            Controls = new MediaControlsData { IsPlayEnabled = true, IsPauseEnabled = true, IsRepeatEnabled = true, IsPlaybackPositionEnabled = true },
        };
        session.Timeline = new TimelinePropertiesData
        {
            Position = TimeSpan.FromSeconds(30),
            EndTime = TimeSpan.FromSeconds(60),
            LastUpdatedTime = DateTimeOffset.UtcNow,
        };

        var monitor = new MediaSessionMonitor(source, (_, _) => { });
        monitor.InitializeAsync().GetAwaiter().GetResult();
        var widget = new NowPlayingWidget(() => monitor);
        widget.InitializeAsync(new TestContext()).GetAwaiter().GetResult();
        return (widget, session);
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
