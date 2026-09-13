using System.IO;
using ModernWigiDash.Widgets.Spotify;

namespace ModernWigiDash.Tests;

[TestClass]
public class SpotifySessionTests
{
    private const string TestClientId = "test-spotify-client-id";

    private sealed class FakeClient : SpotifyApiClient
    {
        public SpotifyTokenSet? PollToken;
        public SpotifyDeviceAuthorization? Device;
        public SpotifyTokenSet? RefreshedToken;
        public bool RefreshRejected;
        public IReadOnlyList<SpotifyPlaylist> Playlists = [];
        public int RefreshCalls { get; private set; }

        public FakeClient() : base(TestClientId) { }

        public override Task<SpotifyDeviceAuthorization> StartDeviceAuthorizationAsync(CancellationToken cancellationToken)
            => Task.FromResult(Device!);

        public override Task<SpotifyTokenSet> PollDeviceTokenAsync(SpotifyDeviceAuthorization deviceAuthorization, CancellationToken cancellationToken)
            => Task.FromResult(PollToken!);

        public override Task<SpotifyTokenSet> RefreshAsync(SpotifyTokenSet current, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            if (RefreshRejected) throw new SpotifyApiException(401, "refresh rejected");
            return Task.FromResult(RefreshedToken!);
        }

        public override Task<SpotifyNowPlaying?> GetNowPlayingAsync(string accessToken, CancellationToken cancellationToken)
            => Task.FromResult<SpotifyNowPlaying?>(null);

        public override Task<IReadOnlyList<SpotifyPlaylist>> GetPlaylistsAsync(string accessToken, CancellationToken cancellationToken)
            => Task.FromResult(Playlists);
    }

    private static SpotifyTokenSet Token(DateTimeOffset? expiresAt = null)
        => new("access-token", "refresh-token", expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), []);

    private static (SpotifySession Session, FakeClient Client, SpotifyTokenStore Store, List<Uri> Opened) CreateSession()
    {
        string storePath = Path.Combine(Path.GetTempPath(), $"wmd-spotify-session-{Guid.NewGuid():N}.bin");
        var store = new SpotifyTokenStore(storePath);
        var client = new FakeClient();
        List<Uri> opened = [];
        var session = new SpotifySession(store, _ => client, TimeProvider.System, opened.Add);
        return (session, client, store, opened);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), "wmd-spotify-session-*.bin"))
        {
            try { File.Delete(file); } catch (IOException) { /* best-effort cleanup */ }
        }
    }

    [TestMethod]
    public async Task Restore_WithNoStoredToken_ReturnsFalse()
    {
        var (session, _, store, _) = CreateSession();
        store.Delete();

        bool ok = await session.RestoreAsync(TestClientId, new TestContext(), CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.IsFalse(session.IsAuthenticated);
    }

    [TestMethod]
    public async Task Restore_WithStoredToken_IsAuthenticated()
    {
        var (session, _, store, _) = CreateSession();
        store.Save(Token());

        bool ok = await session.RestoreAsync(TestClientId, new TestContext(), CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.IsTrue(session.IsAuthenticated);
    }

    [TestMethod]
    public async Task Login_SavesTokenAndOpensBrowser()
    {
        var (session, client, store, opened) = CreateSession();
        client.Device = new SpotifyDeviceAuthorization("device", "user-code", new Uri("https://spotify.com/activate"), DateTimeOffset.UtcNow.AddMinutes(5), 5);
        client.PollToken = Token();

        await session.LoginAsync(TestClientId, new TestContext(), CancellationToken.None);

        Assert.IsTrue(session.IsAuthenticated);
        Assert.AreEqual(1, opened.Count);
        Assert.AreEqual("https://spotify.com/activate", opened[0].AbsoluteUri);
        Assert.IsNotNull(store.Load());
    }

    [TestMethod]
    public async Task EnsureAccessToken_WhenFresh_ReturnsStoredTokenWithoutRefresh()
    {
        var (session, client, store, _) = CreateSession();
        store.Save(Token(expiresAt: DateTimeOffset.UtcNow.AddHours(1)));

        string? token = await session.EnsureAccessTokenAsync(TestClientId, new TestContext(), CancellationToken.None);

        Assert.AreEqual("access-token", token);
        Assert.AreEqual(0, client.RefreshCalls);
    }

    [TestMethod]
    public async Task EnsureAccessToken_WhenExpired_RefreshesOnce()
    {
        var (session, client, store, _) = CreateSession();
        store.Save(Token(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-60)));
        client.RefreshedToken = Token(expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        string? token = await session.EnsureAccessTokenAsync(TestClientId, new TestContext(), CancellationToken.None);

        Assert.AreEqual("access-token", token);
        Assert.AreEqual(1, client.RefreshCalls);
    }

    [TestMethod]
    public async Task EnsureAccessToken_WhenRefreshRejected_ClearsSession()
    {
        var (session, client, store, _) = CreateSession();
        store.Save(Token(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-60)));
        client.RefreshRejected = true;

        string? token = await session.EnsureAccessTokenAsync(TestClientId, new TestContext(), CancellationToken.None);

        Assert.IsNull(token);
        Assert.IsFalse(session.IsAuthenticated);
        Assert.IsNull(store.Load());
    }

    [TestMethod]
    public async Task Logout_ClearsStateAndStore()
    {
        var (session, _, store, _) = CreateSession();
        store.Save(Token());
        await session.RestoreAsync(TestClientId, new TestContext(), CancellationToken.None);

        await session.LogoutAsync(CancellationToken.None);

        Assert.IsFalse(session.IsAuthenticated);
        Assert.IsNull(store.Load());
    }
}
