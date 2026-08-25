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
            PlaybackStatus = MediaPlaybackStatus.Playing,
            AutoRepeatMode = MediaRepeatMode.None,
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
    public void Render_LiveSession_RepeatedSameBounds_UsesCachedIconPaths()
    {
        var (widget, _) = CreateLiveWidget();
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var bounds = new SKRect(0, 0, 1016, 592);

        // The first render builds the cached control-icon paths; the second
        // exercises the cache-hit path — both must paint the control row.
        widget.Render(surface.Canvas, bounds);
        widget.Render(surface.Canvas, bounds);

        // Hero play/pause button center (the control row starts at x 614 at 1016x592).
        var pixel = surface.PeekPixels().GetPixelColor(795, 536);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "the hero button must repaint on the cached-path render");
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
        Assert.AreEqual(MediaRepeatMode.List, session.LastRepeat,
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

    [TestMethod]
    public void OnTouch_TapOnShuffleButton_WhenSessionReportsNotShuffleable_SendsNoCommand()
    {
        var (widget, session) = CreateLiveWidget();
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var bounds = new SKRect(0, 0, 1016, 592);
        widget.Render(surface.Canvas, bounds);
        Assert.IsFalse(session.PlaybackInfo!.Controls.IsShuffleEnabled, "the fixture session reports not-shuffleable");

        // The shuffle button is the leftmost control: at 1016x592 the row
        // starts at x 616 and the shuffle button spans x 616-664, y 512-560.
        var tap = new SKPoint(640f, 536f);
        widget.OnTouch(tap, TouchEventType.TouchDown);
        widget.OnTouch(tap, TouchEventType.TouchUp);

        Assert.AreEqual(0, session.ShuffleCalls, "the monitor's policy vetoes the tap; no widget-side command fires");
    }
}
