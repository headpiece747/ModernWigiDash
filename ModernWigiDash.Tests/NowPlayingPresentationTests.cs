using Windows.Media;
using Windows.Media.Control;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Now Playing widget's display rules — the strings and ratios the render
/// methods lay out. Previously private members of a 719-line widget with no
/// live-session assertions; now pure and assertable.
/// </summary>
[TestClass]
public class NowPlayingPresentationTests
{
    [TestMethod]
    public void FormatTime_SecondsUnderAnHour_MinutesAndSeconds()
    {
        Assert.AreEqual("0:00", NowPlayingPresentation.FormatTime(0));
        Assert.AreEqual("0:07", NowPlayingPresentation.FormatTime(7));
        Assert.AreEqual("1:05", NowPlayingPresentation.FormatTime(65));
        Assert.AreEqual("59:59", NowPlayingPresentation.FormatTime(3599));
    }

    [TestMethod]
    public void FormatTime_OverAnHour_HoursColonMinutesColonSeconds()
    {
        Assert.AreEqual("1:00:00", NowPlayingPresentation.FormatTime(3600));
        Assert.AreEqual("2:03:04", NowPlayingPresentation.FormatTime(2 * 3600 + 3 * 60 + 4));
    }

    [TestMethod]
    public void FormatTime_InvalidDuration_ReadsZero()
    {
        Assert.AreEqual("0:00", NowPlayingPresentation.FormatTime(-5));
        Assert.AreEqual("0:00", NowPlayingPresentation.FormatTime(double.NaN));
        Assert.AreEqual("0:00", NowPlayingPresentation.FormatTime(double.PositiveInfinity));
    }

    [TestMethod]
    public void FriendlyAppName_EveryKnownAppId_MapsToDisplayName()
    {
        Assert.AreEqual("Spotify", NowPlayingPresentation.FriendlyAppName("Spotify.exe"));
        Assert.AreEqual("Chrome", NowPlayingPresentation.FriendlyAppName("chrome.exe"));
        Assert.AreEqual("Edge", NowPlayingPresentation.FriendlyAppName("msedge.exe"));
        Assert.AreEqual("Firefox", NowPlayingPresentation.FriendlyAppName("firefox.exe"));
        Assert.AreEqual("VLC", NowPlayingPresentation.FriendlyAppName("vlc.exe"));
        Assert.AreEqual("iTunes", NowPlayingPresentation.FriendlyAppName("iTunes.exe"));
        Assert.AreEqual("Apple Music", NowPlayingPresentation.FriendlyAppName("AppleMusic.exe"));
        Assert.AreEqual("Windows Media Player", NowPlayingPresentation.FriendlyAppName("wmplayer.exe"));
        Assert.AreEqual("Discord", NowPlayingPresentation.FriendlyAppName("discord.exe"));
        Assert.AreEqual("foobar2000", NowPlayingPresentation.FriendlyAppName("foobar2000.exe"));
        Assert.AreEqual("Steam", NowPlayingPresentation.FriendlyAppName("steam.exe"));
    }

    [TestMethod]
    public void FriendlyAppName_UnknownAppId_FallsBackToLastAumidSegment()
    {
        Assert.AreEqual("Media", NowPlayingPresentation.FriendlyAppName(""));
        Assert.AreEqual("BarBaz", NowPlayingPresentation.FriendlyAppName("com.foo.BarBaz"));
        Assert.AreEqual("ExceedingSixteen", NowPlayingPresentation.FriendlyAppName("com.verylong.packagename.ExceedingSixteenChars"),
            "the fallback truncates the last AUMID segment to 16 characters");
    }

    [TestMethod]
    public void FriendlyAppName_MultiTokenIds_FirstMatchingRuleWins()
    {
        // The rule order is load-bearing: an ID carrying several known tokens
        // maps to the earliest rule, not to any of the others. A reorder (or
        // a merge into an unordered set) must fail here.
        Assert.AreEqual("iTunes", NowPlayingPresentation.FriendlyAppName("Apple.iTunes"),
            "'itunes' must outrank the bare 'apple' token");
        Assert.AreEqual("Spotify", NowPlayingPresentation.FriendlyAppName("spotify.music"),
            "'spotify' must outrank the bare 'music' token");
        Assert.AreEqual("Apple Music", NowPlayingPresentation.FriendlyAppName("Grooveshark.music"),
            "a bare 'music' token still maps to Apple Music");
        Assert.AreEqual("Windows Media Player", NowPlayingPresentation.FriendlyAppName("mediaplayer.wmplayer"),
            "the two Windows Media Player tokens resolve to one display name");
    }

