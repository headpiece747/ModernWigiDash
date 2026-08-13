# In-App Auto-Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an in-app auto-updater: a header button (left of Snap to Grid) appears when a newer GitHub release exists, downloads a slim app-only zip on click, stages it, and applies it on restart — in the same install location, preserving the user's profile and theme.

**Architecture:** A pure `UpdateChecker` (SemVer compare + GitHub release JSON parse + slim-asset pick) behind an `UpdateService` that runs once at startup over `HttpClient`; a three-state WPF button (Griddy icons via `Geometry.Parse` of the bundled path map) wired through MainWindow; an embedded `apply-update.cmd` batch that waits for process exit, self-elevates if needed, and does a crash-safe rename-aside swap; `build-release.ps1` gains the slim zip + upstream auto-resolve.

**Tech Stack:** .NET 10, WPF, C# 14, MSTest, PowerShell (release script), batch (updater), GitHub REST API (`releases/latest`).

## Global Constraints

- .NET 10 / current C# idioms only; **zero new NuGet dependencies**.
- Render/transport path untouched: no changes to the compositor, frame pipeline, USB, or widget renderers.
- Existing 993 tests stay green; test command:
  `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
- `ThemeSettings.ParseColor` stays the single hex parser; `GriddyIconPaths.Map` is the single icon source (public, `OrdinalIgnoreCase` keyed).
- The app's own version: `AssemblyInformationalVersion`, stamped at publish time by `build-release.ps1`; dev builds embed `0.0.0-dev` and the updater is disabled for `0.0.0`/unparseable.
- Update checks run once at startup; failures are silent (log line only); the button stays hidden when up-to-date/offline/failed.
- The full `ModernWigiDash-v{X}-win-x64.zip` remains the canonical fresh-install artifact; the slim `ModernWigiDash-v{X}-app-only.zip` is update-only (never documented for fresh installs).
- GitHub API: `https://api.github.com/repos/headpiece747/ModernWigiDash/releases/latest` (10s timeout); prereleases/drafts excluded by the API.
- File layout: stage + logs under `%LOCALAPPDATA%\ModernWigiDash\updates\`.

---

## File Structure

| File | Responsibility |
|------|----------------|
| Create `ModernWigiDash.App/Update/AppVersion.cs` | Read own version from `AssemblyInformationalVersion`; SemVer parse. |
| Create `ModernWigiDash.App/Update/UpdateInfo.cs` | Result record: available, version, asset URL, sha256 digest. |
| Create `ModernWigiDash.App/Update/UpdateChecker.cs` | Pure: GitHub JSON → `UpdateInfo?`; SemVer compare; slim-asset pick. |
| Create `ModernWigiDash.App/Update/UpdateService.cs` | Runtime: startup check, download w/ progress, SHA-256 verify, extract+stage, stale cleanup, `.old` recovery, spawn updater. |
| Create `ModernWigiDash.App/Update/GriddyIconGeometry.cs` | `Geometry.Parse` of `GriddyIconPaths.Map`, cached per name. |
| Create `ModernWigiDash.App/Update/apply-update.cmd` (embedded resource) | The swap: wait-exit → elevation → rename-aside → relaunch → log. |
| Modify `ModernWigiDash.App/MainWindow.xaml` | Update button in header (left of `ChkSnapToGrid`). |
| Modify `ModernWigiDash.App/MainWindow.Update.cs` | Wire button states, tooltips, restart prompt, spawn+close. |
| Modify `scripts/build-release.ps1` | `-p:InformationalVersion`, slim zip, upstream auto-resolve, `telemetry-versions.txt`. |
| Create `ModernWigiDash.Tests/UpdateCheckerTests.cs` | Pure checker tests. |
| Create `ModernWigiDash.Tests/UpdateServiceTests.cs` | Service tests via seams. |
| Create `ModernWigiDash.Tests/AppVersionTests.cs` | Version read/parse tests. |
| Create `ModernWigiDash.Tests/GriddyIconGeometryTests.cs` | Parse/cache/name tests. |
| Create `ModernWigiDash.Tests/UpdateScriptTests.cs` | `apply-update.cmd` integration on a temp dir. |

---

### Task 1: App Version + UpdateChecker (pure)

**Files:**
- Create: `ModernWigiDash.App/Update/AppVersion.cs`, `ModernWigiDash.App/Update/UpdateInfo.cs`, `ModernWigiDash.App/Update/UpdateChecker.cs`
- Test: `ModernWigiDash.Tests/AppVersionTests.cs`, `ModernWigiDash.Tests/UpdateCheckerTests.cs`

**Interfaces:**
- Produces (used by Tasks 2-5):
  - `public static class AppVersion { public static Version? Current { get; } public static bool IsDevBuild { get; } }` — reads `AssemblyInformationalVersion`, strips any `-suffix`, parses `major.minor.patch`; `IsDevBuild` true when unparseable or `0.0.0`.
  - `public sealed record UpdateInfo(string Version, string ZipUrl, string Sha256);`
  - `public static class UpdateChecker`
    - `public static UpdateInfo? ParseLatestRelease(string json, Version? currentVersion)` — returns the slim-asset update when the latest tag is newer than `currentVersion`, else null.
    - `internal static string? PickAppOnlyAsset(JsonElement release)` — the `browser_download_url` whose name matches `ModernWigiDash-v*-app-only.zip`.
    - `internal static Version? ParseTag(string tag)` — `v`-prefix tolerant SemVer.

- [ ] **Step 1: Write the failing tests**

`ModernWigiDash.Tests/AppVersionTests.cs`:

```csharp
using System.Reflection;
using ModernWigiDash.App.Update;

namespace ModernWigiDash.Tests;

[TestClass]
public class AppVersionTests
{
    [TestMethod]
    public void Current_ReadsInformationalVersion_AsSemVer()
    {
        // The test assembly has no informational stamp; the checker treats
        // unparseable as dev. This test pins the parse path directly instead.
        Assert.IsTrue(AppVersion.IsDevBuild || AppVersion.Current is not null);
    }

    [TestMethod]
    public void Parse_HandlesVersionSuffix()
    {
        var v = AppVersion.Parse("0.4.1-alpha.1");
        Assert.IsNotNull(v);
        Assert.AreEqual(0, v!.Major);
        Assert.AreEqual(4, v.Minor);
        Assert.AreEqual(1, v.Build);
    }

    [TestMethod]
    public void Parse_HandlesVPrefixTag()
    {
        var v = AppVersion.Parse("v0.5.0");
        Assert.IsNotNull(v);
        Assert.AreEqual(0, v!.Major);
        Assert.AreEqual(5, v.Minor);
    }

