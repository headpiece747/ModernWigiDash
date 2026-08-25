using Windows.Media;
using Windows.Media.Control;

// The neutral status enum shares its name with the WinRT one, so the neutral
// side is aliased; every bare MediaPlaybackStatus below is the neutral one.
using MediaPlaybackStatus = ModernWigiDash.Widgets.MediaPlaybackStatus;

namespace ModernWigiDash.Tests;

// The SMTC edge mapping pinned against the real WinRT enums: every named SMTC
// value projects to the same-named neutral member, and any value the OS
// reports outside the named set lands on the neutral Unknown. The pin reads
// the real enums by name, so it stays true even if the OS renumbers the
// ordinals (the assumption the old cast-based mapping broke on), and it fails
// loudly if a future OS adds a named value the neutral set does not cover or
// a neutral member is renamed.
[TestClass]
public class WinRtMediaSessionSourceTests
{
    [TestMethod]
    public void ToNeutralStatus_EveryNamedSmValue_ProjectsToTheSameNamedNeutralMember()
    {
        foreach (var value in Enum.GetValues<GlobalSystemMediaTransportControlsSessionPlaybackStatus>())
        {
            var name = Enum.GetName(value);
            var expected = Enum.Parse<MediaPlaybackStatus>(name!);

            Assert.AreEqual(expected, WinRtMediaSessionSource.ToNeutralStatus(value));
        }
    }

    [TestMethod]
    public void ToNeutralStatus_ValueOutsideTheNamedSet_MapsToUnknown()
    {
        Assert.AreEqual(MediaPlaybackStatus.Unknown, WinRtMediaSessionSource.ToNeutralStatus((GlobalSystemMediaTransportControlsSessionPlaybackStatus)(-1)));
        Assert.AreEqual(MediaPlaybackStatus.Unknown, WinRtMediaSessionSource.ToNeutralStatus((GlobalSystemMediaTransportControlsSessionPlaybackStatus)6));
    }

    [TestMethod]
    public void ToNeutralRepeat_EveryNamedSmValue_ProjectsToTheSameNamedNeutralMember()
    {
        foreach (var value in Enum.GetValues<MediaPlaybackAutoRepeatMode>())
        {
            var name = Enum.GetName(value);
            var expected = Enum.Parse<MediaRepeatMode>(name!);

            Assert.AreEqual(expected, WinRtMediaSessionSource.ToNeutralRepeat(value));
        }
    }

    [TestMethod]
    public void ToNeutralRepeat_ValueOutsideTheNamedSet_MapsToUnknown()
    {
        Assert.AreEqual(MediaRepeatMode.Unknown, WinRtMediaSessionSource.ToNeutralRepeat((MediaPlaybackAutoRepeatMode)(-1)));
    }

    [TestMethod]
    public void ToWinRtRepeat_EveryNamedNeutralMode_RoundTripsToTheSameNamedSmMember()
    {
        var modes = new[] { MediaRepeatMode.None, MediaRepeatMode.Track, MediaRepeatMode.List };
        foreach (var mode in modes)
        {
            var expected = (MediaPlaybackAutoRepeatMode)Enum.Parse(typeof(MediaPlaybackAutoRepeatMode), mode.ToString());

            Assert.AreEqual(expected, WinRtMediaSessionSource.ToWinRtRepeat(mode));
        }
    }

    [TestMethod]
    public void ToWinRtRepeat_UnknownMode_DegradesToNone()
    {
        Assert.AreEqual(MediaPlaybackAutoRepeatMode.None, WinRtMediaSessionSource.ToWinRtRepeat(MediaRepeatMode.Unknown));
    }
}
