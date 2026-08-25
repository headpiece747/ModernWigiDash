namespace ModernWigiDash.Tests;

[TestClass]
public class MediaSessionMonitorTests
{
    private static StubMediaSession CreateSession(string appId, string title)
    {
        return new StubMediaSession
        {
            SourceAppUserModelId = appId,
            Properties = new MediaPropertiesData { Title = title, Artist = "Artist", AlbumTitle = "Album" },
            PlaybackInfo = new PlaybackInfoData
            {
                PlaybackStatus = MediaPlaybackStatus.Playing,
                IsShuffleActive = true,
                AutoRepeatMode = MediaRepeatMode.List,
                Controls = new MediaControlsData { IsPauseEnabled = true, IsNextEnabled = true, IsPlaybackPositionEnabled = true }
            },
            Timeline = new TimelinePropertiesData
            {
                Position = TimeSpan.FromSeconds(30),
                EndTime = TimeSpan.FromSeconds(180),
                LastUpdatedTime = DateTimeOffset.Now
            }
        };
    }

    private static MediaSessionMonitor CreateMonitor(StubMediaSessionSourceManager manager)
        => new MediaSessionMonitor(new StubMediaSessionSource { Manager = manager });

    [TestMethod]
    public async Task InitializeAsync_WithManagerAndSession_SubscribesToAllEventsAndBuildsInitialSnapshot()
    {
        var session = CreateSession("spotify.exe", "Song A");
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);

        await monitor.InitializeAsync();

        Assert.AreEqual(1, manager.CurrentSessionChangedSubscriptionCount);
        Assert.AreEqual(1, manager.SessionsChangedSubscriptionCount);
        Assert.AreEqual(1, session.MediaPropertiesSubscriptionCount);
        Assert.AreEqual(1, session.PlaybackInfoSubscriptionCount);
        Assert.AreEqual(1, session.TimelineSubscriptionCount);

