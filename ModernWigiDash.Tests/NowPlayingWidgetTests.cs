using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// NowPlayingWidget at the widget level. The widget creates its own
/// MediaSessionMonitor/ArtworkLoader in InitializeAsync (no injection seam),
/// so the reachable surface is the no-session path: the idle render and the
/// touch no-ops. The pure helpers (FormatTime/FriendlyAppName) are private
/// static, and the hit-rect/repeat-cycle logic requires a live monitor —
/// those are covered by MediaSessionMonitorTests at the monitor level.
/// </summary>
[TestClass]
public class NowPlayingWidgetTests
{
    private static SKSurface CreateSurface() => SKSurface.Create(new SKImageInfo(508, 296));

    [TestMethod]
    public void Render_WithoutMediaSession_DrawsIdleState()
    {
        var widget = new NowPlayingWidget();

        using var surface = CreateSurface();
        widget.Render(surface.Canvas, new SKRect(0, 0, 508, 296));

        var pixel = surface.PeekPixels().GetPixelColor(254, 148);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "The idle state must paint output");
    }

    [TestMethod]
    public void Render_SmallBounds_WithoutMediaSession_DoesNotThrow()
    {
        var widget = new NowPlayingWidget();

        using var surface = SKSurface.Create(new SKImageInfo(200, 150));
        widget.Render(surface.Canvas, new SKRect(0, 0, 200, 150));

        var pixel = surface.PeekPixels().GetPixelColor(100, 75);
        Assert.AreNotEqual(SKColors.Transparent, pixel);
    }

    [TestMethod]
    public void OnTouch_WithoutMediaSession_IsNoOp()
    {
        var widget = new NowPlayingWidget();

        // Down/Up with no session snapshot must be a safe no-op (no monitor,
        // no hit rects populated yet).
        widget.OnTouch(new SKPoint(100, 100), TouchEventType.TouchDown);
        widget.OnTouch(new SKPoint(100, 100), TouchEventType.TouchUp);

        Assert.IsNotNull(widget, "Touch without a media session must not throw");
    }

    [TestMethod]
    public void Dispose_WithoutInitialize_IsSafe()
    {
        var widget = new NowPlayingWidget();

        widget.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Assert.IsNotNull(widget, "Disposing an uninitialized widget must complete without throwing");
    }
}
