using System.IO;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

[TestClass]
public class TwitchSessionTests
{
    private const string TestClientId = "test-client-id";

    private sealed class FakeContext : IModernWigiDashContext
    {
        public int AuthShown { get; private set; }
        public int AuthClosed { get; private set; }
        public List<string> Errors { get; } = [];

        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) => Errors.Add(message);
        public void RequestRender() { }
        public void RequestInspectorRefresh() { }
        public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt) => AuthShown++;
        public void CloseDeviceAuthorization() => AuthClosed++;
    }

    private sealed class FakeClient : TwitchApiClient
    {
        public TwitchTokenValidation? ValidationResult;
        public bool ValidationUnauthorized;
        public bool RefreshRejected;
        public TwitchTokenSet? RefreshedToken;
        public TwitchTokenSet? PollToken;
        public TwitchDeviceAuthorization? Device;
        public IReadOnlyList<TwitchFollowedChannel> Channels = [];
        public int ValidateCalls { get; private set; }
        public int RevokeCalls { get; private set; }

        public FakeClient() : base(TestClientId) { }

        public override Task<TwitchDeviceAuthorization> StartDeviceAuthorizationAsync(CancellationToken cancellationToken)
            => Task.FromResult(Device!);

        public override Task<TwitchTokenSet> PollDeviceTokenAsync(TwitchDeviceAuthorization deviceAuthorization, CancellationToken cancellationToken)
            => Task.FromResult(PollToken!);

        public override Task<TwitchTokenValidation> ValidateAsync(string accessToken, CancellationToken cancellationToken)
        {
            ValidateCalls++;
            // Only the STALE token is unauthorized — the refreshed token validates.
            if (ValidationUnauthorized && accessToken == "access-token") throw new TwitchApiException(401, "unauthorized");
            return Task.FromResult(ValidationResult!);
        }

        public override Task<TwitchTokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            if (RefreshRejected) throw new TwitchApiException(400, "refresh rejected");
            return Task.FromResult(RefreshedToken!);
        }

        public override Task<IReadOnlyList<TwitchFollowedChannel>> GetFollowedLiveChannelsAsync(string accessToken, string userId, CancellationToken cancellationToken)
            => Task.FromResult(Channels);

        public override Task RevokeAsync(string accessToken, CancellationToken cancellationToken)
        {
            RevokeCalls++;
            return Task.CompletedTask;
        }
    }

    private static TwitchTokenSet Token(string clientId = TestClientId, string accessToken = "access-token") => new(clientId, accessToken, "refresh-token", DateTimeOffset.UtcNow.AddHours(1), []);

    private static TwitchTokenValidation Validation(string clientId = TestClientId) => new(clientId, "user-1", "viewer", 3600, []);

    private static (TwitchSession Session, FakeClient Client, TwitchTokenStore Store, string StorePath) CreateSession()
    {
        string storePath = Path.Combine(Path.GetTempPath(), $"wmd-twitch-{Guid.NewGuid():N}.bin");
        var store = new TwitchTokenStore(storePath);
        var client = new FakeClient { ValidationResult = Validation() };
        var session = new TwitchSession(store, _ => client, TimeProvider.System);
        return (session, client, store, storePath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Never leave token files in the temp dir.
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), "wmd-twitch-*.bin"))
        {
            try { File.Delete(file); } catch (IOException) { /* best-effort cleanup */ }
        }
    }

    [TestMethod]
    public async Task Restore_WithStoredToken_ValidatesAndLoadsChannels()
    {
        var (session, client, store, _) = CreateSession();
        var context = new FakeContext();
        store.Save(Token());
        client.Channels = [new TwitchFollowedChannel("b1", "streamer", "Streamer")];

        bool ok = await session.RestoreAsync(TestClientId, context, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.IsTrue(session.IsAuthenticated);
        Assert.AreEqual(1, session.FollowedChannels.Count);
        Assert.AreEqual("Streamer", session.FollowedChannels[0].DisplayName);
    }

    [TestMethod]
    public async Task Restore_UnauthorizedToken_RefreshesAndAuthenticates()
    {
        var (session, client, store, _) = CreateSession();
        var context = new FakeContext();
        store.Save(Token());
        client.ValidationUnauthorized = true;
        client.RefreshedToken = Token(accessToken: "refreshed-access");
        client.Channels = [new TwitchFollowedChannel("b1", "streamer", "Streamer")];

        bool ok = await session.RestoreAsync(TestClientId, context, CancellationToken.None);

        Assert.IsTrue(ok, "An unauthorized token must trigger a refresh instead of failing");
        Assert.IsTrue(session.IsAuthenticated);
        Assert.IsTrue(client.ValidateCalls >= 2, "Validate must run again after the refresh");
    }

    [TestMethod]
    public async Task Restore_RefreshRejected_ClearsStateAndStore()
    {
        var (session, client, store, _) = CreateSession();
        var context = new FakeContext();
        store.Save(Token());
        client.ValidationUnauthorized = true;
        client.RefreshRejected = true;

        bool ok = await session.RestoreAsync(TestClientId, context, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.IsFalse(session.IsAuthenticated);
        Assert.IsNull(store.Load(), "A rejected refresh must delete the stored token");
    }

    [TestMethod]
    public async Task Login_DeviceFlow_AuthenticatesAndClosesAuthDialog()
    {
        var (session, client, _, _) = CreateSession();
        var context = new FakeContext();
        client.Device = new TwitchDeviceAuthorization("device-code", "USER-CODE", new Uri("https://twitch.tv/activate"), DateTimeOffset.UtcNow.AddMinutes(5), 5);
        client.PollToken = Token();
        client.Channels = [new TwitchFollowedChannel("b1", "streamer", "Streamer")];

        await session.LoginAsync(TestClientId, context, CancellationToken.None);

        Assert.IsTrue(session.IsAuthenticated);
        Assert.AreEqual(1, context.AuthShown);
        Assert.AreEqual(1, context.AuthClosed, "The auth dialog must close after the flow completes");
        Assert.AreEqual(1, session.FollowedChannels.Count);
    }

    [TestMethod]
    public async Task Login_TestClientIdMismatch_Throws()
    {
        var (session, client, _, _) = CreateSession();
        var context = new FakeContext();
        client.Device = new TwitchDeviceAuthorization("device-code", "USER-CODE", new Uri("https://twitch.tv/activate"), DateTimeOffset.UtcNow.AddMinutes(5), 5);
        client.PollToken = Token();
        client.ValidationResult = Validation("different-client-id");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.LoginAsync(TestClientId, context, CancellationToken.None));
        Assert.IsFalse(session.IsAuthenticated);
    }

    [TestMethod]
    public async Task Logout_RevokesTokenAndClearsState()
    {
        var (session, client, store, _) = CreateSession();
        var context = new FakeContext();
        client.Device = new TwitchDeviceAuthorization("device-code", "USER-CODE", new Uri("https://twitch.tv/activate"), DateTimeOffset.UtcNow.AddMinutes(5), 5);
        client.PollToken = Token();
        await session.LoginAsync(TestClientId, context, CancellationToken.None);

        await session.LogoutAsync(CancellationToken.None);

        Assert.IsFalse(session.IsAuthenticated);
        Assert.AreEqual(1, client.RevokeCalls, "Logout must revoke the access token");
        Assert.IsNull(store.Load(), "Logout must delete the stored token");
    }

    [TestMethod]
    public async Task Restore_WrongTestClientId_IsRefused()
    {
        var (session, client, store, _) = CreateSession();
        var context = new FakeContext();
        store.Save(Token("other-client-id"));

        bool ok = await session.RestoreAsync(TestClientId, context, CancellationToken.None);

        Assert.IsFalse(ok, "A token bound to a different client id must not be restored");
        Assert.IsFalse(session.IsAuthenticated);
    }
}
