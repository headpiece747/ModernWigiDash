
namespace ModernWigiDash.Tests;

/// <summary>
/// The artwork background-color rule — previously buried in the artwork
/// loader and untestable without loading real album art. Now pure over the
/// downsample bitmap.
/// </summary>
[TestClass]
public class ArtworkBackgroundColorTests
{
    private static SKBitmap SolidSample(byte r, byte g, byte b)
    {
        var bitmap = new SKBitmap(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(r, g, b));
        return bitmap;
    }

    [TestMethod]
    public void FromSample_MidBrightColor_ReturnedUnchanged()
    {
        using var sample = SolidSample(120, 40, 40); // brightness 0.47, saturated

        var color = ArtworkBackgroundColor.FromSample(sample);

        Assert.AreEqual(120, color.Red, "the selected color keeps its identity");
        Assert.AreEqual(40, color.Green);
        Assert.AreEqual(40, color.Blue);
    }

    [TestMethod]
    public void FromSample_BrightColor_DarkenedToSixtyFivePercent()
    {
        // Brightness 0.86 survives the 0.92 filter, then darkens to 0.65.
        using var sample = SolidSample(220, 60, 60);

        var color = ArtworkBackgroundColor.FromSample(sample);

        Assert.AreEqual(165, color.Red, "220 * 0.65/0.8627 = 165.8, truncated to 165");
        Assert.AreEqual(45, color.Green, "60 * 0.65/0.86 = 45.3 → 45");
        Assert.AreEqual(45, color.Blue);
    }

    [TestMethod]
    public void FromSample_GrayImage_FallsBackToBrightestBucket()
    {
        // Gray has zero saturation — the colorful branch is empty, the
        // brightness branch picks the brightest bucket.
        using var sample = SolidSample(150, 150, 150);

        var color = ArtworkBackgroundColor.FromSample(sample);

        Assert.AreEqual(150, color.Red);
        Assert.AreEqual(150, color.Green);
        Assert.AreEqual(150, color.Blue);
    }

    [TestMethod]
    public void FromSample_AllBlack_ReturnsCenterPixel()
    {
        using var sample = SolidSample(0, 0, 0); // brightness 0 — no bucket survives

        var color = ArtworkBackgroundColor.FromSample(sample);

        Assert.AreEqual(0, color.Red, "the empty-bucket branch returns the sample center");
        Assert.AreEqual(0, color.Green);
        Assert.AreEqual(0, color.Blue);
    }

    [TestMethod]
    public void FromSample_MixedSaturatedAndMuted_PrefersTheColorfulArea()
    {
        var bitmap = new SKBitmap(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(140, 140, 140)); // large muted area
            canvas.DrawRect(new SKRect(0, 0, 24, 24), new SKPaint { Color = new SKColor(160, 40, 40) }); // smaller colorful area
        }

        var color = ArtworkBackgroundColor.FromSample(bitmap);

        Assert.IsTrue(color.Red > 120, "the colorful bucket wins over the larger muted area");
        Assert.IsTrue(color.Red > color.Green, "the result leans red");
    }
}
