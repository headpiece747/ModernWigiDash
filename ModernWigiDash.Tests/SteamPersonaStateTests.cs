using ModernWigiDash.Widgets.Steam.Models;

namespace ModernWigiDash.Tests;

/// <summary>
/// The persona-state ordinal's ONE fact: its display label and its online-dot
/// color come from the same table, so the two facets cannot drift apart.
/// Known ordinals carry their label + color; an unknown ordinal reads as the
/// ordinal string with the offline dot.
/// </summary>
[TestClass]
public sealed class SteamPersonaStateTests
{
    [TestMethod]
    public void KnownOrdinals_CarryTheLabelAndColor()
    {
        Assert.AreEqual("Offline", SteamPersonaState.Label(0));
        AssertColor(SteamPersonaState.Color(0), 144, 148, 153);

        Assert.AreEqual("Online", SteamPersonaState.Label(1));
        AssertColor(SteamPersonaState.Color(1), 86, 199, 115);

        Assert.AreEqual("InGame", SteamPersonaState.Label(2));
        AssertColor(SteamPersonaState.Color(2), 95, 177, 255);

        Assert.AreEqual("Busy", SteamPersonaState.Label(3));
        AssertColor(SteamPersonaState.Color(3), 255, 107, 107);

        Assert.AreEqual("Away", SteamPersonaState.Label(4));
        AssertColor(SteamPersonaState.Color(4), 255, 197, 91);

        Assert.AreEqual("Snoozing", SteamPersonaState.Label(5));
        AssertColor(SteamPersonaState.Color(5), 255, 197, 91);
    }

    [TestMethod]
    public void UnknownOrdinal_ReadsTheOrdinalAndTheOfflineDot()
    {
        Assert.AreEqual("7", SteamPersonaState.Label(7));
        AssertColor(SteamPersonaState.Color(7), 144, 148, 153);
    }

    [TestMethod]
    public void MissingStateSentinel_ReadsOfflineAndTheOfflineDot()
    {
        Assert.AreEqual("Offline", SteamPersonaState.Label(-1), "the missing-state sentinel (-1) reads as Offline, not the raw ordinal");
        AssertColor(SteamPersonaState.Color(-1), 144, 148, 153);
    }

    /// <summary>The dot is a fully-opaque RGB (alpha 255).</summary>
    private static void AssertColor(SKColor color, int red, int green, int blue)
    {
        Assert.AreEqual((byte)red, color.Red);
        Assert.AreEqual((byte)green, color.Green);
        Assert.AreEqual((byte)blue, color.Blue);
        Assert.AreEqual((byte)255, color.Alpha);
    }
}
