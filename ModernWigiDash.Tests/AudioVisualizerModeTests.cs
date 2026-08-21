
namespace ModernWigiDash.Tests;

[TestClass]
public class AudioVisualizerModeTests
{
    [TestMethod]
    public void Parse_KnownStyles_MapExactly()
    {
        Assert.AreEqual(AudioVisualizerMode.NeonBars, AudioVisualizerModeParser.Parse("Neon Bars"));
        Assert.AreEqual(AudioVisualizerMode.Oscilloscope, AudioVisualizerModeParser.Parse("Oscilloscope Wave"));
        Assert.AreEqual(AudioVisualizerMode.RadialPulse, AudioVisualizerModeParser.Parse("Radial Pulse"));
    }

    [TestMethod]
    public void Parse_UnknownStyle_DefaultsToNeonBars()
    {
        Assert.AreEqual(AudioVisualizerMode.NeonBars, AudioVisualizerModeParser.Parse("Bogus"));
        Assert.AreEqual(AudioVisualizerMode.NeonBars, AudioVisualizerModeParser.Parse(null));
    }
}
