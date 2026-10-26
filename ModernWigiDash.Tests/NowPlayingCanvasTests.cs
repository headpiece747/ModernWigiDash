namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets;

/// <summary>
/// The NowPlaying canvas's geometry gate + the renderer's media-text memos:
/// the frame geometry is a pure function of (bounds, badge visibility, badge
/// width) and the media strings change per track, not per tick, so a
/// steady-state tick on the same inputs must not recompute the layout or
/// reformat the strings. The Skia interop draw path has a fixed per-tick
/// floor, so the pin is RELATIVE (steady ticks allocate less than changing
/// ticks) plus a storm-catcher bound. A render-smoke test pins the draw
/// owner's surface for C1.
/// </summary>
[TestClass]
public class NowPlayingCanvasTests
{
    private const long SteadyTickStormBudgetBytes = 8192;

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
        LastUpdated = DateTimeOffset.UnixEpoch,
        CanPlay = true,
        CanPause = true,
        CanSeek = true,
    };

    private static NowPlayingCanvas CreateCanvas()
    {
        var canvas = new NowPlayingCanvas();
        canvas.Initialize(null);
        return canvas;
    }

    [TestMethod]
    public void Render_Smoke_PaintsTheActiveScene()
    {
        using var canvas = CreateCanvas();
        using var bitmap = new SKBitmap(508, 296);
        using var skCanvas = new SKCanvas(bitmap);
        var bounds = new SKRect(0, 0, 508, 296);

        canvas.Render(skCanvas, bounds, ActiveSnapshot(), "#F59E0B", "#FFFFFF", true, DateTimeOffset.UnixEpoch);

        int painted = 0;
        for (int x = 40; x < 460; x += 8)
        {
            for (int y = 40; y < 256; y += 8)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0) painted++;
            }
        }
        Assert.IsTrue(painted > 0, "the Now Playing active scene must paint onto the canvas");
    }

    [TestMethod]
    public void Render_SameInputs_TheGeometryGateServesTheSameLayout()
    {
        using var canvas = CreateCanvas();
        using var bitmap = new SKBitmap(508, 296);
        using var skCanvas = new SKCanvas(bitmap);
        var bounds = new SKRect(0, 0, 508, 296);
        var snap = ActiveSnapshot();

        canvas.Render(skCanvas, bounds, snap, "#F59E0B", "#FFFFFF", true, DateTimeOffset.UnixEpoch);
        var first = canvas.LastLayout;
        int computationsAfterFirst = canvas.LayoutComputationsForTest;

        canvas.Render(skCanvas, bounds, snap, "#F59E0B", "#FFFFFF", true, DateTimeOffset.UnixEpoch);

        Assert.IsTrue(canvas.LastLayout == first,
            "a steady-state tick (same bounds/badge inputs) must serve the gated layout, not recompute it");
        Assert.AreEqual(computationsAfterFirst, canvas.LayoutComputationsForTest,
            "a steady-state tick (same bounds/badge inputs) must not recompute the layout: the gate serves the single slot");
    }

    [TestMethod]
    public void Render_SteadyState_AllocatesLessThanChangingInputs()
    {
        using var canvas = CreateCanvas();
        using var bitmap = new SKBitmap(508, 296);
        using var skCanvas = new SKCanvas(bitmap);
        var bounds = new SKRect(0, 0, 508, 296);
        var snap = ActiveSnapshot();

        // Warm: the font caches + gate fill on the first render.
        canvas.Render(skCanvas, bounds, snap, "#F59E0B", "#FFFFFF", true, DateTimeOffset.UnixEpoch);

        // Steady: the same inputs every tick (the gate + memos serve).
        long steadyBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++)
        {
            canvas.Render(skCanvas, bounds, snap, "#F59E0B", "#FFFFFF", true, DateTimeOffset.UnixEpoch);
        }
        long steadyDelta = GC.GetAllocatedBytesForCurrentThread() - steadyBefore;

        // Changing: a different placement every tick (the gate recomputes).
        long changeBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++)
        {
            var moved = new SKRect(0, 0, 508 + i * 4, 296 + i * 4);
            canvas.Render(skCanvas, moved, snap, "#F59E0B", "#FFFFFF", true, DateTimeOffset.UnixEpoch);
        }
        long changeDelta = GC.GetAllocatedBytesForCurrentThread() - changeBefore;

        Assert.IsTrue(steadyDelta < changeDelta,
            $"steady-state ticks ({steadyDelta} bytes over 5) must allocate less than changing-input ticks ({changeDelta} bytes over 5): the geometry gate and media-text memos serve steady inputs");
    }

    [TestMethod]
    public void Render_SteadyState_AllocatesUnderTheStormBudget()
    {
        using var canvas = CreateCanvas();
        using var bitmap = new SKBitmap(508, 296);
        using var skCanvas = new SKCanvas(bitmap);
        var bounds = new SKRect(0, 0, 508, 296);
        var snap = ActiveSnapshot();

        canvas.Render(skCanvas, bounds, snap, "#F59E0B", "#FFFFFF", true, DateTimeOffset.UnixEpoch);

        long before = GC.GetAllocatedBytesForCurrentThread();
        canvas.Render(skCanvas, bounds, snap, "#F59E0B", "#FFFFFF", true, DateTimeOffset.UnixEpoch);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(delta <= SteadyTickStormBudgetBytes,
            $"a steady-state Now Playing tick allocated {delta} bytes (budget {SteadyTickStormBudgetBytes}): a per-tick string/collection storm has crept into the Now Playing path");
    }
}
