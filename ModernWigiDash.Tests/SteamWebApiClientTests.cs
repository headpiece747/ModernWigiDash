using System.Net;
using System.Net.Http;
using System.Text;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Steam cluster's HTTP owner: the SteamID64 validation gate, the
/// dormant-property rule (no HTTP when the api key or id is empty), the
/// 5-minute persona cache (clock-relative), the persona parse, and the CDN
/// poster fallback's refusal rules. The fetch runs against a stub handler,
/// never the network.
/// </summary>
[TestClass]
public sealed class SteamWebApiClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<string> RequestUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [TestMethod]
    public void IsValidSteamId64_SeventeenDigits_ReturnsTrue()
    {
        Assert.IsTrue(SteamWebApiClient.IsValidSteamId64("76561198000000001"));
    }

    [TestMethod]
    public void IsValidSteamId64_WrongLengthOrLetters_ReturnsFalse()
    {
        Assert.IsFalse(SteamWebApiClient.IsValidSteamId64("7656119800000000")); // 16 digits.
        Assert.IsFalse(SteamWebApiClient.IsValidSteamId64("7656119800000000a")); // a letter.
        Assert.IsFalse(SteamWebApiClient.IsValidSteamId64(""));
    }

    [TestMethod]
    public void FetchPersonaAsync_EitherPropertyEmpty_ReturnsNullWithoutHttp()
    {
        var handler = new StubHandler(_ => JsonResponse("{}"));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var apiClient = new SteamWebApiClient(new FakeTimeProvider(), client);

        Assert.IsNull(apiClient.FetchPersonaAsync("", "76561198000000001", CancellationToken.None).GetAwaiter().GetResult());
        Assert.IsNull(apiClient.FetchPersonaAsync("key", "", CancellationToken.None).GetAwaiter().GetResult());

        Assert.IsEmpty(handler.RequestUrls);
    }

    [TestMethod]
    public void FetchPersonaAsync_ValidResponse_ParsesThePlayerRow()
    {
        string body = """
            {
              "response": {
                "players": [
                  {
                    "steamid": "76561198000000001",
                    "personaname": "Tester",
                    "avatar_full": "https://example.com/avatar.jpg",
                    "personastate": 2
                  }
                ]
              }
            }
            """;
        var handler = new StubHandler(_ => JsonResponse(body));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var apiClient = new SteamWebApiClient(new FakeTimeProvider(), client);

        SteamPersona? persona = apiClient.FetchPersonaAsync("key", "76561198000000001", CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsNotNull(persona);
        Assert.AreEqual("Tester", persona.PersonaName);
        Assert.AreEqual("https://example.com/avatar.jpg", persona.AvatarUrl);
        Assert.AreEqual("InGame", persona.StateLabel);
        CollectionAssert.Contains(handler.RequestUrls, "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key=key&steamids=76561198000000001");
    }

    [TestMethod]
    public void FetchPersonaAsync_FreshCache_ServesWithoutRefetch()
    {
        string body = """{ "response": { "players": [ { "personaname": "T", "personastate": 1 } ] } }""";
        var handler = new StubHandler(_ => JsonResponse(body));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var clock = new FakeTimeProvider();
        var apiClient = new SteamWebApiClient(clock, client);

        apiClient.FetchPersonaAsync("key", "76561198000000001", CancellationToken.None).GetAwaiter().GetResult();
        clock.Advance(TimeSpan.FromMinutes(4));
        apiClient.FetchPersonaAsync("key", "76561198000000001", CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual(1, handler.RequestUrls.Count, "the fresh cache must not refetch");

        clock.Advance(TimeSpan.FromMinutes(2)); // 6 minutes past the first fetch.
        apiClient.FetchPersonaAsync("key", "76561198000000001", CancellationToken.None).GetAwaiter().GetResult();
        Assert.AreEqual(2, handler.RequestUrls.Count, "an expired cache refetches");
    }

    [TestMethod]
    public void FetchPersonaAsync_NoPlayerRow_ReturnsNull()
    {
        var handler = new StubHandler(_ => JsonResponse("""{ "response": { "players": [] } }"""));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var apiClient = new SteamWebApiClient(new FakeTimeProvider(), client);

        Assert.IsNull(apiClient.FetchPersonaAsync("key", "76561198000000001", CancellationToken.None).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void FetchPersonaAsync_HttpFailure_ReturnsNull()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var apiClient = new SteamWebApiClient(new FakeTimeProvider(), client);

        Assert.IsNull(apiClient.FetchPersonaAsync("key", "76561198000000001", CancellationToken.None).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void FetchPosterAsync_CdnUrl_ServesTheBytes()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]),
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var apiClient = new SteamWebApiClient(new FakeTimeProvider(), client);

        byte[]? bytes = apiClient.FetchPosterAsync(730u, CancellationToken.None).GetAwaiter().GetResult();

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes!);
        CollectionAssert.Contains(handler.RequestUrls, "https://cdn.akamai.steamstatic.com/steam/apps/730/library_600x900.jpg");
    }

    [TestMethod]
    public void FetchPosterAsync_NotFound_ReturnsNull()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var apiClient = new SteamWebApiClient(new FakeTimeProvider(), client);

        Assert.IsNull(apiClient.FetchPosterAsync(730u, CancellationToken.None).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void FetchPosterAsync_OversizedAnswer_IsRefused()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]),
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var apiClient = new SteamWebApiClient(new FakeTimeProvider(), client);

        Assert.IsNull(apiClient.FetchPosterAsync(730u, CancellationToken.None).GetAwaiter().GetResult());
    }
}