    [TestMethod]
    public void FriendlyAppName_CaseInsensitive_MatchesAnyCasing()
    {
        Assert.AreEqual("Spotify", NowPlayingPresentation.FriendlyAppName("SPOTIFY.exe"));
        Assert.AreEqual("Edge", NowPlayingPresentation.FriendlyAppName("MSEdge"));
        Assert.AreEqual("Steam", NowPlayingPresentation.FriendlyAppName("SteamApps\\client.exe"));
    }

    [TestMethod]
    public void FriendlyAppName_FallbackSegment_KeepsSixteenAndTruncatesSeventeen()
    {
        string exactly16 = new('a', 16);
        string seventeen = new('b', 17);
        Assert.AreEqual(exactly16, NowPlayingPresentation.FriendlyAppName($"pkg.{exactly16}"),
            "a 16-character segment is the boundary: it survives whole");
        Assert.AreEqual(new('b', 16), NowPlayingPresentation.FriendlyAppName($"pkg.{seventeen}"),
            "a 17-character segment truncates to 16");
        Assert.AreEqual("App", NowPlayingPresentation.FriendlyAppName("com.example.Package!App"),
            "the AUMID '!' separator splits before the last-dot split");
    }

    [TestMethod]
    public void MetaLine_TrackNumberAndGenres_JoinWithSeparators()
    {
        Assert.AreEqual("", NowPlayingPresentation.MetaLine(0, 0, []));
        Assert.AreEqual("Track 3", NowPlayingPresentation.MetaLine(3, 0, []));
        Assert.AreEqual("Track 3/12", NowPlayingPresentation.MetaLine(3, 12, []));
        Assert.AreEqual("Rock / Metal", NowPlayingPresentation.MetaLine(0, 0, ["Rock", "Metal", "Pop"]),
            "only the first two genres show");
        Assert.AreEqual("Track 3/12 · Rock / Metal", NowPlayingPresentation.MetaLine(3, 12, ["Rock", "Metal"]));
    }

    [TestMethod]
    public void ProgressRatio_ClampsAndHandlesUnknownDuration()
    {
        Assert.AreEqual(0.5, NowPlayingPresentation.ProgressRatio(30, 60), 0.001);
        Assert.AreEqual(1.0, NowPlayingPresentation.ProgressRatio(120, 60), 0.001, "past the end clamps to 1");
        Assert.AreEqual(0.0, NowPlayingPresentation.ProgressRatio(30, 0), 0.001, "unknown duration reads 0");
    }

    [TestMethod]
    public void SeekRatio_TapPointMapsToClampedRatio()
    {
        Assert.AreEqual(0.5, NowPlayingPresentation.SeekRatio(50, 0, 100), 0.001);
        Assert.AreEqual(0.0, NowPlayingPresentation.SeekRatio(0, 50, 100), 0.001, "left of the bar clamps to 0");
        Assert.AreEqual(1.0, NowPlayingPresentation.SeekRatio(200, 50, 100), 0.001, "right of the bar clamps to 1");
        Assert.AreEqual(0.0, NowPlayingPresentation.SeekRatio(50, 50, 0), 0.001);
    }

    [TestMethod]
    public void IsIdle_NoSnapshotOrClosedOrStoppedSession_ReadsIdle()
    {
        Assert.IsTrue(NowPlayingPresentation.IsIdle(null), "no session at all is the idle panel");
        Assert.IsTrue(NowPlayingPresentation.IsIdle(SnapshotWith(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)));
        Assert.IsTrue(NowPlayingPresentation.IsIdle(SnapshotWith(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)));
    }

    [TestMethod]
    public void IsIdle_EveryLiveSessionState_ReadsLive()
    {
        // The idle rule excludes exactly Closed and Stopped; every other
        // session state draws the media view.
        Assert.IsFalse(NowPlayingPresentation.IsIdle(SnapshotWith(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)));
        Assert.IsFalse(NowPlayingPresentation.IsIdle(SnapshotWith(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)));
        Assert.IsFalse(NowPlayingPresentation.IsIdle(SnapshotWith(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened)));
        Assert.IsFalse(NowPlayingPresentation.IsIdle(SnapshotWith(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing)));
    }

