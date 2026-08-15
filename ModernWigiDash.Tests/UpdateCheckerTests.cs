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
        Assert.AreEqual("0.5.0", info.Version);
        Assert.AreEqual("https://example.com/app.zip", info.ZipUrl, "must pick the app-only zip, never the full zip");
        Assert.AreEqual("bbb", info.Sha256);
    }

    [TestMethod]
    public void ParseLatestRelease_GitHubSha256PrefixedDigest_IsNormalized()
    {
        // GitHub's asset digest is "sha256:<hex>"; the app compares raw hex.
        // A prefixed digest must be normalized, not compared verbatim (the
        // on-device update loop failed on exactly this until the fix).
        const string prefixed = """
        { "tag_name": "v0.5.0", "assets": [
            { "name": "ModernWigiDash-v0.5.0-app-only.zip", "browser_download_url": "https://example.com/app.zip",
              "digest": "sha256:05236ea7b79e5b4097c7223121f72bcf5576baf7a9f0c1a9d2f2d5a778360070" } ] }
        """;

        var info = UpdateChecker.ParseLatestRelease(prefixed, new Version(0, 4, 1));

        Assert.IsNotNull(info);
        Assert.AreEqual("05236ea7b79e5b4097c7223121f72bcf5576baf7a9f0c1a9d2f2d5a778360070", info.Sha256,
            "the sha256: prefix must be stripped before the hex comparison");
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
