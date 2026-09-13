using System.Net;
using System.Net.Http;
using ModernWigiDash.Widgets.Spotify;

namespace ModernWigiDash.Tests;

[TestClass]
public class SpotifyApiClientTests
{
    private const string ClientId = "test-client-id";

    /// <summary>A stub HTTP handler that returns canned responses in order.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;
        public StubHandler(IEnumerable<(HttpStatusCode Status, string Body)> responses) => _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0) throw new InvalidOperationException("No more canned responses.");
            var (status, body) = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static SpotifyApiClient CreateClient(params (HttpStatusCode Status, string Body)[] responses)
        => new(ClientId, new HttpClient(new StubHandler(responses)));

    [TestMethod]
    public async Task StartDeviceAuthorization_ParsesResponse()
    {
        var client = CreateClient((HttpStatusCode.OK, """{"device_code":"dc","user_code":"UC","verification_uri":"https://spotify.com/activate","expires_in":600,"interval":5}"""));

        var auth = await client.StartDeviceAuthorizationAsync(CancellationToken.None);

        Assert.AreEqual("dc", auth.DeviceCode);
        Assert.AreEqual("UC", auth.UserCode);
        Assert.AreEqual("https://spotify.com/activate", auth.VerificationUri.AbsoluteUri);
        Assert.AreEqual(5, auth.PollIntervalSeconds);
    }

    [TestMethod]
    public async Task PollDeviceToken_ReturnsTokenOnSuccess()
    {
        var client = CreateClient((HttpStatusCode.OK, """{"access_token":"at","refresh_token":"rt","expires_in":3600,"scope":"user-read-playback-state"}"""));
        var device = new SpotifyDeviceAuthorization("dc", "UC", new Uri("https://spotify.com/activate"), DateTimeOffset.UtcNow.AddMinutes(5), 1);

        var token = await client.PollDeviceTokenAsync(device, CancellationToken.None);

        Assert.AreEqual("at", token.AccessToken);
        Assert.AreEqual("rt", token.RefreshToken);
        CollectionAssert.Contains(token.Scopes, "user-read-playback-state");
    }

    [TestMethod]
    public async Task PollDeviceToken_KeepsPollingOnPending()
    {
        var client = CreateClient(
            (HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
            (HttpStatusCode.OK, """{"access_token":"at","refresh_token":"rt","expires_in":3600,"scope":""}"""));
        var device = new SpotifyDeviceAuthorization("dc", "UC", new Uri("https://spotify.com/activate"), DateTimeOffset.UtcNow.AddMinutes(5), 1);

        var token = await client.PollDeviceTokenAsync(device, CancellationToken.None);

        Assert.AreEqual("at", token.AccessToken);
    }

    [TestMethod]
    public async Task GetNowPlaying_ParsesPlaybackState()
    {
        var client = CreateClient((HttpStatusCode.OK, """{"is_playing":true,"progress_ms":30000,"item":{"name":"Song","duration_ms":240000,"album":{"name":"Album","images":[{"url":"img"}]},"artists":[{"name":"Artist"}]}}"""));

        var now = await client.GetNowPlayingAsync("token", CancellationToken.None);

        Assert.IsNotNull(now);
        Assert.AreEqual("Song", now.TrackName);
        Assert.AreEqual("Artist", now.ArtistName);
        Assert.AreEqual("Album", now.AlbumName);
        Assert.IsTrue(now.IsPlaying);
        Assert.AreEqual(30000, now.PositionMs);
        Assert.AreEqual(240000, now.DurationMs);
    }

    [TestMethod]
    public async Task GetNowPlaying_ReturnsNullWhenNotFound()
    {
        var client = CreateClient((HttpStatusCode.NotFound, ""));

        var now = await client.GetNowPlayingAsync("token", CancellationToken.None);

        Assert.IsNull(now);
    }

    [TestMethod]
    public async Task GetPlaylists_ParsesItems()
    {
        var client = CreateClient((HttpStatusCode.OK, """{"items":[{"id":"p1","name":"Playlist","images":[{"url":"img"}]}]}"""));

        var playlists = await client.GetPlaylistsAsync("token", CancellationToken.None);

        Assert.AreEqual(1, playlists.Count);
        Assert.AreEqual("p1", playlists[0].Id);
        Assert.AreEqual("Playlist", playlists[0].Name);
        Assert.AreEqual("img", playlists[0].ImageUrl);
    }

    [TestMethod]
    public void UnauthorizedFlag_IsTrueFor401Only()
    {
        Assert.IsTrue(new SpotifyApiException(401, "x").IsUnauthorized);
        Assert.IsFalse(new SpotifyApiException(400, "x").IsUnauthorized);
        Assert.IsFalse(new SpotifyApiException(500, "x").IsUnauthorized);
    }
}