        var snap = monitor.CurrentSnapshot;
        Assert.IsNotNull(snap);
        Assert.AreEqual("Song A", snap.Title);
        Assert.AreEqual("Artist", snap.Artist);
        Assert.AreEqual("Album", snap.Album);
        Assert.AreEqual("spotify.exe", snap.SourceAppId);
        Assert.AreEqual(MediaPlaybackStatus.Playing, snap.Status);
        Assert.IsTrue(snap.Shuffle);
        Assert.AreEqual(MediaRepeatMode.List, snap.Repeat);
        Assert.AreEqual(TimeSpan.FromSeconds(30), snap.Position);
        Assert.AreEqual(TimeSpan.FromSeconds(180), snap.Duration);
        Assert.IsTrue(snap.CanPause);
        Assert.IsTrue(snap.CanNext);
        Assert.IsTrue(snap.IsPlaying);
    }

    [TestMethod]
    public async Task InitializeAsync_WhenSourceReturnsNullManager_CompletesWithoutSnapshotOrEvents()
    {
        var monitor = new MediaSessionMonitor(new StubMediaSessionSource { Manager = null });

        await monitor.InitializeAsync();

        Assert.IsNull(monitor.CurrentSnapshot);
        await monitor.DisposeAsync();
    }

    [TestMethod]
    public async Task MediaPropertiesChanged_TriggersSnapshotUpdate_WithCorrectMediaPropertiesAndArtKey()
    {
        var session = CreateSession("browser.exe", "Old Title");
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        MediaSessionUpdate? lastUpdate = null;
        monitor.SnapshotChanged += update => lastUpdate = update;
        session.Properties = new MediaPropertiesData
        {
            Title = "New Title",
            Artist = "New Artist",
            AlbumTitle = "New Album",
            TrackNumber = 3,
            AlbumTrackCount = 12,
            Genres = ["Rock", "Alternative"]
        };

        session.RaiseMediaPropertiesChanged();

        Assert.IsNotNull(lastUpdate);
        Assert.AreEqual("New Title", monitor.CurrentSnapshot?.Title);
        Assert.AreEqual("New Artist", lastUpdate.Snapshot.Artist);
        Assert.AreEqual("New Album", lastUpdate.Snapshot.Album);
        Assert.AreEqual(3, lastUpdate.Snapshot.TrackNumber);
        Assert.AreEqual(12, lastUpdate.Snapshot.AlbumTrackCount);
        Assert.AreEqual(2, lastUpdate.Snapshot.Genres.Length);
        Assert.AreEqual("browser.exe:New Title:New Artist:New Album", lastUpdate.ArtKey);
    }

    [TestMethod]
    public async Task CurrentSessionChanged_ToNull_ClearsSnapshotAndDetachesSessionEvents()
    {
        var session = CreateSession("vlc.exe", "Movie");
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();
        Assert.IsNotNull(monitor.CurrentSnapshot);

        MediaSessionUpdate? lastUpdate = null;
        monitor.SnapshotChanged += update => lastUpdate = update;
        manager.Current = null;

        manager.RaiseCurrentSessionChanged();

        Assert.IsNull(monitor.CurrentSnapshot);
        Assert.IsNull(lastUpdate);
        Assert.AreEqual(0, session.MediaPropertiesSubscriptionCount);
        Assert.AreEqual(0, session.PlaybackInfoSubscriptionCount);
        Assert.AreEqual(0, session.TimelineSubscriptionCount);
    }

    [TestMethod]
    public async Task CycleSession_AdvancesThroughSessionsInOrder()
    {
        var a = CreateSession("a.exe", "Track A");
        var b = CreateSession("b.exe", "Track B");
        var manager = new StubMediaSessionSourceManager { Current = a, Sessions = [a, b] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();
        Assert.AreEqual("Track A", monitor.CurrentSnapshot?.Title);

        monitor.CycleSession();

        Assert.AreEqual("Track B", monitor.CurrentSnapshot?.Title);

        monitor.CycleSession();

        Assert.AreEqual("Track A", monitor.CurrentSnapshot?.Title);
    }

    [TestMethod]
    public async Task CycleSession_WithSingleSession_StaysOnSameSession()
    {
        var a = CreateSession("one.exe", "Only");
        var manager = new StubMediaSessionSourceManager { Current = a, Sessions = [a] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        monitor.CycleSession();

        Assert.AreEqual("Only", monitor.CurrentSnapshot?.Title);
    }

    [TestMethod]
    public async Task ControlMethods_RouteToTheAttachedSession()
    {
        var a = CreateSession("a.exe", "A");
        var b = CreateSession("b.exe", "B");
        var manager = new StubMediaSessionSourceManager { Current = a, Sessions = [a, b] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        monitor.Play();
        monitor.SetShuffle(true);
        monitor.SetRepeat(MediaRepeatMode.Track);
        monitor.Seek(TimeSpan.FromSeconds(90));

        Assert.AreEqual(1, a.PlayCalls);
        Assert.IsTrue(a.LastShuffle);
        Assert.AreEqual(MediaRepeatMode.Track, a.LastRepeat);
        Assert.AreEqual(TimeSpan.FromSeconds(90).Ticks, a.LastSeekTicks);

        monitor.CycleSession();
        monitor.Pause();

        Assert.AreEqual(1, b.PauseCalls);
        Assert.AreEqual(0, a.PauseCalls);
    }

    [TestMethod]
    public async Task ToggleShuffle_InvertsTheMonitorLiveState_NotACallerHeldOne()
    {
        var session = CreateSession("player.exe", "Song");
        session.PlaybackInfo!.Controls.IsShuffleEnabled = true;
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();
        Assert.IsTrue(monitor.CurrentSnapshot!.Shuffle);

        monitor.ToggleShuffle();

        Assert.AreEqual(1, session.ShuffleCalls);
        Assert.IsFalse(session.LastShuffle, "the first toggle inverts the live true state");

        // The session's shuffle state flips under the monitor (an SMTC
        // refresh no caller saw); the next toggle must invert the monitor's
        // NEW state, not the one a caller held at the first tap.
        session.PlaybackInfo.IsShuffleActive = false;
        session.RaisePlaybackInfoChanged();
        await TestWait.WaitUntilAsync(() => monitor.CurrentSnapshot is { Shuffle: false }, TimeSpan.FromSeconds(5));

        monitor.ToggleShuffle();

        Assert.AreEqual(2, session.ShuffleCalls);
        Assert.IsTrue(session.LastShuffle, "the second toggle inverts the monitor's refreshed state");
    }

    [TestMethod]
    public async Task ToggleShuffle_SettlesWhenTheSessionReportsNotShuffleable()
    {
        var session = CreateSession("player.exe", "Song");
        session.PlaybackInfo!.Controls.IsShuffleEnabled = false;
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        monitor.ToggleShuffle();

        Assert.AreEqual(0, session.ShuffleCalls, "a not-shuffleable session takes no shuffle command");
    }

    [TestMethod]
    public async Task TogglePlayPause_PausesAPlayingSession()
    {
        var session = CreateSession("player.exe", "Song");
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();
        Assert.IsTrue(monitor.CurrentSnapshot!.IsPlaying);

        monitor.TogglePlayPause();

        Assert.AreEqual(1, session.PauseCalls);
        Assert.AreEqual(0, session.PlayCalls, "a playing session pauses, not plays");
    }

    [TestMethod]
    public async Task TogglePlayPause_PlaysAPausedSession()
    {
        var session = CreateSession("player.exe", "Song");
        var info = session.PlaybackInfo!;
        info.PlaybackStatus = MediaPlaybackStatus.Paused;
        info.Controls = new MediaControlsData { IsPlayEnabled = true };
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        monitor.TogglePlayPause();

        Assert.AreEqual(1, session.PlayCalls);
        Assert.AreEqual(0, session.PauseCalls, "a paused session plays, not pauses");
    }

    [TestMethod]
    public async Task CycleRepeat_StepsTheCycleFromTheMonitorLiveState()
    {
        var session = CreateSession("player.exe", "Song");
        // CreateSession already reports AutoRepeatMode = List; the cycle
        // steps List -> Track.
        session.PlaybackInfo!.Controls.IsRepeatEnabled = true;
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();
        Assert.AreEqual(MediaRepeatMode.List, monitor.CurrentSnapshot!.Repeat);

        monitor.CycleRepeat();

        Assert.AreEqual(1, session.RepeatCalls);
        Assert.AreEqual(MediaRepeatMode.Track, session.LastRepeat, "the cycle steps from the live List state");
    }

    [TestMethod]
    public async Task SeekToRatio_SeeksAgainstTheLiveDuration()
    {
        var session = CreateSession("player.exe", "Song");
        // CreateSession's timeline ends at 180s.
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        monitor.SeekToRatio(0.5);

        Assert.AreEqual(1, session.SeekCalls);
        Assert.AreEqual(TimeSpan.FromSeconds(90).Ticks, session.LastSeekTicks, "half of the live 180s duration");
    }

    [TestMethod]
    public async Task SeekToRatio_SettlesWhenTheSessionCannotSeek()
    {
        var session = CreateSession("player.exe", "Song");
        session.PlaybackInfo!.Controls.IsPlaybackPositionEnabled = false;
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        monitor.SeekToRatio(0.5);

        Assert.AreEqual(0, session.SeekCalls, "a non-seekable session takes no seek command");
    }

    [TestMethod]
    public async Task PlaybackInfoChanged_TriggersSnapshotUpdate_WithPlaybackState()
    {
        var session = CreateSession("player.exe", "Song");
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        MediaSessionUpdate? lastUpdate = null;
        monitor.SnapshotChanged += update => lastUpdate = update;
        session.PlaybackInfo = new PlaybackInfoData
        {
            PlaybackStatus = MediaPlaybackStatus.Paused,
            IsShuffleActive = false,
            AutoRepeatMode = MediaRepeatMode.None,
            Controls = new MediaControlsData { IsPlayEnabled = true }
        };

        session.RaisePlaybackInfoChanged();

        Assert.IsNotNull(lastUpdate);
        Assert.AreEqual(MediaPlaybackStatus.Paused, monitor.CurrentSnapshot?.Status);
        Assert.IsFalse(monitor.CurrentSnapshot?.IsPlaying);
    }

    [TestMethod]
    public async Task SessionsChanged_WithActiveSession_TriggersRefresh()
    {
        var session = CreateSession("app.exe", "Before");
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        MediaSessionUpdate? lastUpdate = null;
        monitor.SnapshotChanged += update => lastUpdate = update;
        session.Properties = new MediaPropertiesData { Title = "After" };

        manager.RaiseSessionsChanged();

        Assert.IsNotNull(lastUpdate);
        Assert.AreEqual("After", monitor.CurrentSnapshot?.Title);
    }

    [TestMethod]
    public async Task DisposeAsync_UnsubscribesManagerAndSessionEvents()
    {
        var session = CreateSession("music.exe", "Track");
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        await monitor.DisposeAsync();

        Assert.AreEqual(0, manager.CurrentSessionChangedSubscriptionCount);
        Assert.AreEqual(0, manager.SessionsChangedSubscriptionCount);
        Assert.AreEqual(0, session.MediaPropertiesSubscriptionCount);
        Assert.AreEqual(0, session.PlaybackInfoSubscriptionCount);
        Assert.AreEqual(0, session.TimelineSubscriptionCount);
    }

    [TestMethod]
    public async Task SlowRefresh_CompletingAfterNewerRefresh_DoesNotOverwriteSnapshot()
    {
        var session = CreateSession("radio.exe", "Initial");
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);

        var tcsA = new TaskCompletionSource<MediaPropertiesData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcsB = new TaskCompletionSource<MediaPropertiesData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        session.PropertiesFunc = () => ++calls == 1 ? tcsA.Task : tcsB.Task;

        List<MediaSessionUpdate?> updates = [];
        monitor.SnapshotChanged += update => updates.Add(update);

        await monitor.InitializeAsync();

        session.RaiseMediaPropertiesChanged();

        tcsB.SetResult(new MediaPropertiesData { Title = "Latest" });
        await TestWait.WaitUntilAsync(() => updates.Count == 1, TimeSpan.FromSeconds(5));

        Assert.AreEqual("Latest", monitor.CurrentSnapshot?.Title);

        tcsA.SetResult(new MediaPropertiesData { Title = "Stale" });
        // Resolving the first fetch lets the stale refresh run out; the second
        // GetPlaybackInfo call is the proof it reached the version check and
        // discarded itself, so the assertions below are race-free.
        await TestWait.WaitUntilAsync(() => session.PlaybackInfoCalls == 2, TimeSpan.FromSeconds(5));

        Assert.AreEqual("Latest", monitor.CurrentSnapshot?.Title);
        Assert.AreEqual(1, updates.Count);
    }

    [TestMethod]
    public async Task TimelineResetDuringPlayback_AppendsTrackTokenToArtKey()
    {
        var session = CreateSession("player.exe", "Song");
        session.Timeline = new TimelinePropertiesData
        {
            Position = TimeSpan.FromSeconds(60),
            EndTime = TimeSpan.FromSeconds(120),
            LastUpdatedTime = DateTimeOffset.Now
        };
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        MediaSessionUpdate? update = null;
        monitor.SnapshotChanged += value => update = value;
        session.Timeline = new TimelinePropertiesData
        {
            Position = TimeSpan.FromSeconds(0.5),
            EndTime = TimeSpan.FromSeconds(120),
            LastUpdatedTime = DateTimeOffset.Now
        };

        session.RaiseTimelinePropertiesChanged();

        Assert.IsNotNull(update);
        Assert.IsTrue(update.ArtKey.EndsWith(":track2"), $"artKey was '{update.ArtKey}'");
    }

    [TestMethod]
    public void MediaSessionMonitor_Sanitize_StripsControlCharsAndCaps()
    {
        Assert.AreEqual("Hello", MediaSessionMonitor.Sanitize("Hello", "fallback"));
        Assert.AreEqual("a b", MediaSessionMonitor.Sanitize("a \u0000b", "fallback"), "control chars go, spaces stay");
        Assert.AreEqual("fallback", MediaSessionMonitor.Sanitize("", "fallback"));
        Assert.AreEqual("fallback", MediaSessionMonitor.Sanitize("\u0001\u0002", "fallback"));
        string longInput = new string('x', 500);
        Assert.AreEqual(256, MediaSessionMonitor.Sanitize(longInput, "fallback").Length, "capped at 256 chars");
    }
}
