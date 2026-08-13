using System.Text.Json;
using ModernWigiDash.App.Update;

namespace ModernWigiDash.Tests;

[TestClass]
public class UpdateCheckerTests
{
    private const string LatestJson = """
    {
      "tag_name": "v0.5.0",
      "assets": [
        { "name": "ModernWigiDash-v0.5.0-win-x64.zip", "browser_download_url": "https://example.com/full.zip", "digest": "aaa" },
        { "name": "ModernWigiDash-v0.5.0-app-only.zip", "browser_download_url": "https://example.com/app.zip", "digest": "bbb" }
      ]
    }
    """;

    [TestMethod]
    public void ParseLatestRelease_NewerVersion_ReturnsSlimAssetUpdate()
    {
        var info = UpdateChecker.ParseLatestRelease(LatestJson, new Version(0, 4, 1));

        Assert.IsNotNull(info);
        Assert.AreEqual("0.5.0", info!.Version);
        Assert.AreEqual("https://example.com/app.zip", info.ZipUrl, "must pick the app-only zip, never the full zip");
        Assert.AreEqual("bbb", info.Sha256);
    }

    [TestMethod]
    public void ParseLatestRelease_CurrentVersionUpToDate_ReturnsNull()
    {
        Assert.IsNull(UpdateChecker.ParseLatestRelease(LatestJson, new Version(0, 5, 0)));
        Assert.IsNull(UpdateChecker.ParseLatestRelease(LatestJson, new Version(1, 0, 0)));
    }

    [TestMethod]
    public void ParseLatestRelease_DevBuild_ReturnsNull()
    {
        Assert.IsNull(UpdateChecker.ParseLatestRelease(LatestJson, null), "dev builds never nag");
    }

    [TestMethod]
    public void ParseLatestRelease_NoAppOnlyAsset_ReturnsNull()
    {
        const string noSlim = """
        { "tag_name": "v0.5.0", "assets": [ { "name": "ModernWigiDash-v0.5.0-win-x64.zip", "browser_download_url": "x", "digest": "a" } ] }
        """;
        Assert.IsNull(UpdateChecker.ParseLatestRelease(noSlim, new Version(0, 4, 1)));
    }

    [TestMethod]
    public void ParseLatestRelease_InvalidJson_ReturnsNull()
    {
        Assert.IsNull(UpdateChecker.ParseLatestRelease("not json", new Version(0, 4, 1)));
    }

    [TestMethod]
    public void ParseLatestRelease_WrongTypedTag_ReturnsNull()
    {
        // tag_name as a number throws InvalidOperationException from
        // GetString — the invalid->null contract must not let it escape.
        Assert.IsNull(UpdateChecker.ParseLatestRelease(
            """{ "tag_name": 42, "assets": [] }""", new Version(0, 4, 1)));
    }

    [TestMethod]
    public void ParseLatestRelease_WrongTypedAssets_ReturnsNull()
    {
        // assets as an object throws InvalidOperationException from
        // EnumerateArray — must degrade to null, not throw.
        Assert.IsNull(UpdateChecker.ParseLatestRelease(
            """{ "tag_name": "v0.5.0", "assets": { "name": "x" } }""", new Version(0, 4, 1)));
    }

    [TestMethod]
    public void ParseLatestRelease_WrongTypedDownloadUrl_ReturnsNull()
    {
        // browser_download_url as a number throws InvalidOperationException
        // from GetString inside the slim-asset pick.
        Assert.IsNull(UpdateChecker.ParseLatestRelease(
            """{ "tag_name": "v0.5.0", "assets": [ { "name": "ModernWigiDash-v0.5.0-app-only.zip", "browser_download_url": 0 } ] }""",
            new Version(0, 4, 1)));
    }

    [TestMethod]
    public void ParseLatestRelease_NoAssets_ReturnsNull()
    {
        Assert.IsNull(UpdateChecker.ParseLatestRelease("""{ "tag_name": "v0.5.0" }""", new Version(0, 4, 1)));
    }
}
