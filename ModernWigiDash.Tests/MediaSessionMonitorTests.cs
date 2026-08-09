using Windows.Media;
using Windows.Media.Control;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class MediaSessionMonitorTests
{
    private sealed class FakeMediaSessionSource : IMediaSessionSource
    {
        public FakeMediaSessionSourceManager? Manager { get; set; }

        public Task<IMediaSessionSourceManager?> GetManagerAsync()
            => Task.FromResult<IMediaSessionSourceManager?>(Manager);
    }

    private sealed class FakeMediaSessionSourceManager : IMediaSessionSourceManager
    {
        private Action? _currentSessionChanged;
        private Action? _sessionsChanged;

        public FakeMediaSession? Current { get; set; }

        public List<FakeMediaSession> Sessions { get; set; } = [];

        public int CurrentSessionChangedSubscriptionCount { get; private set; }

        public int SessionsChangedSubscriptionCount { get; private set; }

        public event Action? CurrentSessionChanged
        {
            add { _currentSessionChanged += value; CurrentSessionChangedSubscriptionCount++; }
            remove { _currentSessionChanged -= value; CurrentSessionChangedSubscriptionCount--; }
        }

        public event Action? SessionsChanged
        {
            add { _sessionsChanged += value; SessionsChangedSubscriptionCount++; }
            remove { _sessionsChanged -= value; SessionsChangedSubscriptionCount--; }
        }

        public IMediaSessionSourceSession? GetCurrentSession() => Current;

        public IReadOnlyList<IMediaSessionSourceSession> GetSessions() => Sessions;

        public void RaiseCurrentSessionChanged() => _currentSessionChanged?.Invoke();

        public void RaiseSessionsChanged() => _sessionsChanged?.Invoke();
    }

    private sealed class FakeMediaSession : IMediaSessionSourceSession
    {
        private Action? _mediaPropertiesChanged;
        private Action? _playbackInfoChanged;
        private Action? _timelinePropertiesChanged;

        public object Identity => this;

        public string SourceAppUserModelId { get; set; } = "fake.app";

        public MediaPropertiesData? Properties { get; set; }

        public PlaybackInfoData? PlaybackInfo { get; set; }

        public TimelinePropertiesData? Timeline { get; set; }

        public Func<Task<MediaPropertiesData?>>? PropertiesFunc { get; set; }

        public int MediaPropertiesSubscriptionCount { get; private set; }

        public int PlaybackInfoSubscriptionCount { get; private set; }

        public int TimelineSubscriptionCount { get; private set; }

        public int PlayCalls { get; private set; }

        public int PauseCalls { get; private set; }

        public int NextCalls { get; private set; }

        public int PreviousCalls { get; private set; }

        public int ShuffleCalls { get; private set; }

        public int RepeatCalls { get; private set; }

        public int SeekCalls { get; private set; }

        public bool LastShuffle { get; private set; }

        public MediaPlaybackAutoRepeatMode LastRepeat { get; private set; }

        public long LastSeekTicks { get; private set; }

        public event Action? MediaPropertiesChanged
        {
            add { _mediaPropertiesChanged += value; MediaPropertiesSubscriptionCount++; }
            remove { _mediaPropertiesChanged -= value; MediaPropertiesSubscriptionCount--; }
        }

        public event Action? PlaybackInfoChanged
        {
            add { _playbackInfoChanged += value; PlaybackInfoSubscriptionCount++; }
            remove { _playbackInfoChanged -= value; PlaybackInfoSubscriptionCount--; }
        }

        public event Action? TimelinePropertiesChanged
        {
            add { _timelinePropertiesChanged += value; TimelineSubscriptionCount++; }
            remove { _timelinePropertiesChanged -= value; TimelineSubscriptionCount--; }
        }

        public Task<MediaPropertiesData?> TryGetMediaPropertiesAsync()
            => PropertiesFunc is not null ? PropertiesFunc() : Task.FromResult(Properties);

        public PlaybackInfoData? GetPlaybackInfo() => PlaybackInfo;

        public TimelinePropertiesData? GetTimelineProperties() => Timeline;

        public Task<bool> TryPlayAsync() { PlayCalls++; return Task.FromResult(true); }

        public Task<bool> TryPauseAsync() { PauseCalls++; return Task.FromResult(true); }

        public Task<bool> TrySkipNextAsync() { NextCalls++; return Task.FromResult(true); }

        public Task<bool> TrySkipPreviousAsync() { PreviousCalls++; return Task.FromResult(true); }

        public Task<bool> TryChangeShuffleActiveAsync(bool shuffle)
        {
            ShuffleCalls++;
            LastShuffle = shuffle;
            return Task.FromResult(true);
        }

        public Task<bool> TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode mode)
        {
            RepeatCalls++;
            LastRepeat = mode;
            return Task.FromResult(true);
        }

        public Task<bool> TryChangePlaybackPositionAsync(long positionTicks)
        {
            SeekCalls++;
            LastSeekTicks = positionTicks;
            return Task.FromResult(true);
        }

        public void RaiseMediaPropertiesChanged() => _mediaPropertiesChanged?.Invoke();

        public void RaisePlaybackInfoChanged() => _playbackInfoChanged?.Invoke();

        public void RaiseTimelinePropertiesChanged() => _timelinePropertiesChanged?.Invoke();
    }

    private static FakeMediaSession CreateSession(string appId, string title)
    {
        return new FakeMediaSession
        {
            SourceAppUserModelId = appId,
            Properties = new MediaPropertiesData { Title = title, Artist = "Artist", AlbumTitle = "Album" },
            PlaybackInfo = new PlaybackInfoData
            {
                PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                IsShuffleActive = true,
                AutoRepeatMode = MediaPlaybackAutoRepeatMode.List,
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

    private static MediaSessionMonitor CreateMonitor(FakeMediaSessionSourceManager manager)
        => new MediaSessionMonitor(new FakeMediaSessionSource { Manager = manager });

    [TestMethod]
    public async Task InitializeAsync_WithManagerAndSession_SubscribesToAllEventsAndBuildsInitialSnapshot()
    {
        var session = CreateSession("spotify.exe", "Song A");
        var manager = new FakeMediaSessionSourceManager { Current = session, Sessions = [session] };
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
        Assert.AreEqual(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, snap.Status);
        Assert.IsTrue(snap.Shuffle);
        Assert.AreEqual(MediaPlaybackAutoRepeatMode.List, snap.Repeat);
        Assert.AreEqual(TimeSpan.FromSeconds(30), snap.Position);
        Assert.AreEqual(TimeSpan.FromSeconds(180), snap.Duration);
        Assert.IsTrue(snap.CanPause);
        Assert.IsTrue(snap.CanNext);
        Assert.IsTrue(snap.IsPlaying);
    }

    [TestMethod]
    public async Task InitializeAsync_WhenSourceReturnsNullManager_CompletesWithoutSnapshotOrEvents()
    {
        var monitor = new MediaSessionMonitor(new FakeMediaSessionSource { Manager = null });

        await monitor.InitializeAsync();

        Assert.IsNull(monitor.CurrentSnapshot);
        await monitor.DisposeAsync();
    }

    [TestMethod]
    public async Task MediaPropertiesChanged_TriggersSnapshotUpdate_WithCorrectMediaPropertiesAndArtKey()
    {
        var session = CreateSession("browser.exe", "Old Title");
        var manager = new FakeMediaSessionSourceManager { Current = session, Sessions = [session] };
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
        var manager = new FakeMediaSessionSourceManager { Current = session, Sessions = [session] };
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
        var manager = new FakeMediaSessionSourceManager { Current = a, Sessions = [a, b] };
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
        var manager = new FakeMediaSessionSourceManager { Current = a, Sessions = [a] };
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
        var manager = new FakeMediaSessionSourceManager { Current = a, Sessions = [a, b] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        monitor.Play();
        monitor.SetShuffle(true);
        monitor.SetRepeat(MediaPlaybackAutoRepeatMode.Track);
        monitor.Seek(TimeSpan.FromSeconds(90));

        Assert.AreEqual(1, a.PlayCalls);
        Assert.IsTrue(a.LastShuffle);
        Assert.AreEqual(MediaPlaybackAutoRepeatMode.Track, a.LastRepeat);
        Assert.AreEqual(TimeSpan.FromSeconds(90).Ticks, a.LastSeekTicks);

        monitor.CycleSession();
        monitor.Pause();

        Assert.AreEqual(1, b.PauseCalls);
        Assert.AreEqual(0, a.PauseCalls);
    }

    [TestMethod]
    public async Task PlaybackInfoChanged_TriggersSnapshotUpdate_WithPlaybackState()
    {
        var session = CreateSession("player.exe", "Song");
        var manager = new FakeMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);
        await monitor.InitializeAsync();

        MediaSessionUpdate? lastUpdate = null;
        monitor.SnapshotChanged += update => lastUpdate = update;
        session.PlaybackInfo = new PlaybackInfoData
        {
            PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            IsShuffleActive = false,
            AutoRepeatMode = MediaPlaybackAutoRepeatMode.None,
            Controls = new MediaControlsData { IsPlayEnabled = true }
        };

        session.RaisePlaybackInfoChanged();

        Assert.IsNotNull(lastUpdate);
        Assert.AreEqual(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, monitor.CurrentSnapshot?.Status);
        Assert.IsFalse(monitor.CurrentSnapshot?.IsPlaying);
    }

    [TestMethod]
    public async Task SessionsChanged_WithActiveSession_TriggersRefresh()
    {
        var session = CreateSession("app.exe", "Before");
        var manager = new FakeMediaSessionSourceManager { Current = session, Sessions = [session] };
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
        var manager = new FakeMediaSessionSourceManager { Current = session, Sessions = [session] };
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
        var manager = new FakeMediaSessionSourceManager { Current = session, Sessions = [session] };
        var monitor = CreateMonitor(manager);

        var tcsA = new TaskCompletionSource<MediaPropertiesData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcsB = new TaskCompletionSource<MediaPropertiesData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        session.PropertiesFunc = () => ++calls == 1 ? tcsA.Task : tcsB.Task;

        var updates = new List<MediaSessionUpdate?>();
        monitor.SnapshotChanged += update => updates.Add(update);

        await monitor.InitializeAsync();

        session.RaiseMediaPropertiesChanged();

        tcsB.SetResult(new MediaPropertiesData { Title = "Latest" });
        await TestWait.WaitUntilAsync(() => updates.Count == 1, TimeSpan.FromSeconds(5));

        Assert.AreEqual("Latest", monitor.CurrentSnapshot?.Title);

        tcsA.SetResult(new MediaPropertiesData { Title = "Stale" });
        await Task.Delay(100);

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
        var manager = new FakeMediaSessionSourceManager { Current = session, Sessions = [session] };
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
}
