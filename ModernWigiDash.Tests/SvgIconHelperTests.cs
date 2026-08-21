
namespace ModernWigiDash.Tests;

/// <summary>
/// The shared SVG-path helpers: the parse cache's empty-path fallback and
/// case-insensitive keying, and the draw-scaling protocol (centered, scaled
/// by the largest bounds dimension, offset-shifted) pinned on real pixels.
/// </summary>
[TestClass]
public class SvgIconHelperTests
{
    [TestMethod]
    public void GetOrParse_EmptyOrInvalidPathData_ReturnsEmptyPath()
    {
        Assert.IsNotNull(SvgIconHelper.SvgPathCache.GetOrParse("fallback-empty", ""));
        Assert.IsNotNull(SvgIconHelper.SvgPathCache.GetOrParse("fallback-null", () => null!));
        Assert.IsNotNull(SvgIconHelper.SvgPathCache.GetOrParse("fallback-garbage", "not valid svg path data"));

        Assert.IsTrue(SvgIconHelper.SvgPathCache.GetOrParse("fallback-empty", "").IsEmpty);
        Assert.IsTrue(SvgIconHelper.SvgPathCache.GetOrParse("fallback-null", () => null!).IsEmpty,
            "null path data must fall back to an empty path, never throw");
        Assert.IsTrue(SvgIconHelper.SvgPathCache.GetOrParse("fallback-garbage", "not valid svg path data").IsEmpty,
            "unparseable path data must fall back to an empty path, never throw");
        Assert.IsTrue(SvgIconHelper.SvgPathCache.GetOrParse("fallback-line", "M0,0L10,0").IsEmpty,
            "a zero-area path has no fillable bounds and must fall back to empty");
    }

    [TestMethod]
    public void GetOrParse_ValidPath_ParsedOnceAndCachedByKey()
    {
        int parseCalls = 0;
        var first = SvgIconHelper.SvgPathCache.GetOrParse("cache-valid-rect", () =>
        {
            parseCalls++;
            return "M0,0L10,0L10,10L0,10Z";
        });
        var second = SvgIconHelper.SvgPathCache.GetOrParse("cache-valid-rect", () =>
        {
            parseCalls++;
            return "M0,0L10,0L10,10L0,10Z";
        });

        Assert.AreEqual(SKPathFillType.Winding, first.FillType);
        Assert.AreSame(first, second, "the cache must hand back the parsed instance");
        Assert.AreEqual(1, parseCalls, "the path-data factory must run exactly once per key");
    }

    [TestMethod]
    public void GetOrParse_KeyLookup_IsCaseInsensitive()
    {
        int parseCalls = 0;
        var first = SvgIconHelper.SvgPathCache.GetOrParse("Icon.Case.Key", () =>
        {
            parseCalls++;
            return "M0,0L10,0L10,10L0,10Z";
        });
        var second = SvgIconHelper.SvgPathCache.GetOrParse("icon.case.key", () =>
        {
            parseCalls++;
            return "M0,0L10,0L10,10L0,10Z";
        });

        Assert.AreSame(first, second, "keys differing only in case must share one cache entry");
        Assert.AreEqual(1, parseCalls);
    }

    [TestMethod]
    public void GetOrParse_DifferentKeys_ParseIndependentPaths()
    {
        var a = SvgIconHelper.SvgPathCache.GetOrParse("cache-distinct-a", "M0,0L10,0L10,10L0,10Z");
        var b = SvgIconHelper.SvgPathCache.GetOrParse("cache-distinct-b", "M0,0L10,0L10,10L0,10Z");

        Assert.AreNotSame(a, b);
    }

    [TestMethod]
    public void DrawPathScaled_CentersAndScalesPathAtCenter()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        // Bounds (0,0,100,20), mid (50,10) — sizePx 100 ⇒ scale 1.
        var rect = SKPath.ParseSvgPathData("M0,0L100,0L100,20L0,20Z")!;

        SvgIconHelper.DrawPathScaled(canvas, rect, new SKPoint(200, 150), 100, SKColors.Red, 0, 0);

        var pixmap = surface.PeekPixels();
        Assert.AreEqual(SKColors.Red, pixmap.GetPixelColor(200, 150), "the path center must land on the target center");
        Assert.AreEqual(SKColors.Red, pixmap.GetPixelColor(160, 145), "an interior path point inside the scaled rect");
        Assert.AreEqual(SKColors.Red, pixmap.GetPixelColor(240, 155), "an interior path point inside the scaled rect");
        Assert.AreEqual(SKColors.Black, pixmap.GetPixelColor(140, 130), "a point outside the rect must stay clear");
        Assert.AreEqual(SKColors.Black, pixmap.GetPixelColor(260, 170), "a point outside the rect must stay clear");
    }

    [TestMethod]
    public void DrawPathScaled_LargerSize_ScalesAboutThePathCenter()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        var rect = SKPath.ParseSvgPathData("M0,0L100,0L100,20L0,20Z")!;

        // sizePx 200 ⇒ scale 2: the rect now spans x∈[100,300], y∈[130,170].
        SvgIconHelper.DrawPathScaled(canvas, rect, new SKPoint(200, 150), 200, SKColors.Red, 0, 0);

        var pixmap = surface.PeekPixels();
        Assert.AreEqual(SKColors.Red, pixmap.GetPixelColor(200, 150), "scaling must keep the path center at the target center");
        Assert.AreEqual(SKColors.Red, pixmap.GetPixelColor(102, 132), "path point (1,1) — inside the doubled rect");
        Assert.AreEqual(SKColors.Black, pixmap.GetPixelColor(98, 128), "path point (-1,-1) — outside the doubled rect");
        Assert.AreEqual(SKColors.Black, pixmap.GetPixelColor(302, 172), "path point (101,21) — outside the doubled rect");
    }

    [TestMethod]
    public void DrawPathScaled_Offsets_ShiftTheDrawing()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        var rect = SKPath.ParseSvgPathData("M0,0L100,0L100,20L0,20Z")!;

        // Offset (10,5): the rect moves to x∈[160,260], y∈[145,165].
        SvgIconHelper.DrawPathScaled(canvas, rect, new SKPoint(200, 150), 100, SKColors.Red, 10, 5);

        var pixmap = surface.PeekPixels();
        Assert.AreEqual(SKColors.Red, pixmap.GetPixelColor(210, 155), "the offset must shift the drawing with the center");
        Assert.AreEqual(SKColors.Red, pixmap.GetPixelColor(170, 150), "an interior point of the shifted rect");
        Assert.AreEqual(SKColors.Black, pixmap.GetPixelColor(140, 140), "the unshifted footprint must stay clear");
    }

    [TestMethod]
    public void DrawPathScaled_EmptyPathOrZeroSize_IsNoOp()
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        SvgIconHelper.DrawPathScaled(canvas, new SKPath(), new SKPoint(100, 100), 50, SKColors.Red, 0, 0);
        SvgIconHelper.DrawPathScaled(canvas, new SKPath(), new SKPoint(100, 100), 0, SKColors.Red, 0, 0);

        Assert.AreEqual(SKColors.Black, surface.PeekPixels().GetPixelColor(100, 100),
            "an empty path or a zero target size must draw nothing");
    }
}
