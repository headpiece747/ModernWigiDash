using System.Net;
using System.Net.Http;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Twitch OAuth device-code poll loop — previously untested because the
/// client's HttpClient and clock seams were never exercised. The stub handler
/// simulates Twitch's token endpoint; a zero poll interval keeps the loop
/// instant.
/// </summary>
[TestClass]
public class TwitchApiClientPollTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses = [];
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.BadRequest));
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private static TwitchApiClient CreateClient(StubHandler handler, out FixedClock clock)
    {
        clock = new FixedClock(Now);
        return new TwitchApiClient("test-client", new HttpClient(handler)) { Clock = clock };
    }

    private static TwitchDeviceAuthorization Authorization(TimeSpan expiry)
        => new("device-code", "USER-CODE", new Uri("https://example.com"), Now + expiry, PollIntervalSeconds: 0);

    private static HttpResponseMessage Pending() => new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"message":"authorization_pending","status":400}""") };

    private static HttpResponseMessage Error(string code) => new(HttpStatusCode.BadRequest) { Content = new StringContent($$"""{"message":"{{code}}","status":400}""") };

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"access_token":"tok","refresh_token":"ref","expires_in":3600,"scope":["user:read:follows"]}"""),
    };

    [TestMethod]
    public async Task PollDeviceTokenAsync_PendingThenSuccess_ReturnsToken()
    {
        var handler = new StubHandler();
        var client = CreateClient(handler, out _);
        handler.Responses.Enqueue(Pending());
        handler.Responses.Enqueue(Success());

        var token = await client.PollDeviceTokenAsync(Authorization(TimeSpan.FromMinutes(5)), CancellationToken.None);

        Assert.AreEqual("tok", token.AccessToken);
        Assert.AreEqual("ref", token.RefreshToken);
        Assert.AreEqual(2, handler.Calls, "pending keeps polling until the token arrives");
    }

    [TestMethod]
    public async Task PollDeviceTokenAsync_SlowDown_KeepsPolling()
    {
        var handler = new StubHandler();
        var client = CreateClient(handler, out _);
        handler.Responses.Enqueue(Pending());
        handler.Responses.Enqueue(Error("slow_down"));
        handler.Responses.Enqueue(Success());

        var token = await client.PollDeviceTokenAsync(Authorization(TimeSpan.FromMinutes(5)), CancellationToken.None);

        Assert.AreEqual("tok", token.AccessToken);
        Assert.AreEqual(3, handler.Calls);
    }

    [TestMethod]
    public async Task PollDeviceTokenAsync_AccessDenied_Throws()
    {
        var handler = new StubHandler();
        var client = CreateClient(handler, out _);
        handler.Responses.Enqueue(Error("access_denied"));

        await Assert.ThrowsAsync<TwitchApiException>(() =>
            client.PollDeviceTokenAsync(Authorization(TimeSpan.FromMinutes(5)), CancellationToken.None));
    }

    [TestMethod]
    public async Task PollDeviceTokenAsync_ExpiredToken_Throws()
    {
        var handler = new StubHandler();
        var client = CreateClient(handler, out _);
        handler.Responses.Enqueue(Error("expired_token"));

        await Assert.ThrowsAsync<TwitchApiException>(() =>
            client.PollDeviceTokenAsync(Authorization(TimeSpan.FromMinutes(5)), CancellationToken.None));
    }

    [TestMethod]
    public async Task PollDeviceTokenAsync_ClockPastExpiry_Throws408WithoutPolling()
    {
        var handler = new StubHandler();
        var client = CreateClient(handler, out _);

        await Assert.ThrowsAsync<TwitchApiException>(() =>
            client.PollDeviceTokenAsync(Authorization(TimeSpan.FromSeconds(-1)), CancellationToken.None));

        Assert.AreEqual(0, handler.Calls, "an already-expired authorization never polls");
    }
}
