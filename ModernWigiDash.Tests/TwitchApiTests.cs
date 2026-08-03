using System.Net;
using System.Text;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class TwitchApiTests
{
    [TestMethod]
    public async Task StartDeviceAuthorization_SendsPublicClientAndFollowScope()
    {
        string? requestBody = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                {
                  "device_code": "device-code",
                  "user_code": "ABCD-EFGH",
                  "verification_uri": "https://www.twitch.tv/activate",
                  "expires_in": 600,
                  "interval": 5
                }
                """);
        }));

        var api = new TwitchApiClient("public-client-id", client);
        TwitchDeviceAuthorization authorization = await api.StartDeviceAuthorizationAsync(CancellationToken.None);

        Assert.AreEqual("device-code", authorization.DeviceCode);
        Assert.AreEqual("ABCD-EFGH", authorization.UserCode);
        Assert.AreEqual("https://www.twitch.tv/activate", authorization.VerificationUri.AbsoluteUri);
        StringAssert.Contains(requestBody, "client_id=public-client-id");
        StringAssert.Contains(requestBody, "scopes=user%3Aread%3Afollows");
    }

    [TestMethod]
    public async Task GetFollowedLiveChannels_FollowsPaginationAndSortsDisplayNames()
    {
        int requestCount = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                StringAssert.Contains(request.RequestUri?.AbsolutePath, "/helix/streams/followed");
                StringAssert.Contains(request.RequestUri?.Query, "user_id=user-1");
                return JsonResponse("""
                    {
                      "data": [
                        {
                          "user_id": "2",
                          "user_login": "zeta",
                          "user_name": "Zeta"
                        }
                      ],
                      "pagination": { "cursor": "next-page" }
                    }
                    """);
            }

            StringAssert.Contains(request.RequestUri?.Query, "after=next-page");
            return JsonResponse("""
                {
                  "data": [
                    {
                      "user_id": "1",
                      "user_login": "alpha",
                      "user_name": "Alpha"
                    }
                  ],
                  "pagination": {}
                }
                """);
        }));

        var api = new TwitchApiClient("public-client-id", client);
        IReadOnlyList<TwitchFollowedChannel> channels = await api.GetFollowedLiveChannelsAsync("access-token", "user-1", CancellationToken.None);

        Assert.AreEqual(2, requestCount);
        Assert.AreEqual("Alpha", channels[0].DisplayName);
        Assert.AreEqual("Zeta", channels[1].DisplayName);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
