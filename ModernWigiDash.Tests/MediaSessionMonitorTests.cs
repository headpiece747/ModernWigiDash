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
    public async Task CycleSession_WithDistinctManagedObjectsMatchingAppId_CyclesBackAndForth()
    {
        // Models production C#/WinRT where GetSessions() returns fresh RCW wrappers
        // whose Identity objects are not ReferenceEqual, but SourceAppUserModelId matches.
        var a = CreateSession("chrome.exe", "YouTube Video");
        var b = CreateSession("foobar2000.exe", "Local Song");
        var manager = new StubMediaSessionSourceManager { Current = a, Sessions = [a, b] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();
        Assert.AreEqual("YouTube Video", monitor.CurrentSnapshot?.Title);

        monitor.CycleSession();
        Assert.AreEqual("Local Song", monitor.CurrentSnapshot?.Title);

        monitor.CycleSession();
        Assert.AreEqual("YouTube Video", monitor.CurrentSnapshot?.Title);
    }

    [TestMethod]
    public async Task CycleSession_WithMultipleSessionsSharingSameAppUserModelId_CyclesBetweenThem()
    {
        // Two browser tabs or windows share the exact same SourceAppUserModelId ("chrome.exe")
        // but have distinct session objects. Cycling must cycle between them rather than aborting.
        var a = CreateSession("chrome.exe", "YouTube Video");
        var b = CreateSession("chrome.exe", "SoundCloud Track");
        var manager = new StubMediaSessionSourceManager { Current = a, Sessions = [a, b] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();
        Assert.AreEqual("YouTube Video", monitor.CurrentSnapshot?.Title);

        monitor.CycleSession();
        Assert.AreEqual("SoundCloud Track", monitor.CurrentSnapshot?.Title, "Cycle must advance to the second session even when AppUserModelId is identical");

        monitor.CycleSession();
        Assert.AreEqual("YouTube Video", monitor.CurrentSnapshot?.Title, "Cycle must wrap back to the first session");
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
    public async Task DisposeAsync_DisposesTheManagerAndSessionAdapters()
    {
        var session = CreateSession("music.exe", "Track");
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        await monitor.DisposeAsync();

        // The neutral unsubscription above is only half the release: the
        // adapters own the WinRT event subscriptions, and the monitor must
        // dispose them so the WinRT manager/session do not outlive it.
        Assert.AreEqual(1, manager.DisposalCount, "the held manager adapter is disposed on dispose");
        Assert.AreEqual(1, session.DisposalCount, "the held session adapter is disposed on dispose");
    }

    [TestMethod]
    public async Task AttachSession_ReplacingDisposesTheReleasedSession()
    {
        var a = CreateSession("a.exe", "Track A");
        var b = CreateSession("b.exe", "Track B");
        var manager = new StubMediaSessionSourceManager { Current = a, Sessions = [a, b] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();
        Assert.AreEqual(0, a.DisposalCount);
        Assert.AreEqual(0, b.DisposalCount);

        monitor.CycleSession();

        // Advancing to b releases the adapter the monitor held on a: its
        // WinRT subscriptions are torn down, so the WinRT session does not
        // accumulate a dead handler. The newly held session is not disposed.
        Assert.AreEqual(1, a.DisposalCount, "the released session adapter is disposed on replacement");
        Assert.AreEqual(0, b.DisposalCount, "the newly held session adapter is not disposed");
    }

    [TestMethod]
    public async Task CycleSession_FreshWrapperSeam_DisposesTheDepartingWrapper()
    {
        var a = CreateSession("a.exe", "Track A");
        var b = CreateSession("b.exe", "Track B");
        var manager = new StubMediaSessionSourceManager { Current = a, Sessions = [a, b] };
        List<FreshSessionWrapper> wrappers = [];
        // The production WinRT seam builds a fresh adapter per session per
        // GetSessions() call: model it so the held adapter and the returned
        // wrappers are different instances for the same session (the shape
        // the same-instance stub cannot represent).
        manager.FreshWrapperFor = s =>
        {
            var wrapper = new FreshSessionWrapper(s);
            wrappers.Add(wrapper);
            return wrapper;
        };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();
        Assert.AreEqual(0, wrappers.Count, "init attaches through GetCurrentSession, not the cycle's fresh wrappers");

        monitor.CycleSession();

        // Advancing to b: the fresh wrapper of the departing session a must
        // be released (a cycle that only releases what AttachSession releases
        // would leave it rooted with live subscriptions), the arriving
        // wrapper is kept, and the held adapter is released exactly once.
        Assert.AreEqual(2, wrappers.Count);
        Assert.AreEqual(1, wrappers[0].DisposalCount, "the departing session's fresh wrapper is disposed by the cycle");
        Assert.AreEqual(0, wrappers[1].DisposalCount, "the arriving session's fresh wrapper is held, not disposed");
        Assert.AreEqual(1, a.DisposalCount, "the held adapter is released by the attach, not double-disposed");
        Assert.AreEqual(1, b.MediaPropertiesSubscriptionCount, "the monitor attached the arriving wrapper's events");
        Assert.AreEqual(0, a.MediaPropertiesSubscriptionCount, "the monitor detached the released adapter's events");
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

    [TestMethod]
    public async Task DisposeAsync_WhileInitializeAsyncIsPending_DisposesLateManagerAndAttachesNoSession()
    {
        var session = CreateSession("spotify.exe", "Playing");
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        var pendingManager = new TaskCompletionSource<IMediaSessionSourceManager?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new StubMediaSessionSource { GetManagerAsyncOverride = () => pendingManager.Task };
        var monitor = new MediaSessionMonitor(source);

        Task initTask = monitor.InitializeAsync();
        await monitor.DisposeAsync();
        pendingManager.SetResult(manager);
        await initTask;

        Assert.AreEqual(1, manager.DisposalCount, "manager resolved after DisposeAsync must still be disposed");
        Assert.AreEqual(0, manager.CurrentSessionChangedSubscriptionCount);
        Assert.AreEqual(0, manager.SessionsChangedSubscriptionCount);
        Assert.AreEqual(0, session.MediaPropertiesSubscriptionCount);
        Assert.IsNull(monitor.CurrentSnapshot);
    }

    [TestMethod]
    public async Task CycleSession_WithSingleSessionFreshWrapper_DisposesUnusedWrapper()
    {
        var a = CreateSession("one.exe", "Only");
        var manager = new StubMediaSessionSourceManager { Current = a, Sessions = [a] };
        List<FreshSessionWrapper> wrappers = [];
        manager.FreshWrapperFor = s =>
        {
            var wrapper = new FreshSessionWrapper(s);
            wrappers.Add(wrapper);
            return wrapper;
        };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        monitor.CycleSession();

        Assert.AreEqual(1, wrappers.Count);
        Assert.AreEqual(1, wrappers[0].DisposalCount, "single-session cycle must dispose its transient fresh wrapper");
        Assert.AreEqual(0, a.DisposalCount);
    }

    [TestMethod]
    public async Task Callbacks_WhenManagerOrControlThrows_LogErrorWithoutCrashing()
    {
        var session = CreateSession("player.exe", "Song");
        session.ControlActionOverride = () => throw new InvalidOperationException("COM control error");
        var manager = new StubMediaSessionSourceManager { Current = session, Sessions = [session] };
        List<string> logged = [];
        var monitor = new MediaSessionMonitor(new StubMediaSessionSource { Manager = manager }, (msg, _) => logged.Add(msg));
        await monitor.InitializeAsync();

        manager.GetCurrentSessionOverride = () => throw new InvalidOperationException("COM current error");
        manager.GetSessionsOverride = () => throw new InvalidOperationException("COM sessions error");

        manager.RaiseCurrentSessionChanged();
        monitor.CycleSession();
        manager.GetCurrentSessionOverride = () => null;
        manager.RaiseCurrentSessionChanged(); // detaches session
        manager.GetCurrentSessionOverride = () => throw new InvalidOperationException("COM sessions lookup error");
        manager.RaiseSessionsChanged();

        // Reattach session to exercise failing control command cleanly.
        manager.GetCurrentSessionOverride = () => session;
        manager.RaiseCurrentSessionChanged();
        int loggedBeforeControl = logged.Count;
        monitor.Play();
        await Task.Yield();

        Assert.IsGreaterThanOrEqualTo(3, loggedBeforeControl, $"Expected callback errors logged, saw {loggedBeforeControl}");
        Assert.AreEqual(loggedBeforeControl + 1, logged.Count, $"Expected control error logged, saw {logged.Count - loggedBeforeControl}");
    }

    [TestMethod]
    public async Task ConcurrentDisposeAndCallbacks_LeavesCleanDisposedStateWithZeroLeaks()
    {
        for (int i = 0; i < 50; i++)
        {
            var a = CreateSession("a.exe", "Track A");
            var b = CreateSession("b.exe", "Track B");
            var manager = new StubMediaSessionSourceManager { Current = a, Sessions = [a, b] };
            manager.FreshWrapperFor = s => new FreshSessionWrapper(s);
            var monitor = new MediaSessionMonitor(new StubMediaSessionSource { Manager = manager });
            using var gate = new ManualResetEventSlim(false);

            Task init = Task.Run(() => { gate.Wait(); return monitor.InitializeAsync(); });
            Task cb1 = Task.Run(() =>
            {
                gate.Wait();
                manager.Current = b;
                manager.RaiseCurrentSessionChanged();
                manager.RaiseSessionsChanged();
            });
            Task cb2 = Task.Run(() =>
            {
                gate.Wait();
                monitor.CycleSession();
                monitor.Play();
            });
            Task dispose = Task.Run(async () =>
            {
                gate.Wait();
                await monitor.DisposeAsync().ConfigureAwait(false);
            });

            gate.Set();
            await Task.WhenAll(init, cb1, cb2, dispose);

            Assert.IsNull(monitor.CurrentSnapshot);
            Assert.AreEqual(0, manager.CurrentSessionChangedSubscriptionCount);
            Assert.AreEqual(0, manager.SessionsChangedSubscriptionCount);
            Assert.AreEqual(0, a.MediaPropertiesSubscriptionCount);
            Assert.AreEqual(0, a.PlaybackInfoSubscriptionCount);
            Assert.AreEqual(0, a.TimelineSubscriptionCount);
            Assert.AreEqual(0, b.MediaPropertiesSubscriptionCount);
            Assert.AreEqual(0, b.PlaybackInfoSubscriptionCount);
            Assert.AreEqual(0, b.TimelineSubscriptionCount);
        }
    }

    [TestMethod]
    public async Task SessionEvents_WhenAttachOrDetachThrows_LogsAndDisposesWithoutCrashing()
    {
        var badAdd = new ThrowingEventSession(throwOnAdd: true, throwOnRemove: false);
        var manager = new StubMediaSessionSourceManager
        {
            GetCurrentSessionOverride = () => badAdd,
            GetSessionsOverride = () => [badAdd],
        };
        var monitor = CreateMonitor(manager);

        await monitor.InitializeAsync();
        Assert.AreEqual(1, badAdd.DisposalCount, "session that fails += subscription must be disposed immediately");
        Assert.IsFalse(badAdd.HasSubscribers);
        Assert.IsNull(monitor.CurrentSnapshot);
        await monitor.DisposeAsync();

        var badRemove = new ThrowingEventSession(throwOnAdd: false, throwOnRemove: true);
        var manager2 = new StubMediaSessionSourceManager
        {
            GetCurrentSessionOverride = () => badRemove,
            GetSessionsOverride = () => [badRemove],
        };
        var monitor2 = CreateMonitor(manager2);

        await monitor2.InitializeAsync();
        await monitor2.DisposeAsync();
        Assert.AreEqual(1, badRemove.DisposalCount, "throwing on -= must still run session disposal");

        var badMgr = new ThrowingRemoveManager();
        var monitor3 = new MediaSessionMonitor(badMgr);
        await monitor3.InitializeAsync();
        await monitor3.DisposeAsync();
        Assert.AreEqual(1, badMgr.DisposalCount, "throwing on manager -= must detach remaining events and dispose manager");
        Assert.AreEqual(2, badMgr.DetachAttempts, "both manager events must attempt detach independently");
        Assert.IsFalse(badMgr.HasSubscribers);
    }

    private sealed class ThrowingRemoveManager : IMediaSessionSource, IMediaSessionSourceManager
    {
        private Action? _handlers;
        private int _detachAttempts;
        private int _disposalCount;

        public int DetachAttempts => Volatile.Read(ref _detachAttempts);
        public int DisposalCount => Volatile.Read(ref _disposalCount);
        public bool HasSubscribers => Volatile.Read(ref _handlers) is not null;

        public Task<IMediaSessionSourceManager?> GetManagerAsync() => Task.FromResult<IMediaSessionSourceManager?>(this);

        public event Action? CurrentSessionChanged
        {
            add { _handlers += value; }
            remove
            {
                Interlocked.Increment(ref _detachAttempts);
                _handlers -= value;
                throw new InvalidOperationException("CurrentSessionChanged detach failed");
            }
        }

        public event Action? SessionsChanged
        {
            add { _handlers += value; }
            remove
            {
                Interlocked.Increment(ref _detachAttempts);
                _handlers -= value;
                throw new InvalidOperationException("SessionsChanged detach failed");
            }
        }

        public IMediaSessionSourceSession? GetCurrentSession() => null;
        public IReadOnlyList<IMediaSessionSourceSession> GetSessions() => [];
        public void Dispose() => Interlocked.Increment(ref _disposalCount);
    }

    private sealed class ThrowingEventSession(bool throwOnAdd, bool throwOnRemove) : IMediaSessionSourceSession
    {
        private Action? _handlers;
        private int _disposalCount;

        public object Identity => this;
        public string SourceAppUserModelId => "broken.app";
        public int DisposalCount => Volatile.Read(ref _disposalCount);
        public bool HasSubscribers => Volatile.Read(ref _handlers) is not null;

        public event Action? MediaPropertiesChanged
        {
            add
            {
                if (throwOnAdd) throw new InvalidOperationException("add failed");
                _handlers += value;
            }
            remove
            {
                if (throwOnRemove) throw new InvalidOperationException("remove failed");
                _handlers -= value;
            }
        }

        public event Action? PlaybackInfoChanged
        {
            add
            {
                if (throwOnAdd) throw new InvalidOperationException("add failed");
                _handlers += value;
            }
            remove
            {
                if (throwOnRemove) throw new InvalidOperationException("remove failed");
                _handlers -= value;
            }
        }

        public event Action? TimelinePropertiesChanged
        {
            add
            {
                if (throwOnAdd) throw new InvalidOperationException("add failed");
                _handlers += value;
            }
            remove
            {
                if (throwOnRemove) throw new InvalidOperationException("remove failed");
                _handlers -= value;
            }
        }

        public void Dispose() => Interlocked.Increment(ref _disposalCount);
        public Task<MediaPropertiesData?> TryGetMediaPropertiesAsync() => Task.FromResult<MediaPropertiesData?>(new MediaPropertiesData { Title = "Track" });
        public PlaybackInfoData? GetPlaybackInfo() => new() { PlaybackStatus = MediaPlaybackStatus.Playing };
        public TimelinePropertiesData? GetTimelineProperties() => new();
        public Task<bool> TryPlayAsync() => Task.FromResult(true);
        public Task<bool> TryPauseAsync() => Task.FromResult(true);
        public Task<bool> TrySkipNextAsync() => Task.FromResult(true);
        public Task<bool> TrySkipPreviousAsync() => Task.FromResult(true);
        public Task<bool> TryChangeShuffleActiveAsync(bool shuffle) => Task.FromResult(true);
        public Task<bool> TryChangeAutoRepeatModeAsync(MediaRepeatMode mode) => Task.FromResult(true);
        public Task<bool> TryChangePlaybackPositionAsync(long positionTicks) => Task.FromResult(true);
    }
}