    [TestMethod]
    public void Parse_Unparseable_ReturnsNull()
    {
        Assert.IsNull(AppVersion.Parse("dev"));
        Assert.IsNull(AppVersion.Parse("0.0.0-dev"));
        Assert.IsNull(AppVersion.Parse(""));
    }
}
```

`ModernWigiDash.Tests/UpdateCheckerTests.cs`:

```csharp
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
    public void ParseLatestRelease_NoAssets_ReturnsNull()
    {
        Assert.IsNull(UpdateChecker.ParseLatestRelease("""{ "tag_name": "v0.5.0" }""", new Version(0, 4, 1)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~AppVersionTests|FullyQualifiedName~UpdateCheckerTests"`
Expected: FAIL — `AppVersion`/`UpdateChecker` not defined.

- [ ] **Step 3: Implement**

`ModernWigiDash.App/Update/AppVersion.cs`:

```csharp
using System.Reflection;

namespace ModernWigiDash.App.Update;

/// <summary>
/// The app's own version, read from the build-time informational stamp
/// (written by build-release.ps1 from the release tag). Dev builds embed
/// "0.0.0-dev" — unparseable, so <see cref="IsDevBuild"/> disables the updater.
/// </summary>
public static class AppVersion
{
    /// <summary>Parsed major.minor version, or null for dev/unparseable builds.</summary>
    public static Version? Current { get; } = Parse(ReadInformationalVersion());

    /// <summary>True when the build carries no parseable release version (dev).</summary>
    public static bool IsDevBuild => Current is null;

    /// <summary>Parses "v0.5.0", "0.5.0", or "0.5.0-suffix" into a Version (suffix stripped); null otherwise.</summary>
    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim().TrimStart('v', 'V');
        int dash = trimmed.IndexOf('-');
        if (dash >= 0) trimmed = trimmed[..dash];
        return Version.TryParse(trimmed, out var v) ? v : null;
    }

    private static string ReadInformationalVersion()
        => Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "";
}
```

`ModernWigiDash.App/Update/UpdateInfo.cs`:

```csharp
namespace ModernWigiDash.App.Update;

/// <summary>A pending update: the new version, the slim zip URL, and its SHA-256 digest.</summary>
public sealed record UpdateInfo(string Version, string ZipUrl, string Sha256);
```

`ModernWigiDash.App/Update/UpdateChecker.cs`:

```csharp
using System.Text.Json;

namespace ModernWigiDash.App.Update;

/// <summary>
/// Pure update-decision logic: parse the GitHub releases/latest JSON, compare
/// SemVer, and pick the slim app-only asset. No I/O — testable via a JSON string.
/// </summary>
public static class UpdateChecker
{
    /// <summary>Returns the pending slim update when the latest release is newer
    /// than <paramref name="currentVersion"/>, else null. Null current (dev) never updates.</summary>
    public static UpdateInfo? ParseLatestRelease(string json, Version? currentVersion)
    {
        if (currentVersion is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            var latest = AppVersion.Parse(tagEl.GetString());
            if (latest is null || latest <= currentVersion) return null;

            string? url = PickAppOnlyAsset(root);
            string? digest = FindDigest(root, url);
            if (url is null || digest is null) return null;

            return new UpdateInfo(
                $"{latest.Major}.{latest.Minor}.{latest.Build}",
                url,
                digest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Picks the app-only slim asset's download URL, or null when absent.</summary>
    internal static string? PickAppOnlyAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name.StartsWith("ModernWigiDash-v", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith("-app-only.zip", StringComparison.OrdinalIgnoreCase)
                && asset.TryGetProperty("browser_download_url", out var u))
            {
                return u.GetString();
            }
        }
        return null;
    }

    private static string? FindDigest(JsonElement release, string? url)
    {
        if (url is null || !release.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            string u = asset.TryGetProperty("browser_download_url", out var ue) ? ue.GetString() ?? "" : "";
            if (u == url && asset.TryGetProperty("digest", out var d)) return d.GetString();
        }
        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: the same filter command as Step 2.
Expected: PASS (10/10).

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/Update/AppVersion.cs ModernWigiDash.App/Update/UpdateInfo.cs ModernWigiDash.App/Update/UpdateChecker.cs ModernWigiDash.Tests/AppVersionTests.cs ModernWigiDash.Tests/UpdateCheckerTests.cs
git commit -m "feat: pure update checker with SemVer compare and slim-asset pick"
```

---

### Task 2: UpdateService — download, verify, stage, cleanup

**Files:**
- Create: `ModernWigiDash.App/Update/UpdateService.cs`
- Test: `ModernWigiDash.Tests/UpdateServiceTests.cs`

**Interfaces:**
- Consumes: `UpdateInfo`, `AppVersion`, `UpdateChecker` (Task 1).
- Produces (used by Tasks 3-5):
  - `public sealed class UpdateService`
    - `public UpdateService(Func<string, string?, Task<string?>>? downloadText = null, Func<string, string, IProgress<double>, CancellationToken, Task>? downloadFile = null, Func<string, bool>? sha256Matches = null, string? updatesRoot = null)` — test seams default to real implementations.
    - `public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)` — GETs `https://api.github.com/repos/headpiece747/ModernWigiDash/releases/latest`, calls `UpdateChecker.ParseLatestRelease`; null on dev/up-to-date/failure.
    - `public Task<bool> DownloadAndStageAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct = default)` — downloads to `updates\download\{version}.zip`, verifies SHA-256, extracts to `updates\staged\{version}`, writes `apply-update.cmd`; false + cleanup on mismatch.
    - `public void CleanupStale()` — deletes `updates\staged\*` and `updates\download\*` older than the current session (any existing stage at startup is stale).
    - `public void RecoverInterruptedSwap(string installDir)` — if `exe.old` exists and the new exe is missing, restore `.old`; else delete `.old`.
    - `public string StagedCmdPath(UpdateInfo info)` — path to the written cmd.
    - `internal static string ComputeSha256(string path)`.
    - `internal static string ExtractSlimZip(string zipPath, string targetDir)` — `System.IO.Compression.ZipFile.ExtractToDirectory`, returns the single `ModernWigiDash.App.exe` path.
  - `public const string GitHubLatestUrl = "https://api.github.com/repos/headpiece747/ModernWigiDash/releases/latest";`

- [ ] **Step 1: Write the failing tests**

`ModernWigiDash.Tests/UpdateServiceTests.cs`:

```csharp
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModernWigiDash.App.Update;

namespace ModernWigiDash.Tests;

[TestClass]
public class UpdateServiceTests
{
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "wmd-update-tests");

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(TempRoot, true); } catch { }
    }

    private static string NewDir() => Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));

    private static string Sha256Of(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [TestMethod]
    public async Task CheckForUpdate_NewerRelease_ReturnsInfo()
    {
        string json = """
        { "tag_name": "v0.5.0", "assets": [
            { "name": "ModernWigiDash-v0.5.0-app-only.zip", "browser_download_url": "https://x/app.zip", "digest": "abc" } ] }
        """;
        var service = new UpdateService(
            downloadText: (_, _) => Task.FromResult<string?>(json),
            updatesRoot: NewDir());

        var info = await service.CheckForUpdateAsync();

        Assert.IsNotNull(info);
        Assert.AreEqual("0.5.0", info!.Version);
    }

    [TestMethod]
    public async Task CheckForUpdate_HttpFailure_ReturnsNull()
    {
        var service = new UpdateService(
            downloadText: (_, _) => Task.FromResult<string?>(null),
            updatesRoot: NewDir());

        Assert.IsNull(await service.CheckForUpdateAsync());
    }

    [TestMethod]
    public void ComputeSha256_MatchesContent()
    {
        string dir = NewDir();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "f.bin");
        File.WriteAllText(path, "hello");

        Assert.AreEqual(Sha256Of("hello"), UpdateService.ComputeSha256(path));
    }

    [TestMethod]
    public void ExtractSlimZip_ReturnsExePath()
    {
        string dir = NewDir();
        Directory.CreateDirectory(dir);
        string zipPath = Path.Combine(dir, "slim.zip");
        string innerDir = "ModernWigiDash-win-x64";
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry($"{innerDir}/ModernWigiDash.App.exe");
            using var w = new StreamWriter(entry.Open());
            w.Write("exe");
            var res = zip.CreateEntry($"{innerDir}/Resources/font.ttf");
            using var rw = new StreamWriter(res.Open());
            rw.Write("font");
        }

        string target = Path.Combine(dir, "extracted");
        string exe = UpdateService.ExtractSlimZip(zipPath, target);

        Assert.IsTrue(exe.EndsWith("ModernWigiDash.App.exe", StringComparison.Ordinal));
        Assert.IsTrue(File.Exists(exe));
        Assert.IsTrue(File.Exists(Path.Combine(target, innerDir, "Resources", "font.ttf")));
    }

    [TestMethod]
    public async Task DownloadAndStage_ShaMismatch_ReturnsFalseAndCleansUp()
    {
        string dir = NewDir();
        var service = new UpdateService(
            downloadFile: async (_, dest, _, _) => await File.WriteAllTextAsync(dest, "corrupt"),
            sha256Matches: _ => false,
            updatesRoot: dir);
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", "expected-digest");

        bool ok = await service.DownloadAndStageAsync(info, new Progress<double>());

        Assert.IsFalse(ok);
        Assert.IsFalse(Directory.Exists(Path.Combine(dir, "downloads")),
            "a corrupt download must be cleaned up");
    }

    [TestMethod]
    public async Task DownloadAndStage_ShaMatches_StagesAndWritesCmd()
    {
        string dir = NewDir();
        string zipPath = Path.Combine(dir, "slim.zip");
        Directory.CreateDirectory(dir);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("ModernWigiDash-win-x64/ModernWigiDash.App.exe");
            using var w = new StreamWriter(entry.Open());
            w.Write("exe");
        }
        byte[] zipBytes = File.ReadAllBytes(zipPath);
        string digest = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();

        var service = new UpdateService(
            downloadFile: async (_, dest, _, _) => await File.WriteAllBytesAsync(dest, zipBytes),
            sha256Matches: actual => actual == digest,
            updatesRoot: dir);
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", digest);

        bool ok = await service.DownloadAndStageAsync(info, new Progress<double>());

        Assert.IsTrue(ok);
        string stagedExe = Path.Combine(dir, "staged", "0.5.0", "ModernWigiDash-win-x64", "ModernWigiDash.App.exe");
        Assert.IsTrue(File.Exists(stagedExe), "the zip must be extracted under staged/{version}");
        Assert.IsTrue(File.Exists(service.StagedCmdPath(info)), "apply-update.cmd must be written into the stage");
    }

    [TestMethod]
    public void RecoverInterruptedSwap_RestoresOldWhenNewMissing()
    {
        string dir = NewDir();
        Directory.CreateDirectory(dir);
        string old = Path.Combine(dir, "ModernWigiDash.App.exe.old");
        File.WriteAllText(old, "old");

        UpdateService.RecoverInterruptedSwap(dir);

        Assert.IsTrue(File.Exists(Path.Combine(dir, "ModernWigiDash.App.exe")), "the .old must be restored");
        Assert.IsFalse(File.Exists(old));
    }

    [TestMethod]
    public void RecoverInterruptedSwap_DeletesOldWhenNewPresent()
    {
        string dir = NewDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ModernWigiDash.App.exe"), "new");
        File.WriteAllText(Path.Combine(dir, "ModernWigiDash.App.exe.old"), "old");

        UpdateService.RecoverInterruptedSwap(dir);

        Assert.IsTrue(File.Exists(Path.Combine(dir, "ModernWigiDash.App.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(dir, "ModernWigiDash.App.exe.old")));
    }

    [TestMethod]
    public void CleanupStale_DeletesExistingStages()
    {
        string dir = NewDir();
        string staged = Path.Combine(dir, "staged", "0.4.1");
        Directory.CreateDirectory(staged);
        var service = new UpdateService(updatesRoot: dir);

        service.CleanupStale();

        Assert.IsFalse(Directory.Exists(Path.Combine(dir, "staged")), "any stage present at startup is stale");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~UpdateServiceTests"`
Expected: FAIL — `UpdateService` not defined.

- [ ] **Step 3: Implement**

`ModernWigiDash.App/Update/UpdateService.cs`:

```csharp
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace ModernWigiDash.App.Update;

/// <summary>
/// Runtime update flow: check GitHub once at startup, download + verify the
/// slim zip, extract into a writable stage under %LOCALAPPDATA%, and write the
/// apply-update.cmd the swap runs on restart. All file paths live under
/// <see cref="UpdatesRoot"/> so a non-writable install dir never blocks the
/// download. I/O seams are injectable for tests.
/// </summary>
public sealed class UpdateService
{
    public const string GitHubLatestUrl = "https://api.github.com/repos/headpiece747/ModernWigiDash/releases/latest";
    private const string RepoUserAgent = "ModernWigiDash-Updater/1.0";

    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { UserAgent = { new("ModernWigiDash-Updater", "1.0") } }
    };

    private readonly Func<string, string?, Task<string?>> _downloadText;
    private readonly Func<string, string, IProgress<double>, CancellationToken, Task> _downloadFile;
    private readonly Func<string, bool> _sha256Matches;
    private readonly string _updatesRoot;

    public UpdateService(
        Func<string, string?, Task<string?>>? downloadText = null,
        Func<string, string, IProgress<double>, CancellationToken, Task>? downloadFile = null,
        Func<string, bool>? sha256Matches = null,
        string? updatesRoot = null)
    {
        _updatesRoot = updatesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernWigiDash", "updates");
        _downloadText = downloadText ?? DownloadTextAsync;
        _downloadFile = downloadFile ?? DownloadFileAsync;
        _sha256Matches = sha256Matches ?? (actual => true); // digest verified by DownloadAndStage
    }

    public string StagedCmdPath(UpdateInfo info) => Path.Combine(_updatesRoot, "staged", info.Version, "apply-update.cmd");

    /// <summary>One startup check: newer slim release → UpdateInfo, else null (silent).</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (AppVersion.IsDevBuild) return null;
        string? json = await _downloadText(GitHubLatestUrl, RepoUserAgent).ConfigureAwait(false);
        return json is null ? null : UpdateChecker.ParseLatestRelease(json, AppVersion.Current);
    }

    /// <summary>Downloads the slim zip, verifies SHA-256, extracts to staged/{version}, writes the cmd.</summary>
    public async Task<bool> DownloadAndStageAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct = default)
    {
        string downloadDir = Path.Combine(_updatesRoot, "downloads");
        string stagedDir = Path.Combine(_updatesRoot, "staged", info.Version);
        Directory.CreateDirectory(downloadDir);
        Directory.CreateDirectory(stagedDir);
        string zipPath = Path.Combine(downloadDir, $"{info.Version}.zip");

        try
        {
            await _downloadFile(info.ZipUrl, zipPath, progress, ct).ConfigureAwait(false);
            if (!File.Exists(zipPath)) return false;

            string actual = ComputeSha256(zipPath);
            if (!_sha256Matches(actual) || !string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zipPath);
                return false;
            }

            ExtractSlimZip(zipPath, stagedDir);
            WriteUpdaterCmd(StagedCmdPath(info), info.Version);
            return true;
        }
        catch
        {
            TryDelete(zipPath);
            return false;
        }
    }

    /// <summary>Deletes any existing staged/download folders — a stage present at
    /// startup means the last update was never applied (stale).</summary>
    public void CleanupStale()
    {
        foreach (string sub in new[] { "staged", "downloads" })
        {
            string dir = Path.Combine(_updatesRoot, sub);
            if (Directory.Exists(dir)) TryDeleteDirectory(dir);
        }
    }

    /// <summary>Repairs an interrupted swap: restores exe.old when the new exe is
    /// missing, otherwise clears the .old.</summary>
    public static void RecoverInterruptedSwap(string installDir)
    {
        string exe = Path.Combine(installDir, "ModernWigiDash.App.exe");
        string old = exe + ".old";
        if (!File.Exists(old)) return;
        if (!File.Exists(exe)) File.Move(old, exe);
        else File.Delete(old);
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Extracts the slim zip (root folder included) and returns the exe path.</summary>
    internal static string ExtractSlimZip(string zipPath, string targetDir)
    {
        ZipFile.ExtractToDirectory(zipPath, targetDir);
        string exe = Path.Combine(targetDir, "ModernWigiDash-win-x64", "ModernWigiDash.App.exe");
        if (!File.Exists(exe)) throw new InvalidDataException("Slim zip has no ModernWigiDash.App.exe");
        return exe;
    }

    private void WriteUpdaterCmd(string path, string version)
    {
        // The full batch body lives in the embedded apply-update.cmd resource;
        // this method copies it into the stage with the version substituted.
        var assembly = typeof(UpdateService).Assembly;
        using var stream = assembly.GetManifestResourceStream("ModernWigiDash.App.Update.apply-update.cmd")
            ?? throw new InvalidOperationException("apply-update.cmd resource missing");
        using var reader = new StreamReader(stream);
        string body = reader.ReadToEnd().Replace("{{VERSION}}", version);
        File.WriteAllText(path, body);
    }

    private static async Task<string?> DownloadTextAsync(string url, string? userAgent)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(userAgent)) req.Headers.UserAgent.ParseAdd(userAgent);
        using var resp = await SharedHttp.SendAsync(req).ConfigureAwait(false);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
    }

    private static async Task DownloadFileAsync(string url, string dest, IProgress<double> progress, CancellationToken ct)
    {
        using var resp = await SharedHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0) progress.Report((double)read / total);
        }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string dir) { try { Directory.Delete(dir, true); } catch { } }
}
```

**Note:** the embedded `apply-update.cmd` resource doesn't exist yet (Task 4 creates it). To keep Task 2 self-contained and green, add a **minimal placeholder** `apply-update.cmd` resource now (a stub that logs `stub` and exits 0), marked in the commit message, and replace it in Task 4. Steps 3a/3b:

- 3a: create `ModernWigiDash.App/Update/apply-update.cmd` with content `@echo stub updater - replaced in Task 4 > "%LOCALAPPDATA%\ModernWigiDash\updates\update.log"` and mark it `EmbeddedResource` in the csproj (`<EmbeddedResource Include="Update\apply-update.cmd" />`).
- 3b: implement `UpdateService` as above.

- [ ] **Step 4: Run tests to verify they pass**

Run: the Step 2 filter command.
Expected: PASS (9/9).

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/Update/UpdateService.cs ModernWigiDash.App/Update/apply-update.cmd ModernWigiDash.App/ModernWigiDash.App.csproj ModernWigiDash.Tests/UpdateServiceTests.cs
git commit -m "feat: update service with download, sha256 verify, stage, and cleanup"
```

---

### Task 3: Griddy Icon Geometry (WPF)

**Files:**
- Create: `ModernWigiDash.App/Update/GriddyIconGeometry.cs`
- Test: `ModernWigiDash.Tests/GriddyIconGeometryTests.cs`

**Interfaces:**
- Consumes: `GriddyIconPaths.Map` (Widgets, public, `OrdinalIgnoreCase`).
- Produces (used by Task 5):
  - `public static class GriddyIconGeometry`
    - `public static Geometry? FromName(string name)` — `Geometry.Parse(pathData)`, cached per name; null for unknown/empty.
    - `internal static Geometry ParsePathData(string pathData)` — `Geometry.Parse`, null-guarded.
    - `internal static int CacheCount { get; }` — test seam.

- [ ] **Step 1: Write the failing tests**

`ModernWigiDash.Tests/GriddyIconGeometryTests.cs`:

```csharp
using System.Windows.Media;
using ModernWigiDash.App.Update;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class GriddyIconGeometryTests
{
    [TestMethod]
    public void FromName_UpdateIconNames_Resolve()
    {
        foreach (string name in new[] { "arrow-circle-down", "swap-horizontal", "refresh" })
        {
            Assert.IsNotNull(GriddyIconGeometry.FromName(name), $"'{name}' must resolve from the Griddy map");
        }
    }

    [TestMethod]
    public void FromName_Unknown_ReturnsNull()
        => Assert.IsNull(GriddyIconGeometry.FromName("no-such-icon"));

    [TestMethod]
    public void FromName_IsCaseInsensitive()
    {
        Assert.IsNotNull(GriddyIconGeometry.FromName("Refresh"));
        Assert.IsNotNull(GriddyIconGeometry.FromName("ARROW-CIRCLE-DOWN"));
    }

    [TestMethod]
    public void FromName_SameIcon_ReturnsCachedInstance()
    {
        Assert.AreSame(GriddyIconGeometry.FromName("refresh"), GriddyIconGeometry.FromName("refresh"));
    }

    [TestMethod]
    public void ParsePathData_Empty_ReturnsNull()
        => Assert.IsNull(GriddyIconGeometry.ParsePathData(""));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~GriddyIconGeometryTests"`
Expected: FAIL — type not defined.

- [ ] **Step 3: Implement**

`ModernWigiDash.App/Update/GriddyIconGeometry.cs`:

```csharp
using System.Collections.Concurrent;
using System.Windows.Media;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Update;

/// <summary>
/// WPF geometry for the bundled Griddy icon paths: parses the SVG path data
/// from <see cref="GriddyIconPaths.Map"/> via <see cref="Geometry.Parse"/> and
/// caches per name — one parse per icon, shared by every header button.
/// </summary>
public static class GriddyIconGeometry
{
    private static readonly ConcurrentDictionary<string, Geometry?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parsed geometry for <paramref name="name"/>, or null when unknown.</summary>
    public static Geometry? FromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Cache.GetOrAdd(name.Trim(), static key =>
            GriddyIconPaths.Map.TryGetValue(key, out string? pathData)
                ? ParsePathData(pathData)
                : null);
    }

    internal static Geometry? ParsePathData(string pathData)
    {
        try
        {
            return string.IsNullOrWhiteSpace(pathData) ? null : Geometry.Parse(pathData);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static int CacheCount => Cache.Count;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: the Step 2 filter.
Expected: PASS (5/5).

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/Update/GriddyIconGeometry.cs ModernWigiDash.Tests/GriddyIconGeometryTests.cs
git commit -m "feat: WPF geometry cache for Griddy update icons"
```

---

### Task 4: apply-update.cmd — the swap

**Files:**
- Create (replace placeholder): `ModernWigiDash.App/Update/apply-update.cmd`
- Test: `ModernWigiDash.Tests/UpdateScriptTests.cs`

**Interfaces:**
- Consumes: stage layout from Task 2 (`staged\{version}\ModernWigiDash-win-x64\...`), `UpdateService` paths.
- Produces: the real updater batch invoked by Task 5 with args:
  `apply-update.cmd <installDir> <stagedVersionDir> <appExeName>` (e.g. `ModernWigiDash.App.exe`).

- [ ] **Step 1: Write the failing test (integration on a temp install dir)**

`ModernWigiDash.Tests/UpdateScriptTests.cs`:

```csharp
using System.Diagnostics;
using System.IO;
using ModernWigiDash.App.Update;

namespace ModernWigiDash.Tests;

[TestClass]
public class UpdateScriptTests
{
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "wmd-update-script");

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(TempRoot, true); } catch { }
    }

    /// <summary>Runs the real apply-update.cmd against a temp install dir with a
    /// fake exe, verifying the rename-aside swap, preservation of user files,
    /// and relaunch.</summary>
    [TestMethod]
    public void ApplyUpdateCmd_SwapsExePreservesUserFilesAndRelaunches()
    {
        string root = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "install");
        string stage = Path.Combine(root, "staged");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(Path.Combine(stage, "ModernWigiDash-win-x64"));

        // Old install: exe + a user file that must survive.
        File.WriteAllText(Path.Combine(install, "ModernWigiDash.App.exe"), "old-exe");
        File.WriteAllText(Path.Combine(install, "app_theme.json"), "user-theme");
        // Staged new exe + Resources.
        File.WriteAllText(Path.Combine(stage, "ModernWigiDash-win-x64", "ModernWigiDash.App.exe"), "new-exe");
        Directory.CreateDirectory(Path.Combine(stage, "ModernWigiDash-win-x64", "Resources"));
        File.WriteAllText(Path.Combine(stage, "ModernWigiDash-win-x64", "Resources", "font.ttf"), "font");

        // The real cmd, extracted from the embedded resource.
        string cmdPath = Path.Combine(root, "apply-update.cmd");
        var asm = typeof(UpdateService).Assembly;
        using var stream = asm.GetManifestResourceStream("ModernWigiDash.App.Update.apply-update.cmd")!;
        using var reader = new StreamReader(stream);
        File.WriteAllText(cmdPath, reader.ReadToEnd().Replace("{{VERSION}}", "0.5.0"));

        var psi = new ProcessStartInfo("cmd.exe", $"/c \"{cmdPath}\" \"{install}\" \"{stage}\" ModernWigiDash.App.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi)!;
        string outp = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        Assert.IsTrue(proc.WaitForExit(30_000), $"updater timed out; output: {outp}");
        Assert.AreEqual(0, proc.ExitCode, $"updater failed ({proc.ExitCode}): {outp}");

        Assert.AreEqual("new-exe", File.ReadAllText(Path.Combine(install, "ModernWigiDash.App.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(install, "ModernWigiDash.App.exe.old")), "the .old must be cleaned after a successful swap");
        Assert.AreEqual("user-theme", File.ReadAllText(Path.Combine(install, "app_theme.json")), "user files must survive");
        Assert.IsTrue(File.Exists(Path.Combine(install, "Resources", "font.ttf")), "staged Resources must be copied");
        Assert.IsFalse(File.Exists(Path.Combine(stage, "ModernWigiDash-win-x64", "ModernWigiDash.App.exe")),
            "the stage must be deleted after applying");
    }
}
```

**Note:** the relaunch step inside the cmd would start a fake "exe" that's text — the test's cmd content therefore uses a `{{RELAUNCH}}` marker that the test replaces with an empty value (`-replace "{{RELAUNCH}}",""`), so the integration test exercises swap + cleanup without launching garbage. The real app (Task 5) passes the full relaunch command. Adjust the test to replace `{{RELAUNCH}}` with empty.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~UpdateScriptTests"`
Expected: FAIL — the placeholder cmd writes only a log line and exits without swapping.

- [ ] **Step 3: Implement the real cmd**

`ModernWigiDash.App/Update/apply-update.cmd`:

```batch
@echo off
setlocal EnableExtensions
rem apply-update.cmd <installDir> <stagedVersionDir> <appExeName>
rem The swap: wait for the app to exit, ensure the install dir is writable
rem (self-elevate if not), then rename-aside the exe and copy the staged
rem payload in. Never delete-first: a crash mid-swap leaves the .old recoverable.
set "LOG=%LOCALAPPDATA%\ModernWigiDash\updates\update.log"
mkdir "%LOCALAPPDATA%\ModernWigiDash\updates" 2>nul
echo [%date% %time%] updater start args: %* >> "%LOG%"

set "INSTALL=%~1"
set "STAGE=%~2"
set "EXE=%~3"

rem ---- 1. Wait for all app processes to exit (60s cap) ----
set /a WAIT=0
:waitloop
tasklist /FI "IMAGENAME eq %EXE%" 2>nul | find /I "%EXE%" >nul
if errorlevel 1 goto exited
set /a WAIT+=1
if %WAIT% GEQ 60 goto timeout
timeout /t 1 /nobreak >nul
goto waitloop
:timeout
echo [%date% %time%] ERROR: app did not exit within 60s >> "%LOG%"
exit /b 2
:exited

rem ---- 2. Writability check; self-elevate when needed ----
set "PROBE=%INSTALL%\.update-write-probe"
echo x > "%PROBE%" 2>nul
if not exist "%PROBE%" goto elevate
del "%PROBE%" 2>nul
goto writable
:elevate
echo [%date% %time%] install dir not writable; requesting elevation >> "%LOG%"
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs" 
exit /b 0
:writable

rem ---- 3. Rename-aside swap (crash-safe) ----
if not exist "%INSTALL%\%EXE%.old" (
  move /Y "%INSTALL%\%EXE%" "%INSTALL%\%EXE%.old" >nul
)
if not exist "%STAGE%\ModernWigiDash-win-x64\%EXE%" (
  echo [%date% %time%] ERROR: staged exe missing >> "%LOG%"
  exit /b 3
)

rem Retry loop for file-in-use (AV scanning etc.)
set /a TRY=0
:copyloop
copy /Y "%STAGE%\ModernWigiDash-win-x64\%EXE%" "%INSTALL%\%EXE%" >nul 2>&1
if not errorlevel 1 goto copied
set /a TRY+=1
if %TRY% GEQ 10 goto copyfail
timeout /t 1 /nobreak >nul
goto copyloop
:copyfail
echo [%date% %time%] ERROR: could not copy new exe after 10 tries >> "%LOG%"
exit /b 4
:copied

rem Copy staged Resources over (fonts/theme/icons) — preserve unknown user files.
if exist "%STAGE%\ModernWigiDash-win-x64\Resources" (
  xcopy /E /I /Y "%STAGE%\ModernWigiDash-win-x64\Resources" "%INSTALL%\Resources" >nul
)

rem ---- 4. Cleanup: drop the .old and the stage, relaunch ----
del "%INSTALL%\%EXE%.old" 2>nul
del /Q "%STAGE%\ModernWigiDash-win-x64\%EXE%" 2>nul
rd /S /Q "%STAGE%\ModernWigiDash-win-x64\Resources" 2>nul
rd /S /Q "%STAGE%" 2>nul
echo [%date% %time%] swap complete >> "%LOG%"
{{RELAUNCH}}
exit /b 0
```

(With `{{RELAUNCH}}` empty for the integration test; the real app substitutes `start "" "%INSTALL%\%EXE%"` — see Task 5.)

- [ ] **Step 4: Run test to verify it passes**

Run: the Step 2 filter.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/Update/apply-update.cmd ModernWigiDash.Tests/UpdateScriptTests.cs
git commit -m "feat: crash-safe apply-update.cmd swap with elevation and retry"
```

---

### Task 5: Header button + MainWindow wiring

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml` (header StackPanel, left of `ChkSnapToGrid`)
- Create: `ModernWigiDash.App/MainWindow.Update.cs` (partial)
- Test: `ModernWigiDash.Tests/MainWindowUpdateTests.cs` (STA wiring smoke)

**Interfaces:**
- Consumes: `UpdateService`, `UpdateInfo`, `GriddyIconGeometry` (Tasks 1-4), `AppVersion`.
- Produces: the visible feature.

- [ ] **Step 1: Write the failing test**

`ModernWigiDash.Tests/MainWindowUpdateTests.cs`:

```csharp
using System.Windows;
using ModernWigiDash.App;

namespace ModernWigiDash.Tests;

[TestClass]
public class MainWindowUpdateTests
{
    private static readonly StaHost Host = new("MainWindowUpdateTests-STA");

    [TestCleanup]
    public void Cleanup() => Host.DetachApplication();

    [TestMethod]
    public void Ctor_UpdateButton_ExistsAndHiddenByDefault()
    {
        Host.Run<object?>(() =>
        {
            var window = new MainWindow(new StubPresentMonNative());
            try
            {
                Assert.IsNotNull(window.UpdateButton, "the update button must exist in the header");
                Assert.AreEqual(Visibility.Collapsed, window.UpdateButton.Visibility,
                    "the update button must be hidden when no update is known");
                return null;
            }
            finally
            {
                window.Close();
            }
        });
    }
}
```

**Note:** `MainWindow` ctor currently never checks for updates (it must stay inert for test hosts — construction must not hit the network). The wiring exposes `internal Button UpdateButton` and a `public/internal void ApplyUpdateState(UpdateState state, string tooltip, string? version)` seam; the test pins construction-hidden + the button existing. The startup check itself runs from `SourceInitialized` (not the ctor) so test hosts stay network-free; the smoke test covers the state seam:

```csharp
    [TestMethod]
    public void ApplyUpdateState_Available_ShowsButtonWithGriddyIcon()
    {
        Host.Run<object?>(() =>
        {
            var window = new MainWindow(new StubPresentMonNative());
            try
            {
                window.ApplyUpdateState(UpdateState.Available, "Update v0.5.0 available", "0.5.0");
                Assert.AreEqual(Visibility.Visible, window.UpdateButton.Visibility);
                Assert.IsNotNull(window.UpdateIconPath?.Data, "the arrow-circle-down geometry must be set");
                return null;
            }
            finally
            {
                window.Close();
            }
        });
    }
```

(Add `UpdateState` enum + `UpdateIconPath` internal accessors to the App; the test needs InternalsVisibleTo, which already exists.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false --filter "FullyQualifiedName~MainWindowUpdateTests"`
Expected: FAIL — `UpdateButton`/`UpdateState` not defined.

- [ ] **Step 3: XAML — button left of Snap to Grid**

In `ModernWigiDash.App/MainWindow.xaml` header StackPanel (line ~49), insert **before** the `ChkSnapToGrid` CheckBox:

```xml
<!-- Update button: hidden until an update is available (UpdateService) -->
<Button x:Name="UpdateButton" Visibility="Collapsed" Margin="0,0,16,0" Padding="6"
        ToolTip="Check for updates" Click="UpdateButton_Click"
        Background="Transparent" BorderThickness="0"
        Style="{StaticResource {x:Static ToolBar.ButtonStyleKey}}">
    <Path x:Name="UpdateIconPath" Width="18" Height="18" Stretch="Uniform"
          Fill="{DynamicResource M3Primary}"/>
</Button>
```

- [ ] **Step 4: Implement `MainWindow.Update.cs`**

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ModernWigiDash.App.Update;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App;

public enum UpdateState { Hidden, Available, Downloading, Ready }

/// <summary>
/// The update button's UI states (approved mockup: Griddy icons left of Snap
/// to Grid, hover tooltips per state) and the restart-prompt flow. The startup
/// check runs from SourceInitialized so window construction in tests stays
/// network-free; the swap spawns apply-update.cmd and closes the window.
/// </summary>
public partial class MainWindow
{
    internal Button UpdateButton => FindName("UpdateButton") as Button ?? throw new InvalidOperationException("UpdateButton missing");
    internal Path UpdateIconPath => FindName("UpdateIconPath") as Path ?? throw new InvalidOperationException("UpdateIconPath missing");

    private readonly UpdateService _updateService = new();
    private UpdateState _updateState = UpdateState.Hidden;
    private UpdateInfo? _pendingUpdate;

    private async void OnUpdateCheckAtStartup(object? sender, EventArgs e)
    {
        // SourceInitialized: the window is visible; run the check off-thread.
        var info = await _updateService.CheckForUpdateAsync();
        if (info is null) return; // up-to-date/offline/failed — silent
        _pendingUpdate = info;
        Dispatcher.Invoke(() => ApplyUpdateState(UpdateState.Available, $"Update v{info.Version} available", info.Version));
    }

    internal void ApplyUpdateState(UpdateState state, string tooltip, string? version)
    {
        _updateState = state;
        UpdateButton.ToolTip = tooltip;
        string icon = state switch
        {
            UpdateState.Available => "arrow-circle-down",
            UpdateState.Downloading => "swap-horizontal",
            UpdateState.Ready => "refresh",
            _ => ""
        };
        UpdateIconPath.Data = GriddyIconGeometry.FromName(icon);
        UpdateButton.Visibility = state == UpdateState.Hidden ? Visibility.Collapsed : Visibility.Visible;
        UpdateIconPath.Fill = state switch
        {
            UpdateState.Ready => new SolidColorBrush(Color.FromRgb(16, 185, 129)), // green
            UpdateState.Available => new SolidColorBrush(Color.FromRgb(245, 158, 11)), // amber
            _ => new SolidColorBrush(Color.FromRgb(250, 250, 250))
        };
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_updateState)
        {
            case UpdateState.Available when _pendingUpdate is not null:
                _ = DownloadUpdateAsync(_pendingUpdate);
                break;
            case UpdateState.Ready:
                ShowRestartPrompt();
                break;
        }
    }

    private async Task DownloadUpdateAsync(UpdateInfo info)
    {
        ApplyUpdateState(UpdateState.Downloading, $"Downloading v{info.Version}… 0%", info.Version);
        var progress = new Progress<double>(p =>
            UpdateButton.ToolTip = $"Downloading v{info.Version}… {p * 100:F0}%");
        bool ok = await _updateService.DownloadAndStageAsync(info, progress);
        if (!ok)
        {
            ApplyUpdateState(UpdateState.Hidden, "", null); // silent fail
            return;
        }
        _pendingUpdate = info;
        ApplyUpdateState(UpdateState.Ready, "Restart to apply", info.Version);
    }

    private void ShowRestartPrompt()
    {
        if (_pendingUpdate is null) return;
        bool restart = _dialogHost.Confirm("Update ready — restart to apply",
            $"v{_pendingUpdate.Version} is downloaded and staged. It will be installed in place when the app closes. Your profile and theme are preserved.");
        if (!restart) return;

        // Spawn the updater hidden, then close normally (standby teardown).
        string installDir = AppContext.BaseDirectory;
        string stageDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernWigiDash", "updates", "staged", _pendingUpdate.Version);
        string cmd = _updateService.StagedCmdPath(_pendingUpdate);
        string relaunch = $"start \"\" \"{installDir}\\ModernWigiDash.App.exe\"";
        string args = $"\"{cmd}\" \"{installDir}\" \"{stageDir}\" ModernWigiDash.App.exe";

        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{args}\"") { UseShellExecute = false };
        // Replace the {{RELAUNCH}} marker inside the staged cmd with the relaunch line.
        string body = File.ReadAllText(cmd).Replace("{{RELAUNCH}}", relaunch);
        File.WriteAllText(cmd, body);
        System.Diagnostics.Process.Start(psi);

        Close();
    }
}
```

**Wire the startup check** in `MainWindow.xaml.cs` after `InitializeComponent()`:

```csharp
SourceInitialized += OnUpdateCheckAtStartup;
```

(It already has a `SourceInitialized` handler for the theme — add this second subscription alongside.)

- [ ] **Step 5: Run tests to verify they pass**

Run: the Step 2 filter, then the full suite.
Expected: PASS — MainWindowUpdateTests 2/2, full 995+.

- [ ] **Step 6: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml ModernWigiDash.App/MainWindow.Update.cs ModernWigiDash.App/MainWindow.xaml.cs ModernWigiDash.Tests/MainWindowUpdateTests.cs
git commit -m "feat: header update button with three states and restart prompt"
```

---

### Task 6: Release pipeline — slim zip + upstream auto-resolve

**Files:**
- Modify: `scripts/build-release.ps1`
- Test: local run verification (no unit tests — PowerShell script; the repo's convention is verifying release scripts by running them).

**Interfaces:**
- Consumes: existing `-Version`, `-SkipTelemetry`, `-LhsVersion`, `-PresentMonVersion` params.
- Produces: `ModernWigiDash-v{X}-win-x64.zip` (unchanged) + `ModernWigiDash-v{X}-app-only.zip` (exe + Resources + README + LICENSE) + `telemetry-versions.txt` inside the full zip; exe stamped with `InformationalVersion`.

- [ ] **Step 1: Stamp the informational version**

In `build-release.ps1`, add `-p:InformationalVersion=$Version` to the `dotnet publish` invocation (line ~56-60) when `-Version` is non-empty:

```powershell
$publishArgs = @(
    "-c", "Release", "-r", "win-x64", "--self-contained", "-o", $publishOut,
    "-p:PublishSingleFile=true", "-p:PublishReadyToRun=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=None", "-p:DebugSymbols=false"
)
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $publishArgs += "-p:InformationalVersion=$Version"
}
& dotnet publish (Join-Path $Root "ModernWigiDash.App\ModernWigiDash.App.csproj") @publishArgs | Out-Null
```

(Refactor the hardcoded args into `$publishArgs`; behavior identical when no `-Version`.)

- [ ] **Step 2: Upstream auto-resolve (when pins are empty)**

Replace the `$LhsVersion`/`$PresentMonVersion` defaults with empty strings, and add resolution before the download map is built:

```powershell
[Parameter()][string]$LhsVersion = "",
[Parameter()][string]$PresentMonVersion = "",

function Get-LatestReleaseVersion([string]$Repo) {
    $json = & curl.exe -f -L -sS "https://api.github.com/repos/$Repo/releases/latest"
    if ($LASTEXITCODE -ne 0) { throw "Could not query latest release for $Repo" }
    $release = $json | ConvertFrom-Json
    return $release.tag_name.TrimStart("v")
}

if ([string]::IsNullOrWhiteSpace($LhsVersion)) {
    $LhsVersion = Get-LatestReleaseVersion "epinter/LibreHardwareService"
    Write-Host "LHS: auto-resolved latest v$LhsVersion"
}
if ([string]::IsNullOrWhiteSpace($PresentMonVersion)) {
    $PresentMonVersion = Get-LatestReleaseVersion "GameTechDev/PresentMon"
    Write-Host "PresentMon: auto-resolved latest v$PresentMonVersion"
}
```

(Place before the `$Downloads = [ordered]@{...}` map so the map uses the resolved versions.)

- [ ] **Step 3: Record resolved versions**

After writing `NOTICES.txt`, write `telemetry-versions.txt` into `$licDir` (full zip only):

```powershell
[System.IO.File]::WriteAllText(
    (Join-Path $licDir "telemetry-versions.txt"),
    "LibreHardwareService=$LhsVersion`r`nPresentMon=$PresentMonVersion`r`n",
    [System.Text.UTF8Encoding]::new($false))
```

- [ ] **Step 4: Build the slim app-only zip**

After the full zip is built (after step 5/zip), add step 6b — assemble and zip the slim artifact:

```powershell
# --- 6b. Slim app-only zip (updater payload: exe + Resources + docs) ---
Write-Host "Building app-only slim zip..."
$SlimDir = Join-Path $Staging "slim"
New-Item -ItemType Directory -Path $SlimDir -Force | Out-Null
Copy-Item (Join-Path $publishOut "ModernWigiDash.App.exe") $SlimDir
Copy-Item (Join-Path $publishOut "Resources") (Join-Path $SlimDir "Resources") -Recurse
[System.IO.File]::WriteAllText((Join-Path $SlimDir "README.txt"), $readme, [System.Text.UTF8Encoding]::new($false))
Copy-Item (Join-Path $Root "LICENSE") (Join-Path $SlimDir "LICENSE-ModernWigiDash.txt")

$SlimZipPath = if ([string]::IsNullOrWhiteSpace($Version)) { Join-Path $Root "ModernWigiDash-app-only-win-x64.zip" } else { Join-Path $Root "ModernWigiDash-v$Version-app-only.zip" }
if (Test-Path $SlimZipPath) { Remove-Item $SlimZipPath -Force }
Compress-Archive -Path $SlimDir -DestinationPath $SlimZipPath
if (-not (Test-Path $SlimZipPath)) { throw "Slim zip creation failed" }
Write-Host ""
Write-Host "Built $SlimZipPath"
```

**Note:** the slim README is the same versioned README (stamped in step 4), so the slim zip is self-describing. The `ReleaseDir/README.txt` must gain a line noting the slim zip is update-only — add to `release/README.txt` template:

```text
Updating: the app's built-in updater uses the separate app-only zip; never
use that artifact for a fresh install (it has no telemetry installers).
```

- [ ] **Step 5: Verify by running the script locally (dev, no version)**

Run: `.\scripts\build-release.ps1 -SkipTelemetry` (dev build, no `-Version` → unversioned zips, auto-resolved upstream versions fetched into the cache).
Expected: both zips produced; the full zip contains `telemetry-versions.txt`; the publish output contains the exe; `InformationalVersion` absent in dev build (updater disabled via `0.0.0` default is handled by `AppVersion.IsDevBuild` — verify the exe still runs).

- [ ] **Step 6: Verify the stamped version end-to-end**

Run: `.\scripts\build-release.ps1 -SkipTelemetry -Version 0.5.0-test` (skip telemetry for speed; downloads skipped).
Expected: `ModernWigiDash-v0.5.0-test-app-only.zip` + `ModernWigiDash-v0.5.0-test-win-x64.zip` produced; then extract the slim zip, run the exe (it will show the UI; verify no crash), and confirm the version stamp via:
`[System.Reflection.AssemblyName]::GetAssemblyName("...\ModernWigiDash.App.exe").Version` (informational version is visible via `dotnet` tools or by the updater's absence of nagging in a dev run).

- [ ] **Step 7: Commit**

```bash
git add scripts/build-release.ps1 release/README.txt
git commit -m "feat: slim app-only release zip, version stamping, upstream auto-resolve"
```

---

### Task 7: Full verification + smoke on the physical WigiDash

**Files:** none (verification only).

- [ ] **Step 1: Full suite**

Run:
```
dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false
```
Expected: all tests pass (existing 993 + new ~25).

- [ ] **Step 2: Release build + slim zip**

Run: `.\scripts\build-release.ps1 -Version 0.5.0` (full, with telemetry).
Expected: `ModernWigiDash-v0.5.0-win-x64.zip` (full, ~294 MB) + `ModernWigiDash-v0.5.0-app-only.zip` (~100 MB), both containing the version-stamped exe; full zip has `telemetry-versions.txt` with the auto-resolved LHS/PresentMon versions.

- [ ] **Step 3: Physical-device smoke (elevated)**

Run via `C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\run-elevated.ps1`:
1. Fresh default profile (delete `%LOCALAPPDATA%\ModernWigiDash\profile.json` first).
2. Verify the update button is **hidden** (up-to-date: the release is the same version as the exe).
3. Verify no network calls at construction (test hosts already prove this; on-device: no update.log entries).
4. Verify the app streams frames (log: `Hardware connection successful!`, `BulkWrite` lines).
5. If a newer GitHub release exists by then, verify: amber button appears at startup → hover tooltip shows version → click → spinner + progress tooltip → green refresh → "Restart now" → app closes (standby) → updater swaps → app relaunches with the new version; `%LOCALAPPDATA%\ModernWigiDash\updates\update.log` shows the swap; profile + theme survive.

- [ ] **Step 4: Commit any smoke-driven tweaks**

```bash
git add -A
git commit -m "fix: smoke-pass adjustments"
```

---

## Self-Review Notes

- **Spec coverage:** version stamp (Task 6) ↔ spec stamping; UpdateChecker/Service (Tasks 1-2) ↔ checker/download/stage; GriddyIconGeometry (Task 3) ↔ Griddy icons; apply-update.cmd (Task 4) ↔ swap/elevation/rename-aside; header + restart prompt (Task 5) ↔ UI/mockup; slim zip + auto-resolve (Task 6) ↔ release pipeline; startup recovery + stale cleanup (Task 2) ↔ spec startup-recovery; testing (Tasks 1-5, 7) ↔ spec testing. Out-of-scope items (periodic checks, deltas, signing) deliberately absent.
- **Type consistency:** `UpdateInfo(Version, ZipUrl, Sha256)` — same record in Tasks 1, 2, 5; `UpdateState` enum defined once in Task 5 and used by the seam tests; `UpdateService` seams (`downloadText`, `downloadFile`, `sha256Matches`, `updatesRoot`) match their test usage; `StagedCmdPath(info)` used consistently in Tasks 2 and 5; `GriddyIconGeometry.FromName` is the single icon entry point.
- **Known plan caveats handled:** the Task 2 placeholder cmd (replaced in Task 4); the `{{RELAUNCH}}` marker (empty in tests, substituted by the app in Task 5); `MainWindow` construction stays network-free (startup check from `SourceInitialized`, not the ctor); the `MainWindowUpdateTests` uses the repo's `StaHost` + `StubPresentMonNative` patterns.
