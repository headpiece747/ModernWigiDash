using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
            { "name": "ModernWigiDash-v1.1.0-app-only.zip", "browser_download_url": "https://github.com/headpiece747/ModernWigiDash/releases/download/v1.1.0/app.zip", "digest": "sha256:abc" } ] }
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
        bool downloadCalled = false;
        var service = new UpdateService(
            downloadText: (_, _) =>
            {
                downloadCalled = true;
                return Task.FromResult<string?>(null);
            },
            updatesRoot: NewDir(),
            currentVersion: new Version(1, 0, 0)); // pin a release version so the null-version short-circuit can't mask the HTTP path

        Assert.IsNull(await service.CheckForUpdateAsync());
        Assert.IsTrue(downloadCalled, "the HTTP failure path must be exercised, not skipped by a null dev version");
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
    public void ExtractSlimZip_EscapingEntry_ThrowsAndWritesNothingOutside()
    {
        string dir = NewDir();
        Directory.CreateDirectory(dir);
        string zipPath = Path.Combine(dir, "evil.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../escape.txt");
            using (var w = new StreamWriter(entry.Open()))
            {
                w.Write("escape");
            }
        }

        string target = Path.Combine(dir, "extracted");
        Assert.ThrowsExactly<InvalidDataException>(() => UpdateService.ExtractSlimZip(zipPath, target),
            "A zip entry escaping the stage directory must be rejected");
        Assert.IsFalse(File.Exists(Path.Combine(dir, "escape.txt")), "Nothing may be written outside the stage");
    }

    [TestMethod]
    public void EnforceDownloadCap_OverCap_Throws()
    {
        // The boundary itself must pass (no throw); one byte over must throw.
        UpdateService.EnforceDownloadCap(UpdateService.MaxUpdateBytes);
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => UpdateService.EnforceDownloadCap(UpdateService.MaxUpdateBytes + 1));
        StringAssert.Contains(ex.Message, "size cap", "The failure must name the cap so the log line is diagnostic");
    }

    [TestMethod]
    public async Task DownloadAndStage_ShaMismatch_ReturnsFalseAndCleansUp()
    {
        string dir = NewDir();
        var service = new UpdateService(
            downloadFile: async (_, dest, _, _) => await File.WriteAllTextAsync(dest, "corrupt").ConfigureAwait(false),
            sha256Matches: (_, _) => false,
            updatesRoot: dir);
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", "expected-digest");

        bool ok = await service.DownloadAndStageAsync(info, new Progress<double>()).ConfigureAwait(false);

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
        using (var zip = await ZipFile.OpenAsync(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("ModernWigiDash-win-x64/ModernWigiDash.App.exe");
            using var w = new StreamWriter(await entry.OpenAsync());
            await w.WriteAsync("exe");
        }
#pragma warning restore S6966
        byte[] zipBytes = await File.ReadAllBytesAsync(zipPath);
        string digest = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();

        var service = new UpdateService(
            downloadFile: async (_, dest, _, _) => await File.WriteAllBytesAsync(dest, zipBytes).ConfigureAwait(false),
            sha256Matches: (actual, expected) => actual == expected,
            updatesRoot: dir);
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", digest);

        bool ok = await service.DownloadAndStageAsync(info, new Progress<double>()).ConfigureAwait(false);

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

    [TestMethod]
    public void LaunchUpdater_SubstitutesRelaunchAndSpawns()
    {
        string dir = NewDir();
        string stageDir = Path.Combine(dir, "staged", "0.5.0");
        Directory.CreateDirectory(stageDir);
        // Production stages the cmd with {{VERSION}} already substituted
        // (WriteUpdaterCmd); LaunchUpdater resolves only {{RELAUNCH}}.
        string stagedCmd = Path.Combine(stageDir, "apply-update.cmd");
        File.WriteAllText(stagedCmd, "echo 0.5.0\r\n{{RELAUNCH}}\r\n");
        // Production stamps the staged cmd's hash at staging
        // (WriteUpdaterCmd); LaunchUpdater refuses a launch without a
        // matching stamp.
        File.WriteAllText(
            UpdateService.StagedCmdHashPath(stagedCmd),
            UpdateService.ComputeSha256(stagedCmd));

        ProcessStartInfo? started = null;
        var service = new UpdateService(
            updatesRoot: dir,
            startProcess: psi =>
            {
                started = psi;
                return null;
            });
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", "digest");
        string installDir = Path.Combine(Path.GetTempPath(), "wmd-install");

        bool ok = service.LaunchUpdater(info, installDir);

        Assert.IsTrue(ok);
        string liveCmd = Path.Combine(dir, "apply-update-live.cmd");
        Assert.IsTrue(File.Exists(liveCmd), "the substituted cmd must be written outside the stage");
        string body = File.ReadAllText(liveCmd);
        Assert.IsTrue(body.Contains("echo 0.5.0"), "{{VERSION}} must be substituted");
        Assert.IsTrue(body.Contains($"start \"\" \"{Path.Combine(installDir, "ModernWigiDash.App.exe")}\""),
            "{{RELAUNCH}} must be substituted with the install-dir relaunch line");
        Assert.IsFalse(body.Contains("{{RELAUNCH}}"));
        Assert.IsNotNull(started, "the launch seam must be invoked");
        Assert.IsTrue(started.UseShellExecute, "ShellExecute detaches the updater from the app's job object");
        Assert.IsTrue(started.Arguments.Contains(installDir), "the install dir must reach the updater");
        Assert.IsTrue(started.Arguments.Contains(stageDir), "the stage dir must reach the updater");
    }

    [TestMethod]
    public void LaunchUpdater_MissingStagedCmd_ReturnsFalse()
    {
        string dir = NewDir();
        int spawns = 0;
        var service = new UpdateService(
            updatesRoot: dir,
            startProcess: _ =>
            {
                spawns++;
                return null;
            });
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", "digest");

        bool ok = service.LaunchUpdater(info, Path.Combine(Path.GetTempPath(), "wmd-install"));

        Assert.IsFalse(ok, "a vanished stage must fail the launch, not the UI thread");
        Assert.AreEqual(0, spawns);
        Assert.IsFalse(File.Exists(Path.Combine(dir, "apply-update-live.cmd")));
    }

    [TestMethod]
    public void LaunchUpdater_TamperedStagedCmd_ReturnsFalse()
    {
        string dir = NewDir();
        string stageDir = Path.Combine(dir, "staged", "0.5.0");
        Directory.CreateDirectory(stageDir);
        string stagedCmd = Path.Combine(stageDir, "apply-update.cmd");
        File.WriteAllText(stagedCmd, "echo 0.5.0\r\n{{RELAUNCH}}\r\n");
        // A stamp that does not match the staged content: a local writer
        // swapped the file between staging and launch.
        File.WriteAllText(
            UpdateService.StagedCmdHashPath(stagedCmd),
            "0000000000000000000000000000000000000000000000000000000000000000");
        int spawns = 0;
        var service = new UpdateService(
            updatesRoot: dir,
            startProcess: _ =>
            {
                spawns++;
                return null;
            });
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", "digest");

        bool ok = service.LaunchUpdater(info, Path.Combine(Path.GetTempPath(), "wmd-install"));

        Assert.IsFalse(ok, "a tampered staged cmd must fail the launch, not the UI thread");
        Assert.AreEqual(0, spawns);
        Assert.IsFalse(File.Exists(Path.Combine(dir, "apply-update-live.cmd")),
            "no live cmd may be written from a tampered stage");
    }

    [TestMethod]
    public void RecoverAtStartup_HealsInterruptedSwapAndCleansStale()
    {
        string dir = NewDir();
        string installDir = Path.Combine(dir, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "ModernWigiDash.App.exe.old"), "old-exe");
        string staged = Path.Combine(dir, "staged", "0.5.0");
        Directory.CreateDirectory(staged);
        var service = new UpdateService(updatesRoot: dir);

        service.RecoverAtStartup(installDir);

        Assert.IsTrue(File.Exists(Path.Combine(installDir, "ModernWigiDash.App.exe")),
            "an interrupted swap must be restored at startup");
        Assert.IsFalse(File.Exists(Path.Combine(installDir, "ModernWigiDash.App.exe.old")));
        Assert.IsFalse(Directory.Exists(Path.Combine(dir, "staged")), "stale stages must be cleaned at startup");
    }
}
