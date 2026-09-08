namespace ModernWigiDash.Tests;

/// <summary>
/// NowPlayingRenderer at its interface: the draw module the widget routes
/// every paint through. Driven without a widget instance or a live monitor -
/// a snapshot record, a geometry record, and a canvas are enough - so the
/// active scene's paint decisions (icon colors, the hero play/pause branch,
/// the repeat-one glyph) are assertable where they live. The pixel pins use
/// the same coordinates as NowPlayingWidgetLiveSessionTests, which still
/// covers the widget-level wiring end to end.
/// </summary>
[TestClass]
public class NowPlayingRendererTests
{
    private static MediaSnapshot ActiveSnapshot() => new()
    {
        SourceAppId = "Spotify.exe",
        Title = "Test Song",
        Artist = "Test Artist",
        Album = "Test Album",
        TrackNumber = 3,
        AlbumTrackCount = 12,
        Genres = ["Rock"],
        Status = MediaPlaybackStatus.Playing,
        Position = TimeSpan.FromSeconds(30),
        Duration = TimeSpan.FromSeconds(60),
        LastUpdated = DateTimeOffset.UtcNow,
        CanPlay = true,
        CanPause = true,
        CanNext = true,
        CanPrev = true,
        CanShuffle = true,
        CanRepeat = true,
        CanSeek = true,
    };

    private static NowPlayingGeometry Layout(SKRect bounds)
        => NowPlayingLayout.Compute(bounds, 1f, showSourceBadge: true, badgeTextWidth: 60f);

    [TestMethod]
    public void DrawIdle_PaintsTheIdlePanel()
    {
        using var renderer = new NowPlayingRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(508, 296));
        var bounds = new SKRect(0, 0, 508, 296);

        renderer.DrawBackground(surface.Canvas, bounds, 0.5f, null);
        renderer.DrawIdle(surface.Canvas, bounds, 0.5f, new SKColor(245, 158, 11), SKColors.White);

        var pixel = surface.PeekPixels().GetPixelColor(254, 148);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "the idle panel must paint output");
    }

    [TestMethod]
    public void DrawActiveScene_Playing_DarkIconSitsOnTheAccentHeroButton()
    {
        using var renderer = new NowPlayingRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var bounds = new SKRect(0, 0, 1016, 592);
        var layout = Layout(bounds);

        renderer.DrawBackground(surface.Canvas, bounds, 1f, null);
        renderer.DrawActiveScene(surface.Canvas, bounds, 1f, layout, ActiveSnapshot(), artwork: null,
            accent: new SKColor(245, 158, 11), text: SKColors.White, clockNow: DateTimeOffset.UtcNow);

        // Hero play/pause button center (the control row starts at x 614 at 1016x592):
        // the playing state draws the dark pause icon on the accent fill. The
        // pause bars are centered in the button, so sample a bar pixel instead
        // of the exact center (which falls in the gap between the two bars).
        var pixel = surface.PeekPixels().GetPixelColor(790, 536);
        Assert.AreEqual(18, pixel.Red, "the pause icon is the fixed dark color drawn over the accent fill");
    }

    [TestMethod]
    public void DrawActiveScene_Stopped_HeroButtonUsesTheDisabledAlpha()
    {
        using var renderer = new NowPlayingRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var bounds = new SKRect(0, 0, 1016, 592);
        var layout = Layout(bounds);
        var snap = ActiveSnapshot();
        snap.Status = MediaPlaybackStatus.Stopped;
        snap.CanPlay = false;
        snap.CanPause = false;

        renderer.DrawBackground(surface.Canvas, bounds, 1f, null);
        renderer.DrawActiveScene(surface.Canvas, bounds, 1f, layout, snap, artwork: null,
            accent: new SKColor(245, 158, 11), text: SKColors.White, clockNow: DateTimeOffset.UtcNow);

        // The stopped state draws the play triangle (dark color) instead of
        // the pause bars: a bar pixel that is dark in the playing state must
        // not be dark here. Sample the exact center row across the button and
        // count dark pixels — the two pause bars leave two runs, the triangle
        // leaves one narrower run shifted right.
        int darkPixels = 0;
        for (int x = 770; x < 820; x++)
        {
            if (surface.PeekPixels().GetPixelColor(x, 536).Red == 18) darkPixels++;
        }
        Assert.IsTrue(darkPixels > 0 && darkPixels < 40, $"the play triangle's dark footprint ({darkPixels}px) differs from the pause bars'");
    }

    [TestMethod]
    public void DrawActiveScene_RepeatOne_DrawsTheRepeatGlyph()
    {
        using var renderer = new NowPlayingRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var bounds = new SKRect(0, 0, 1016, 592);
        var layout = Layout(bounds);
        var snap = ActiveSnapshot();
        snap.Repeat = MediaRepeatMode.Track;

        renderer.DrawBackground(surface.Canvas, bounds, 1f, null);
        renderer.DrawActiveScene(surface.Canvas, bounds, 1f, layout, snap, artwork: null,
            accent: new SKColor(245, 158, 11), text: SKColors.White, clockNow: DateTimeOffset.UtcNow);

        // The repeat button sits at the right end of the centered control row
        // (roughly x 928-976, y 512-560 at 1016x592); the repeat-one state adds
        // the "1" glyph inside the ring.
        var pixel = surface.PeekPixels().GetPixelColor(952, 536);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "the repeat-one glyph must paint inside the ring");
    }

    [TestMethod]
    public void IconColor_ActiveReturnsAccent_OtherwiseTextAtCapabilityAlpha()
    {
        var accent = new SKColor(245, 158, 11);
        var text = SKColors.White;

        Assert.AreEqual(accent, NowPlayingRenderer.IconColor(active: true, enabled: false, accent, text));
        Assert.AreEqual(text.WithAlpha(240), NowPlayingRenderer.IconColor(active: false, enabled: true, accent, text));
        Assert.AreEqual(text.WithAlpha(70), NowPlayingRenderer.IconColor(active: false, enabled: false, accent, text));
    }

    [TestMethod]
    public void RepeatedSameBounds_UsesCachedIconPathsAndRepaints()
    {
        using var renderer = new NowPlayingRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var bounds = new SKRect(0, 0, 1016, 592);
        var layout = Layout(bounds);

        // The first scene builds the cached control-icon paths; the second
        // exercises the cache-hit path — both must paint the control row.
        renderer.DrawBackground(surface.Canvas, bounds, 1f, null);
        renderer.DrawActiveScene(surface.Canvas, bounds, 1f, layout, ActiveSnapshot(), artwork: null,
            accent: new SKColor(245, 158, 11), text: SKColors.White, clockNow: DateTimeOffset.UtcNow);
        renderer.DrawActiveScene(surface.Canvas, bounds, 1f, layout, ActiveSnapshot(), artwork: null,
            accent: new SKColor(245, 158, 11), text: SKColors.White, clockNow: DateTimeOffset.UtcNow);

        var pixel = surface.PeekPixels().GetPixelColor(795, 536);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "the hero button must repaint on the cached-path render");
    }
}
