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
        try { Directory.Delete(TempRoot, true); } catch { /* best-effort */ }
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
        { "tag_name": "v1.1.0", "assets": [
            { "name": "ModernWigiDash-v1.1.0-app-only.zip", "browser_download_url": "https://x/app.zip", "digest": "abc" } ] }
        """;
        var service = new UpdateService(
            downloadText: (_, _) => Task.FromResult<string?>(json),
            updatesRoot: NewDir(),
            currentVersion: new Version(1, 0, 0)); // the real stamp is a dev 0.0.0 — pin a release version

        var info = await service.CheckForUpdateAsync();

        Assert.IsNotNull(info);
        Assert.AreEqual("1.1.0", info.Version);
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
            using (var w = new StreamWriter(entry.Open()))
            {
                w.Write("exe");
            }
            var res = zip.CreateEntry($"{innerDir}/Resources/font.ttf");
            using (var rw = new StreamWriter(res.Open()))
            {
                rw.Write("font");
            }
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
        // S6966 suppressed: ZipArchive has no OpenAsync/Entry.OpenAsync variants.
#pragma warning disable S6966
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("ModernWigiDash-win-x64/ModernWigiDash.App.exe");
            using var w = new StreamWriter(entry.Open());
            await w.WriteAsync("exe");
        }
#pragma warning restore S6966
        byte[] zipBytes = await File.ReadAllBytesAsync(zipPath);
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
    public async Task DownloadAndStage_CancelledMidDownload_ReturnsFalse()
    {
        // The 15-minute stall bound is wired through the download token: a
        // download seam that never completes must be cut off when the caller
        // (or the bound) cancels — the method returns false, never hangs.
        string dir = NewDir();
        var service = new UpdateService(
            downloadFile: async (_, _, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            },
            updatesRoot: dir);
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", "digest");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        bool ok = await service.DownloadAndStageAsync(info, new Progress<double>(), cts.Token);

        Assert.IsFalse(ok);
        Assert.IsFalse(Directory.Exists(Path.Combine(dir, "downloads")),
            "a cancelled download must be cleaned up");
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
