namespace ModernWigiDash.Tests;

[TestClass]
public class MediaSnapshotBuilderTests
{
    [TestMethod]
    public void Build_FullData_PopulatesAllFields()
    {
        var clock = new FakeTimeProvider();
        var props = new MediaPropertiesData
        {
            Title = "Test Song",
            Artist = "Test Artist",
            AlbumTitle = "Test Album",
            AlbumArtist = "Test Album Artist",
            TrackNumber = 5,
            AlbumTrackCount = 12,
            Genres = ["Rock", "Pop"]
        };
        var info = new PlaybackInfoData
        {
            PlaybackStatus = MediaPlaybackStatus.Playing,
            IsShuffleActive = true,
            AutoRepeatMode = MediaRepeatMode.List,
            PlaybackRate = 1.5,
            Controls = new MediaControlsData
            {
                IsPlayEnabled = true,
                IsPauseEnabled = true,
                IsStopEnabled = true,
                IsNextEnabled = true,
                IsPreviousEnabled = true,
                IsPlaybackPositionEnabled = true,
                IsShuffleEnabled = true,
                IsRepeatEnabled = true
            }
        };
        var timeline = new TimelinePropertiesData
        {
            Position = TimeSpan.FromSeconds(30),
            EndTime = TimeSpan.FromSeconds(240),
            LastUpdatedTime = clock.GetUtcNow()
        };

        var snapshot = MediaSnapshotBuilder.Build("com.test.app", props, info, timeline, clock);

        Assert.AreEqual("com.test.app", snapshot.SourceAppId);
        Assert.AreEqual("Test Song", snapshot.Title);
        Assert.AreEqual("Test Artist", snapshot.Artist);
        Assert.AreEqual("Test Album", snapshot.Album);
        Assert.AreEqual("Test Album Artist", snapshot.AlbumArtist);
        Assert.AreEqual(5, snapshot.TrackNumber);
        Assert.AreEqual(12, snapshot.AlbumTrackCount);
        CollectionAssert.AreEqual(new[] { "Rock", "Pop" }, snapshot.Genres);
        Assert.AreEqual(MediaPlaybackStatus.Playing, snapshot.Status);
        Assert.AreEqual(TimeSpan.FromSeconds(30), snapshot.Position);
        Assert.AreEqual(TimeSpan.FromSeconds(240), snapshot.Duration);
        Assert.AreEqual(clock.GetUtcNow(), snapshot.LastUpdated);
        Assert.IsTrue(snapshot.Shuffle);
        Assert.AreEqual(MediaRepeatMode.List, snapshot.Repeat);
        Assert.AreEqual(1.5, snapshot.PlaybackRate);
        Assert.IsTrue(snapshot.CanPlay);
        Assert.IsTrue(snapshot.CanPause);
        Assert.IsTrue(snapshot.CanStop);
        Assert.IsTrue(snapshot.CanNext);
        Assert.IsTrue(snapshot.CanPrev);
        Assert.IsTrue(snapshot.CanSeek);
        Assert.IsTrue(snapshot.CanShuffle);
        Assert.IsTrue(snapshot.CanRepeat);
    }

    [TestMethod]
    public void Build_NullProps_DefaultsMetadata()
    {
        var clock = new FakeTimeProvider();
        var snapshot = MediaSnapshotBuilder.Build("app", null, null, null, clock);

        Assert.AreEqual("", snapshot.Title);
        Assert.AreEqual("", snapshot.Artist);
        Assert.AreEqual("", snapshot.Album);
        Assert.AreEqual("", snapshot.AlbumArtist);
        Assert.AreEqual(0, snapshot.TrackNumber);
        Assert.AreEqual(0, snapshot.AlbumTrackCount);
        Assert.AreEqual(0, snapshot.Genres.Length);
        Assert.AreEqual(MediaPlaybackStatus.Closed, snapshot.Status);
        Assert.AreEqual(TimeSpan.Zero, snapshot.Position);
        Assert.AreEqual(TimeSpan.Zero, snapshot.Duration);
        Assert.AreEqual(clock.GetUtcNow(), snapshot.LastUpdated);
        Assert.IsFalse(snapshot.Shuffle);
        Assert.AreEqual(MediaRepeatMode.None, snapshot.Repeat);
        Assert.AreEqual(1.0, snapshot.PlaybackRate);
        Assert.IsFalse(snapshot.CanPlay);
        Assert.IsFalse(snapshot.CanPause);
        Assert.IsFalse(snapshot.CanStop);
        Assert.IsFalse(snapshot.CanNext);
        Assert.IsFalse(snapshot.CanPrev);
        Assert.IsFalse(snapshot.CanSeek);
        Assert.IsFalse(snapshot.CanShuffle);
        Assert.IsFalse(snapshot.CanRepeat);
    }

    [TestMethod]
    public void Sanitize_StripsControlChars_AndCapsAt256()
    {
        var input = "Hello\x01World";
        var result = MediaSnapshotBuilder.Sanitize(input, "");
        Assert.AreEqual("HelloWorld", result);

        var longInput = new string('a', 300);
        var capped = MediaSnapshotBuilder.Sanitize(longInput, "");
        Assert.AreEqual(256, capped.Length);
    }

    [TestMethod]
    public void Sanitize_EmptyInput_ReturnsFallback()
    {
        Assert.AreEqual("fallback", MediaSnapshotBuilder.Sanitize(null, "fallback"));
        Assert.AreEqual("fallback", MediaSnapshotBuilder.Sanitize("", "fallback"));
        Assert.AreEqual("fallback", MediaSnapshotBuilder.Sanitize("   ", "fallback"));
    }
}
