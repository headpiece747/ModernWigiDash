using System.IO;
using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

[TestClass]
public class TwitchSessionTests
{
    private const string TestClientId = "test-client-id";

    private sealed class FakeClient : TwitchApiClient
    {
        public TwitchTokenValidation? ValidationResult;
        public bool ValidationUnauthorized;
        public bool RefreshRejected;
        // When set, ValidateAsync parks on this task — the "hung network" the
        // validation-gate tests drive (the tick's network call holds no gate).
        public TaskCompletionSource? ValidatePark;
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

        public override async Task<TwitchTokenValidation> ValidateAsync(string accessToken, CancellationToken cancellationToken)
        {
            ValidateCalls++;
            if (ValidatePark is { } park) await park.Task.ConfigureAwait(false);
            // Only the STALE token is unauthorized — the refreshed token validates.
            if (ValidationUnauthorized && accessToken == "access-token") throw new TwitchApiException(401, "unauthorized");
            return ValidationResult!;
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

    private static TwitchTokenSet Token(
        string clientId = TestClientId,
        string accessToken = "access-token",
        DateTimeOffset? expiresAt = null)
        => new(clientId, accessToken, "refresh-token", expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), []);

    private static TwitchTokenValidation Validation(string clientId = TestClientId) => new(clientId, "user-1", "viewer", 3600, []);

    /// <summary>
    /// The client id the tick's resolver would pick on this machine: the tick
    /// (like the production loop) resolves with a null configured id, so the
    /// environment variable wins when set — the test token must carry that
    /// id, or the mismatch verdict clears the session before the verdict
    /// under test. Mirrors the resolver's trim/whitespace rule.
    /// </summary>
    private static string MachineClientId()
    {
        string? env = Environment.GetEnvironmentVariable("MODERNWIGIDASH_TWITCH_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(env)) return TestClientId;
        return env.Trim();
    }

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
        var context = new TestContext();
        store.Save(Token());
        client.Channels = [new TwitchFollowedChannel("streamer", "Streamer")];

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
        var context = new TestContext();
        store.Save(Token());
        client.ValidationUnauthorized = true;
        client.RefreshedToken = Token(accessToken: "refreshed-access");
        client.Channels = [new TwitchFollowedChannel("streamer", "Streamer")];

        bool ok = await session.RestoreAsync(TestClientId, context, CancellationToken.None);

        Assert.IsTrue(ok, "An unauthorized token must trigger a refresh instead of failing");
        Assert.IsTrue(session.IsAuthenticated);
        Assert.IsTrue(client.ValidateCalls >= 2, "Validate must run again after the refresh");
    }

    [TestMethod]
    public async Task Restore_RefreshRejected_ClearsStateAndStore()
    {
        var (session, client, store, _) = CreateSession();
        var context = new TestContext();
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
        var context = new TestContext();
        client.Device = new TwitchDeviceAuthorization("device-code", "USER-CODE", new Uri("https://twitch.tv/activate"), DateTimeOffset.UtcNow.AddMinutes(5), 5);
        client.PollToken = Token();
        client.Channels = [new TwitchFollowedChannel("streamer", "Streamer")];

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
        var context = new TestContext();
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
        var context = new TestContext();
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
        var (session, _, store, _) = CreateSession();
        var context = new TestContext();
        store.Save(Token("other-client-id"));

        bool ok = await session.RestoreAsync(TestClientId, context, CancellationToken.None);

        Assert.IsFalse(ok, "A token bound to a different client id must not be restored");
        Assert.IsFalse(session.IsAuthenticated);
    }

