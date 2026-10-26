using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Rendering;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Steam text-fit owner (the shared module behind the grid and
/// showcase renderers): the zero-allocation binary-search fit, the bounded
/// name cache with the source-rename rule, and the empty-state draw.
/// </summary>
[TestClass]
public class SteamTextFitterTests
{
    private static SKFont Font(float px) => FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, px);

    private static SKPaint Paint() => new() { IsAntialias = true };

    private static SteamGameItem Game(uint appId, string name) => new(appId, name, "", SteamGameState.Idle, 0f, 0, null, false);

    [TestMethod]
    public void Fit_TextThatFits_ReturnsTheSameString()
    {
        var fitter = new SteamTextFitter(cacheLimit: 32);
        var font = Font(12f);
        var paint = Paint();
        string text = "OK";

        string fitted = fitter.Fit(text, font, paint, 100f);

        Assert.AreSame(text, fitted, "a fitting text is the SAME string (the zero-allocation contract)");
    }

    [TestMethod]
    public void Fit_OverwideText_EllipsizesWithinTheWidth()
    {
        var fitter = new SteamTextFitter(cacheLimit: 32);
        var font = Font(12f);
        var paint = Paint();
        string text = "A very long game title that cannot possibly fit";
        float maxWidth = font.MeasureText("AB…", paint);

        string fitted = fitter.Fit(text, font, paint, maxWidth);

        Assert.IsTrue(fitted.EndsWith('…'), "the fitted text carries the ellipsis");
        Assert.IsTrue(font.MeasureText(fitted, paint) <= maxWidth, "the fitted text measures inside the width");
        Assert.IsTrue(fitted.StartsWith("A ", StringComparison.Ordinal), "the fit keeps the longest fitting prefix");
    }

    [TestMethod]
    public void Fit_WidthBelowTheEllipsis_ReturnsTheBareEllipsis()
    {
        var fitter = new SteamTextFitter(cacheLimit: 32);
        var font = Font(12f);
        var paint = Paint();

        string fitted = fitter.Fit("Some title", font, paint, 1f);

        Assert.AreEqual("…", fitted, "a width that cannot hold the ellipsis yields the bare ellipsis");
    }

    [TestMethod]
    public void FitName_SteadyState_ReusesTheCachedFit()
    {
        var fitter = new SteamTextFitter(cacheLimit: 32);
        var font = Font(12f);
        var paint = Paint();
        var game = Game(730, "Counter-Strike 2");

        string first = fitter.FitName(game, font, paint, 12f, 30f);
        string second = fitter.FitName(game, font, paint, 12f, 30f);

        Assert.AreSame(first, second, "a steady-state frame reuses the cached fit");
    }

    [TestMethod]
    public void FitName_RenamedTitle_Refits()
    {
        var fitter = new SteamTextFitter(cacheLimit: 32);
        var font = Font(12f);
        var paint = Paint();
        var game = Game(730, "Counter-Strike 2");

        // A width the ORIGINAL title fits: the renamed (longer) title must
        // refit to an ellipsized form, which a stale cache hit would not serve.
        float maxWidth = font.MeasureText("Counter-Strike 2", paint);
        _ = fitter.FitName(game, font, paint, 12f, maxWidth);
        SteamGameItem renamed = game with { Name = "Counter-Strike 2 Deluxe Edition" };
        string refit = fitter.FitName(renamed, font, paint, 12f, maxWidth);

        Assert.IsTrue(refit.EndsWith('…'), "the source-name compare catches a rename and refits");
    }

    [TestMethod]
    public void FitName_PastTheLimit_EvictsTheCache()
    {
        var fitter = new SteamTextFitter(cacheLimit: 2);
        var font = Font(12f);
        var paint = Paint();

        _ = fitter.FitName(Game(1, "One"), font, paint, 12f, 40f);
        _ = fitter.FitName(Game(2, "Two"), font, paint, 12f, 40f);
        _ = fitter.FitName(Game(3, "Three"), font, paint, 12f, 40f);

        Assert.AreEqual(1, fitter.CacheCountForTest, "at the limit the whole cache resets and the new entry is the sole occupant");
    }

    [TestMethod]
    public void DrawEmptyState_Smoke_DrawsTheCenteredMessage()
    {
        var fitter = new SteamTextFitter(cacheLimit: 32);
        var paint = Paint();
        using var bitmap = new SKBitmap(160, 80, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        fitter.DrawEmptyState(canvas, new SKRect(0, 0, 160f, 80f), SKColors.White, paint);

        SKColor center = bitmap.GetPixel(80, 40);
        Assert.IsTrue(center.Alpha > 0, "the empty-state message paints inside the bounds");
    }
}
