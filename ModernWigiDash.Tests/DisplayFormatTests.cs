using System.Globalization;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The display-rules culture contract: one invariant, zero-aware number
/// formatter shared by every presentation module, so a comma-decimal machine
/// renders exactly what an en-US machine renders.
/// </summary>
[TestClass]
public class DisplayFormatTests
{
    // ── zero values ──

    [TestMethod]
    public void Zero_AllHelpers_RenderTheirZeroStrings()
    {
        Assert.AreEqual("0 FPS", DisplayFormat.Fps(0));
        Assert.AreEqual("0", DisplayFormat.FpsValue(0));
        Assert.AreEqual("0.0 ms", DisplayFormat.Ms(0));
        Assert.AreEqual("0%", DisplayFormat.Pct(0));
        Assert.AreEqual("0", DisplayFormat.Count(0));
    }

    // ── Fps ──

    [TestMethod]
    public void Fps_RoundsToWholeFps()
    {
        Assert.AreEqual("60 FPS", DisplayFormat.Fps(60));
        Assert.AreEqual("162 FPS", DisplayFormat.Fps(162.4));
        Assert.AreEqual("138 FPS", DisplayFormat.Fps(137.6), "F0 rounds");
    }

    [TestMethod]
    public void Fps_Midpoint_RoundsHalfToEven()
    {
        // "F0" is round-half-to-even: 136.5 → 136, 137.5 → 138. Pin the
        // contract so a swap to AwayFromZero cannot slip through.
        Assert.AreEqual("136 FPS", DisplayFormat.Fps(136.5));
        Assert.AreEqual("138 FPS", DisplayFormat.Fps(137.5));
        Assert.AreEqual("2", DisplayFormat.FpsValue(2.5));
        Assert.AreEqual("62%", DisplayFormat.Pct(62.5));
    }

    [TestMethod]
    public void Fps_ZeroNegativeOrNaN_ReadsZeroFps()
    {
        Assert.AreEqual("0 FPS", DisplayFormat.Fps(0));
        Assert.AreEqual("0 FPS", DisplayFormat.Fps(-5), "a negative reading is never shown as negative FPS");
        Assert.AreEqual("0 FPS", DisplayFormat.Fps(double.NaN));
        Assert.AreEqual("0 FPS", DisplayFormat.Fps(double.PositiveInfinity), "a non-finite reading is never shown as a value");
    }

    [TestMethod]
    public void FpsValue_ZeroAwareBareNumber()
    {
        Assert.AreEqual("162", DisplayFormat.FpsValue(162.4));
        Assert.AreEqual("0", DisplayFormat.FpsValue(-1));
    }

    // ── Ms ──

    [TestMethod]
    public void Ms_OneDecimal_RoundsInvariant()
    {
        Assert.AreEqual("16.7 ms", DisplayFormat.Ms(16.7));
        Assert.AreEqual("6.2 ms", DisplayFormat.Ms(6.16), "F1 rounds");
        Assert.AreEqual("7.2 ms", DisplayFormat.Ms(7.2));
    }

    [TestMethod]
    public void Ms_ZeroNegativeOrNonFinite_ReadsZeroMs()
    {
        Assert.AreEqual("0.0 ms", DisplayFormat.Ms(0));
        Assert.AreEqual("0.0 ms", DisplayFormat.Ms(-3.2));
        Assert.AreEqual("0.0 ms", DisplayFormat.Ms(double.NaN));
        Assert.AreEqual("0.0 ms", DisplayFormat.Ms(double.PositiveInfinity), "1000/0-derived frame times read zero");
        Assert.AreEqual("0.0 ms", DisplayFormat.Ms(double.NegativeInfinity));
    }

    [TestMethod]
    public void Ms_LargeValue_RendersWholeMs()
    {
        Assert.AreEqual("123456.8 ms", DisplayFormat.Ms(123456.78));
    }

    // ── Pct ──

    [TestMethod]
    public void Pct_RoundsToWholePercent_ZeroForNonPositive()
    {
        Assert.AreEqual("71%", DisplayFormat.Pct(71));
        Assert.AreEqual("64%", DisplayFormat.Pct(63.5));
        Assert.AreEqual("0%", DisplayFormat.Pct(0));
        Assert.AreEqual("0%", DisplayFormat.Pct(-1));
    }

    // ── Count ──

    [TestMethod]
    public void Count_InvariantInteger()
    {
        Assert.AreEqual("3", DisplayFormat.Count(3));
        Assert.AreEqual("0", DisplayFormat.Count(0));
        Assert.AreEqual("-12", DisplayFormat.Count(-12));
    }

    // ── Value / Number ──

    [TestMethod]
    public void Value_FormatsInvariant_WithTheRequestedFormat()
    {
        Assert.AreEqual("21.5", DisplayFormat.Value(21.5, "F1"));
        Assert.AreEqual("-3", DisplayFormat.Value(-3.2, "F0"), "temperatures keep negative values");
        Assert.AreEqual("71", DisplayFormat.Value(71.0, "F0"));
    }

    [TestMethod]
    public void Number_GroupSeparatedInvariantDecimals()
    {
        Assert.AreEqual("1,234.56", DisplayFormat.Number(1234.56m, 2));
        // The requested precision is an upper bound: extra raw digits round.
        Assert.AreEqual("0.000012", DisplayFormat.Number(0.00001234m, 6));
        Assert.AreEqual("-5.00", DisplayFormat.Number(-5m, 2));
        Assert.AreEqual("1,234.57", DisplayFormat.Number(1234.5678m, 2));
        Assert.AreEqual("0.00", DisplayFormat.Number(0.00001234m, 2));
    }

    // ── the culture contract ──

    [TestMethod]
    public void AllHelpers_AreCultureIndependent_CommaDecimalMachineReadsDots()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.AreEqual("162 FPS", DisplayFormat.Fps(162.4));
            Assert.AreEqual("162", DisplayFormat.FpsValue(162.4));
            Assert.AreEqual("16.7 ms", DisplayFormat.Ms(16.7));
            Assert.AreEqual("64%", DisplayFormat.Pct(63.5));
            Assert.AreEqual("1,234.56", DisplayFormat.Number(1234.56m, 2));
            Assert.AreEqual("21.5", DisplayFormat.Value(21.5, "F1"));
            Assert.AreEqual("-12", DisplayFormat.Count(-12));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
