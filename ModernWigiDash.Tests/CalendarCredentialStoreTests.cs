using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModernWigiDash.Tests;

[TestClass]
public class CalendarCredentialStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"wmd-cal-cred-{Guid.NewGuid():N}.bin");

    [TestMethod]
    public void SaveThenLoad_RoundTripsThePassword()
    {
        string path = TempPath();
        try
        {
            var store = new CalendarCredentialStore(path);
            store.SavePassword("icloud", "s3cret");

            Assert.AreEqual("s3cret", store.LoadPassword("icloud"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_AbsentFeed_ReturnsNull()
    {
        string path = TempPath();
        try
        {
            var store = new CalendarCredentialStore(path);
            Assert.IsNull(store.LoadPassword("nope"), "an absent feed id has no stored password");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void Save_ReplacesAnExistingPasswordForTheSameFeed()
    {
        string path = TempPath();
        try
        {
            var store = new CalendarCredentialStore(path);
            store.SavePassword("icloud", "old");
            store.SavePassword("icloud", "new");

            Assert.AreEqual("new", store.LoadPassword("icloud"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void Delete_RemovesTheStoredPassword()
    {
        string path = TempPath();
        try
        {
            var store = new CalendarCredentialStore(path);
            store.SavePassword("icloud", "s3cret");
            store.DeletePassword("icloud");

            Assert.IsNull(store.LoadPassword("icloud"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void Credentials_AreIsolatedPerFeedId()
    {
        string path = TempPath();
        try
        {
            var store = new CalendarCredentialStore(path);
            store.SavePassword("a", "pa");
            store.SavePassword("b", "pb");

            Assert.AreEqual("pa", store.LoadPassword("a"));
            Assert.AreEqual("pb", store.LoadPassword("b"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

[TestClass]
public class CalendarCredentialSeamTests
{
    [TestMethod]
    public void TestContext_SaveCalendarCredential_RecordsTheCall()
    {
        var ctx = new TestContext();

        ctx.SaveCalendarCredential("icloud", "s3cret");

        CollectionAssert.Contains(ctx.CalendarCredentialCalls, ("icloud", "s3cret"));
    }

    [TestMethod]
    public void Context_Default_NoOpDoesNotThrow()
    {
        // The interface default is a no-op (the NavigatePage precedent): a host
        // that does not override it must not throw when the editor saves. A
        // sentinel written after the call proves execution continued past it.
        IModernWigiDashContext ctx = new NoOpContext();
        ctx.SaveCalendarCredential("x", "y"); // must be a benign no-op
        string reached = "past-the-call";
        Assert.AreEqual("past-the-call", reached);
    }

    private sealed class NoOpContext : IModernWigiDashContext
    {
        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
        public void RequestRender() { }
        public void RequestInspectorRefresh() { }
        public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt) { }
        public void CloseDeviceAuthorization() { }
    }
}
