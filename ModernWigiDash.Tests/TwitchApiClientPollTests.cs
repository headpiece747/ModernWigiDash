using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Time.Testing;
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
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private static TwitchApiClient CreateClient(StubHttpHandler handler, out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider(Now);
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
        var handler = new StubHttpHandler(Pending(), Success());
        var client = CreateClient(handler, out _);

        var token = await client.PollDeviceTokenAsync(Authorization(TimeSpan.FromMinutes(5)), CancellationToken.None);

        Assert.AreEqual("tok", token.AccessToken);
        Assert.AreEqual("ref", token.RefreshToken);
        Assert.AreEqual(2, handler.Calls, "pending keeps polling until the token arrives");
    }

    [TestMethod]
    public async Task PollDeviceTokenAsync_SlowDown_KeepsPolling()
    {
        var handler = new StubHttpHandler(Pending(), Error("slow_down"), Success());
        var client = CreateClient(handler, out _);

        var token = await client.PollDeviceTokenAsync(Authorization(TimeSpan.FromMinutes(5)), CancellationToken.None);

        Assert.AreEqual("tok", token.AccessToken);
        Assert.AreEqual(3, handler.Calls);
    }

    [TestMethod]
    public async Task PollDeviceTokenAsync_AccessDenied_Throws()
    {
        var handler = new StubHttpHandler(Error("access_denied"));
        var client = CreateClient(handler, out _);

        await Assert.ThrowsAsync<TwitchApiException>(() =>
            client.PollDeviceTokenAsync(Authorization(TimeSpan.FromMinutes(5)), CancellationToken.None));
    }

    [TestMethod]
    public async Task PollDeviceTokenAsync_ExpiredToken_Throws()
    {
        var handler = new StubHttpHandler(Error("expired_token"));
        var client = CreateClient(handler, out _);

        await Assert.ThrowsAsync<TwitchApiException>(() =>
            client.PollDeviceTokenAsync(Authorization(TimeSpan.FromMinutes(5)), CancellationToken.None));
    }

    [TestMethod]
    public async Task PollDeviceTokenAsync_ClockPastExpiry_Throws408WithoutPolling()
    {
        var handler = new StubHttpHandler();
        var client = CreateClient(handler, out _);

        await Assert.ThrowsAsync<TwitchApiException>(() =>
            client.PollDeviceTokenAsync(Authorization(TimeSpan.FromSeconds(-1)), CancellationToken.None));

        Assert.AreEqual(0, handler.Calls, "an already-expired authorization never polls");
    }
}