    [TestMethod]
    public async Task ValidateTick_ValidToken_StampIsClockPlusExpiresIn()
    {
        string storePath = Path.Combine(Path.GetTempPath(), $"wmd-twitch-{Guid.NewGuid():N}.bin");
        var store = new TwitchTokenStore(storePath);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var client = new FakeClient { ValidationResult = new TwitchTokenValidation(TestClientId, "user-1", "viewer", 3600, ["chat:read"]) };
        var session = new TwitchSession(store, _ => client, clock);
        var context = new TestContext();
        store.Save(Token(clientId: MachineClientId(), expiresAt: clock.GetUtcNow().AddSeconds(10)));

        bool kept = await session.ValidateTickAsync(context, CancellationToken.None);

        Assert.IsTrue(kept, "A valid token keeps the monitor running");
        Assert.IsTrue(session.IsAuthenticated);
        Assert.IsTrue(client.ValidateCalls == 1);
        Assert.AreEqual(clock.GetUtcNow().AddSeconds(3600), store.Load()!.ExpiresAt,
            "The stamp is the clock's now plus the server's ExpiresIn");
        CollectionAssert.AreEqual(new[] { "chat:read" }, store.Load()!.Scopes,
            "The scopes come from the validation response");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-5)]
    public async Task ValidateTick_NonPositiveExpiresIn_ClampsToOneSecond(int expiresIn)
    {
        string storePath = Path.Combine(Path.GetTempPath(), $"wmd-twitch-{Guid.NewGuid():N}.bin");
        var store = new TwitchTokenStore(storePath);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var client = new FakeClient { ValidationResult = new TwitchTokenValidation(TestClientId, "user-1", "viewer", expiresIn, []) };
        var session = new TwitchSession(store, _ => client, clock);
        var context = new TestContext();
        store.Save(Token(clientId: MachineClientId()));

        bool kept = await session.ValidateTickAsync(context, CancellationToken.None);

        Assert.IsTrue(kept);
        Assert.AreEqual(clock.GetUtcNow().AddSeconds(1), store.Load()!.ExpiresAt,
            "A zero or negative server ExpiresIn clamps to one second");
    }

    [TestMethod]
    public async Task Login_PollTokenWithStaleClientId_StampReassertsTheResolvedClient()
    {
        var (session, client, store, _) = CreateSession();
        var context = new TestContext();
        client.Device = new TwitchDeviceAuthorization("device-code", "USER-CODE", new Uri("https://twitch.tv/activate"), DateTimeOffset.UtcNow.AddMinutes(5), 5);
        client.PollToken = Token(clientId: "stale-client-id");

        await session.LoginAsync(TestClientId, context, CancellationToken.None);

        Assert.AreEqual(TestClientId, store.Load()!.ClientId,
            "The stamp re-asserts the client id the token was minted for");
    }

    [TestMethod]
    public async Task ValidateTick_RefreshRejected_ClearsStateAndEndsTheMonitor()
    {
        var (session, client, store, _) = CreateSession();
        var context = new TestContext();
        store.Save(Token(clientId: MachineClientId()));
        client.ValidationUnauthorized = true;
        client.RefreshRejected = true;

        bool kept = await session.ValidateTickAsync(context, CancellationToken.None);

        Assert.IsFalse(kept, "A rejected refresh ends the monitor");
        Assert.IsFalse(session.IsAuthenticated);
        Assert.IsNull(store.Load(), "A rejected refresh must delete the stored token");
    }

    [TestMethod]
    public async Task ValidateTick_HungValidation_DoesNotHoldTheUserGate()
    {
        var (session, client, store, _) = CreateSession();
        var context = new TestContext();
        store.Save(Token(clientId: MachineClientId()));
        client.ValidatePark = new TaskCompletionSource();

        // The tick runs to its first await synchronously — by the time the
        // Task is returned it is parked inside the validation network call.
        Task tick = session.ValidateTickAsync(context, CancellationToken.None);
        Assert.IsFalse(tick.IsCompleted, "The tick must be parked in the network call");

        // The user's logout must not queue behind a gate a hung validation
        // would be holding — it completes while the tick is still parked.
        Task logout = session.LogoutAsync(CancellationToken.None);
        var winner = await Task.WhenAny(logout, Task.Delay(2000));
        Assert.AreSame(logout, winner, "A hung validation must not hold the user gate");
        Assert.IsFalse(tick.IsCompleted, "The tick is still parked in the validation");

        client.ValidatePark.SetResult();
        await tick;

        Assert.IsNull(store.Load(), "A stale verdict must not resurrect a logged-out token");
        Assert.IsFalse(session.IsAuthenticated, "The logout's clear must win over the late verdict");
    }
}