    [TestMethod]
    public void CanSeekNow_SeekableWithPositiveDuration_AllowsTheSeekTap()
    {
        var snap = SnapshotWith(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        snap.CanSeek = true;

        Assert.IsTrue(NowPlayingPresentation.CanSeekNow(snap));
    }

    [TestMethod]
    public void CanSeekNow_NotSeekableOrZeroDuration_VetoesTheSeekTap()
    {
        var snap = SnapshotWith(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        snap.CanSeek = true;
        snap.Duration = TimeSpan.Zero;
        Assert.IsFalse(NowPlayingPresentation.CanSeekNow(snap), "a zero-duration track must not seek to a single point");

        snap.Duration = TimeSpan.FromSeconds(120);
        snap.CanSeek = false;
        Assert.IsFalse(NowPlayingPresentation.CanSeekNow(snap), "a non-seekable session must not seek");
    }

    [TestMethod]
    public void PlaybackRateText_OnlyDeviationsFromOneShow()
    {
        Assert.IsNull(NowPlayingPresentation.PlaybackRateText(1.0));
        Assert.AreEqual("1.5×", NowPlayingPresentation.PlaybackRateText(1.5));
        Assert.AreEqual("0.5×", NowPlayingPresentation.PlaybackRateText(0.5));
    }

    [TestMethod]
    public void NextRepeatMode_CyclesNoneListTrackAndWraps()
    {
        // The tap handler walks None -> List -> Track -> None; the cycle is a
        // display decision, assertable here without a widget or SMTC session.
        Assert.AreEqual(MediaPlaybackAutoRepeatMode.List, NowPlayingPresentation.NextRepeatMode(MediaPlaybackAutoRepeatMode.None));
        Assert.AreEqual(MediaPlaybackAutoRepeatMode.Track, NowPlayingPresentation.NextRepeatMode(MediaPlaybackAutoRepeatMode.List));
        Assert.AreEqual(MediaPlaybackAutoRepeatMode.None, NowPlayingPresentation.NextRepeatMode(MediaPlaybackAutoRepeatMode.Track));
        // The cycle must close: three steps from None land back on None.
        MediaPlaybackAutoRepeatMode current = MediaPlaybackAutoRepeatMode.None;
        for (int i = 0; i < 3; i++)
        {
            current = NowPlayingPresentation.NextRepeatMode(current);
        }
        Assert.AreEqual(MediaPlaybackAutoRepeatMode.None, current, "the cycle must wrap to its start");

        // A value outside the projection's declared set (None=0, Track=1,
        // List=2; the OS can report values the compile-time projection
        // does not name) degrades to None via the catch-all arm.
        Assert.AreEqual(MediaPlaybackAutoRepeatMode.None,
            NowPlayingPresentation.NextRepeatMode((MediaPlaybackAutoRepeatMode)3),
            "an out-of-cycle mode must degrade to the cycle's start");
    }

    [TestMethod]
    public void ExtrapolatedPosition_PlayingAdvancesByElapsedSinceLastUpdated()
    {
        DateTimeOffset lastUpdated = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var snap = new MediaSnapshot
        {
            Position = TimeSpan.FromSeconds(30),
            Duration = TimeSpan.FromSeconds(60),
            LastUpdated = lastUpdated,
            Status = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
        };

        double pos = NowPlayingPresentation.ExtrapolatedPosition(snap, lastUpdated.AddSeconds(5));

        Assert.AreEqual(35.0, pos, 0.001, "a playing snapshot advances by the elapsed render time");
    }

    [TestMethod]
    public void ExtrapolatedPosition_PausedReadsRawPosition()
    {
        DateTimeOffset lastUpdated = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var snap = new MediaSnapshot
        {
            Position = TimeSpan.FromSeconds(30),
            Duration = TimeSpan.FromSeconds(60),
            LastUpdated = lastUpdated,
            Status = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
        };

        double pos = NowPlayingPresentation.ExtrapolatedPosition(snap, lastUpdated.AddSeconds(5));

        Assert.AreEqual(30.0, pos, 0.001, "a paused snapshot must not advance between refreshes");
    }

    private static MediaSnapshot SnapshotWith(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        => new()
        {
            Status = status,
            Duration = TimeSpan.FromSeconds(120)
        };
}
