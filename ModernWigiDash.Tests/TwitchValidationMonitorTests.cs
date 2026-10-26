using System.IO;
using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

/// <summary>
/// The validation monitor's tick, driven directly (the S2 seam): a real
/// credential core over a fake store, a fake client, and a fake clock —
/// the tick's contract (the stamp rule, the rejection clear, the tokenless
/// clear) without the hourly timer.
/// </summary>
[TestClass]
public class TwitchValidationMonitorTests
{
    private const string TestClientId = "test-client-id";

    private sealed class FakeClient : TwitchApiClient
    {
        public TwitchTokenValidation? ValidationResult;
        public bool ValidationUnauthorized;
        public bool RefreshRejected;

        public FakeClient() : base(TestClientId) { }

        public override Task<TwitchDeviceAuthorization> StartDeviceAuthorizationAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<TwitchTokenSet> PollDeviceTokenAsync(TwitchDeviceAuthorization deviceAuthorization, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<TwitchTokenValidation> ValidateAsync(string accessToken, CancellationToken cancellationToken)
        {
            if (ValidationUnauthorized) throw new TwitchApiException(401, "unauthorized");
            return Task.FromResult(ValidationResult!);
        }

        public override Task<TwitchTokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            if (RefreshRejected) throw new TwitchApiException(400, "refresh rejected");
            throw new InvalidOperationException("the rejection tests never reach a successful refresh");
        }

        public override Task<IReadOnlyList<TwitchFollowedChannel>> GetFollowedLiveChannelsAsync(string accessToken, string userId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task RevokeAsync(string accessToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static TwitchTokenSet Token(
        string clientId = TestClientId,
        string accessToken = "access-token",
        DateTimeOffset? expiresAt = null)
        => new(clientId, accessToken, "refresh-token", expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), []);

    /// <summary>
    /// The client id the tick's resolver picks on this machine (the tick
    /// resolves with a null configured id, so the environment variable wins
    /// when set): the test token must carry that id, or the mismatch
    /// verdict clears the session before the verdict under test.
    /// </summary>
    private static string MachineClientId()
    {
        string? env = Environment.GetEnvironmentVariable("MODERNWIGIDASH_TWITCH_CLIENT_ID");
        return string.IsNullOrWhiteSpace(env) ? TestClientId : env.Trim();
    }

    private static (TwitchCredentialCore Core, TwitchValidationMonitor Monitor, FakeClient Client, TwitchTokenStore Store, FakeTimeProvider Clock) CreateMonitor(out string storePath)
    {
        storePath = Path.Combine(Path.GetTempPath(), $"wmd-twitch-{Guid.NewGuid():N}.bin");
        var store = new TwitchTokenStore(storePath);
        var client = new FakeClient { ValidationResult = new TwitchTokenValidation(MachineClientId(), "user-1", 3600, ["chat:read"]) };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var core = new TwitchCredentialCore(store, _ => client, clock);
        var monitor = new TwitchValidationMonitor(core, _ => client);
        return (core, monitor, client, store, clock);
    }

    [TestMethod]
    public async Task ValidateTick_ValidToken_StampsAndApplies()
    {
        var (core, monitor, _, store, clock) = CreateMonitor(out string storePath);
        try
        {
            var context = new TestContext();
            store.Save(Token(clientId: MachineClientId(), expiresAt: clock.GetUtcNow().AddSeconds(10)));

            bool kept = await monitor.ValidateTickAsync(context, CancellationToken.None);

            Assert.IsTrue(kept, "a valid token keeps the session running");
            Assert.IsTrue(core.IsAuthenticated);
            TwitchTokenSet? live = core.Snapshot();
            Assert.IsNotNull(live);
            Assert.AreEqual(clock.GetUtcNow().AddSeconds(3600), live.ExpiresAt, "the stamp is the clock plus the validation's ExpiresIn");
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    [TestMethod]
    public async Task ValidateTick_RejectedRefresh_ClearsStateAndStore()
    {
        var (core, monitor, client, store, _) = CreateMonitor(out string storePath);
        try
        {
            client.ValidationUnauthorized = true;
            client.RefreshRejected = true;
            var context = new TestContext();
            store.Save(Token(clientId: MachineClientId()));

            bool kept = await monitor.ValidateTickAsync(context, CancellationToken.None);

            Assert.IsFalse(kept, "a rejected refresh ends the monitor");
            Assert.IsFalse(core.IsAuthenticated);
            Assert.IsNull(store.Load(), "the clear deletes the stored token");
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    [TestMethod]
    public async Task ValidateTick_Tokenless_ClearsAndEnds()
    {
        var (core, monitor, _, _, _) = CreateMonitor(out string storePath);
        try
        {
            var context = new TestContext();

            bool kept = await monitor.ValidateTickAsync(context, CancellationToken.None);

            Assert.IsFalse(kept, "a tokenless session ends the monitor");
            Assert.IsFalse(core.IsAuthenticated);
        }
        finally
        {
            File.Delete(storePath);
        }
    }
}
