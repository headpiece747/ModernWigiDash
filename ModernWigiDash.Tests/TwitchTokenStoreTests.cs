using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

[TestClass]
public class TwitchTokenStoreTests
{
    [TestMethod]
    public void SaveLoad_RoundTripsTokenSet()
    {
        var store = new TwitchTokenStore();
        var token = new TwitchTokenSet(
            ClientId: "client-id",
            AccessToken: "access-token-123",
            RefreshToken: "refresh-token-456",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Scopes: ["chat:read", "user:read:follows"]);

        store.Save(token);
        try
        {
            TwitchTokenSet? loaded = store.Load();

            Assert.IsNotNull(loaded);
            Assert.AreEqual(token.AccessToken, loaded.AccessToken);
            Assert.AreEqual(token.RefreshToken, loaded.RefreshToken);
            Assert.AreEqual(token.ClientId, loaded.ClientId);
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
        var store = new TwitchTokenStore();
        store.Delete();

        Assert.IsNull(store.Load());
    }

    [TestMethod]
    public void Delete_RemovesStoredToken()
    {
        var store = new TwitchTokenStore();
        store.Save(new TwitchTokenSet(ClientId: "c", AccessToken: "a", RefreshToken: "r", ExpiresAt: DateTimeOffset.UtcNow.AddHours(1), Scopes: []));

        store.Delete();

        Assert.IsNull(store.Load());
    }

    [TestMethod]
    public void Overwrite_ReplacesPreviousToken()
    {
        var store = new TwitchTokenStore();
        store.Save(new TwitchTokenSet(ClientId: "c", AccessToken: "old", RefreshToken: "old-refresh", ExpiresAt: DateTimeOffset.UtcNow.AddHours(1), Scopes: []));
        try
        {
            store.Save(new TwitchTokenSet(ClientId: "c", AccessToken: "new", RefreshToken: "new-refresh", ExpiresAt: DateTimeOffset.UtcNow.AddHours(2), Scopes: []));

            TwitchTokenSet? loaded = store.Load();
            Assert.IsNotNull(loaded);
            Assert.AreEqual("new", loaded.AccessToken);
            Assert.AreEqual("new-refresh", loaded.RefreshToken);
        }
        finally
        {
            store.Delete();
        }
    }
}
