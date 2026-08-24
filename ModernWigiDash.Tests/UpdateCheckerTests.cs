using ModernWigiDash.App.Update;

namespace ModernWigiDash.Tests;

[TestClass]
public class UpdateCheckerTests
{
    // The fixtures use GitHub's real payload shape: the asset URLs live on
    // github.com (the host pin) and the digests carry the sha256: prefix
    // (the digest pin). The example.com / prefix-less fixtures are the
    // rejected shapes, pinned by the *_ReturnsNull tests below.
    private const string LatestJson = """
    {
      "tag_name": "v0.5.0",
      "assets": [
        { "name": "ModernWigiDash-v0.5.0-win-x64.zip", "browser_download_url": "https://github.com/headpiece747/ModernWigiDash/releases/download/v0.5.0/ModernWigiDash-v0.5.0-win-x64.zip", "digest": "sha256:aaa" },
        { "name": "ModernWigiDash-v0.5.0-app-only.zip", "browser_download_url": "https://github.com/headpiece747/ModernWigiDash/releases/download/v0.5.0/ModernWigiDash-v0.5.0-app-only.zip", "digest": "sha256:bbb" }
      ]
    }
    """;

    [TestMethod]
    public void ParseLatestRelease_NewerVersion_ReturnsSlimAssetUpdate()
    {
        var info = UpdateChecker.ParseLatestRelease(LatestJson, new Version(0, 4, 1));

        Assert.IsNotNull(info);
        Assert.AreEqual("0.5.0", info.Version);
        Assert.AreEqual("https://github.com/headpiece747/ModernWigiDash/releases/download/v0.5.0/ModernWigiDash-v0.5.0-app-only.zip", info.ZipUrl, "must pick the app-only zip, never the full zip");
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
            { "name": "ModernWigiDash-v0.5.0-app-only.zip", "browser_download_url": "https://github.com/headpiece747/ModernWigiDash/releases/download/v0.5.0/app.zip",
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

    [TestMethod]
    public void ParseLatestRelease_UntrustedAssetHost_ReturnsNull()
    {
        // The digest is well formed; the host is not. The digest and the
        // bytes come from the same payload, so only the host is independent
        // evidence against a MITM-served release.
        const string untrusted = """
        { "tag_name": "v0.5.0", "assets": [
            { "name": "ModernWigiDash-v0.5.0-app-only.zip", "browser_download_url": "https://example.com/app.zip",
              "digest": "sha256:05236ea7b79e5b4097c7223121f72bcf5576baf7a9f0c1a9d2f2d5a778360070" } ] }
        """;
        Assert.IsNull(UpdateChecker.ParseLatestRelease(untrusted, new Version(0, 4, 1)),
            "a release payload served from a non-GitHub host must be refused");
    }

    [TestMethod]
    public void ParseLatestRelease_NonHttpsAssetHost_ReturnsNull()
    {
        const string plainHttp = """
        { "tag_name": "v0.5.0", "assets": [
            { "name": "ModernWigiDash-v0.5.0-app-only.zip", "browser_download_url": "http://github.com/headpiece747/ModernWigiDash/releases/download/v0.5.0/app.zip",
              "digest": "sha256:05236ea7b79e5b4097c7223121f72bcf5576baf7a9f0c1a9d2f2d5a778360070" } ] }
        """;
        Assert.IsNull(UpdateChecker.ParseLatestRelease(plainHttp, new Version(0, 4, 1)),
            "asset downloads are https only");
    }

    [TestMethod]
    public void ParseLatestRelease_PrefixLessDigest_ReturnsNull()
    {
        // A digest the payload does not name an algorithm for cannot be
        // compared: GitHub always sends "sha256:<hex>", anything else is not
        // a trusted release digest.
        const string prefixless = """
        { "tag_name": "v0.5.0", "assets": [
            { "name": "ModernWigiDash-v0.5.0-app-only.zip", "browser_download_url": "https://github.com/headpiece747/ModernWigiDash/releases/download/v0.5.0/app.zip",
              "digest": "05236ea7b79e5b4097c7223121f72bcf5576baf7a9f0c1a9d2f2d5a778360070" } ] }
        """;
        Assert.IsNull(UpdateChecker.ParseLatestRelease(prefixless, new Version(0, 4, 1)),
            "a prefix-less digest is not a trusted release digest");
    }
}
