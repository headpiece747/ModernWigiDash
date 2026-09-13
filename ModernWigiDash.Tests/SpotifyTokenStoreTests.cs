using System.IO;
using ModernWigiDash.Widgets.Spotify;

namespace ModernWigiDash.Tests;

[TestClass]
public class SpotifyTokenStoreTests
{
    [TestMethod]
    public void SaveLoad_RoundTripsTokenSet()
    {
        string path = TempPath();
        var store = new SpotifyTokenStore(path);
        var token = new SpotifyTokenSet(
            AccessToken: "access-token-123",
            RefreshToken: "refresh-token-456",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Scopes: ["user-read-playback-state", "playlist-read-private"]);

        store.Save(token);
        try
        {
            SpotifyTokenSet? loaded = store.Load();

            Assert.IsNotNull(loaded);
            Assert.AreEqual(token.AccessToken, loaded.AccessToken);
            Assert.AreEqual(token.RefreshToken, loaded.RefreshToken);
            Assert.AreEqual(token.ExpiresAt, loaded.ExpiresAt);
            CollectionAssert.AreEqual(token.Scopes, loaded.Scopes);
        }
        finally
        {
            store.Delete();
        }
    }

    [TestMethod]
    public void Load_WhenNoFileExists_ReturnsNull()
    {
        string path = TempPath();
        var store = new SpotifyTokenStore(path);
        store.Delete();

        Assert.IsNull(store.Load());
    }

    [TestMethod]
    public void Delete_RemovesStoredToken()
    {
        string path = TempPath();
        var store = new SpotifyTokenStore(path);
        store.Save(new SpotifyTokenSet("a", "r", DateTimeOffset.UtcNow.AddHours(1), []));

        store.Delete();

        Assert.IsNull(store.Load());
    }

    [TestMethod]
    public void Overwrite_ReplacesPreviousToken()
    {
        string path = TempPath();
        var store = new SpotifyTokenStore(path);
        store.Save(new SpotifyTokenSet("old", "old-refresh", DateTimeOffset.UtcNow.AddHours(1), []));
        try
        {
            store.Save(new SpotifyTokenSet("new", "new-refresh", DateTimeOffset.UtcNow.AddHours(2), []));

            SpotifyTokenSet? loaded = store.Load();
            Assert.IsNotNull(loaded);
            Assert.AreEqual("new", loaded.AccessToken);
            Assert.AreEqual("new-refresh", loaded.RefreshToken);
        }
        finally
        {
            store.Delete();
        }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"wmd-spotify-{Guid.NewGuid():N}.bin");
}
