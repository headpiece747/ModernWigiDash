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
    public void DigestMatches_SameValueDifferentCase_ReturnsTrue()
    {
        string lower = Sha256Of("hello");
        string upper = lower.ToUpperInvariant();

        Assert.IsTrue(UpdateService.DigestMatches(lower, upper));
        Assert.IsTrue(UpdateService.DigestMatches(upper, lower));
    }

    [TestMethod]
    public void DigestMatches_DifferentDigest_ReturnsFalse()
    {
        Assert.IsFalse(UpdateService.DigestMatches(Sha256Of("hello"), Sha256Of("world")));
    }

    [TestMethod]
    public void DigestMatches_NonHexInput_ReturnsFalse()
    {
        // A tampered stamp file or a non-hex expected digest is a mismatch,
        // never a throw: the caller's mismatch path logs and fails closed.
        Assert.IsFalse(UpdateService.DigestMatches(Sha256Of("hello"), "not-a-hex-digest"));
        Assert.IsFalse(UpdateService.DigestMatches(Sha256Of("hello"), Sha256Of("hello")[..63]));
        Assert.IsFalse(UpdateService.DigestMatches(Sha256Of("hello") + "00", Sha256Of("hello")));
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
    public void RunStartupPhase_RemovesAStaleSwapLock()
    {
        // A crashed updater can leave the swap lock behind; startup clears it
        // before any update check, so it can never block a future update.
        string dir = NewDir();
        string installDir = Path.Combine(dir, "install");
        string lockDir = Path.Combine(installDir, ".update-running");
        Directory.CreateDirectory(lockDir);
        var service = new UpdateService(updatesRoot: dir);

        service.RunStartupPhase(installDir);

        Assert.IsFalse(Directory.Exists(lockDir), "startup must drop a stale swap lock");
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
    public async Task DownloadAndStage_DoesNotLaunchTheUpdater()
    {
        // The download path must never launch the updater: the restart prompt
        // is the single launch site, and a second launcher races the swap
        // (the v0.9.3/v0.9.4 double-updater defect).
        string dir = NewDir();
        string zipPath = Path.Combine(dir, "slim.zip");
        Directory.CreateDirectory(dir);
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

        int spawns = 0;
        var service = new UpdateService(
            downloadFile: async (_, dest, _, _) => await File.WriteAllBytesAsync(dest, zipBytes).ConfigureAwait(false),
            sha256Matches: (actual, expected) => actual == expected,
            startProcess: _ => { spawns++; return null; },
            updatesRoot: dir);
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", digest);

        bool ok = await service.DownloadAndStageAsync(info, new Progress<double>()).ConfigureAwait(false);

        Assert.IsTrue(ok, "a clean download must stage");
        Assert.AreEqual(0, spawns, "the download path must not launch the updater");
    }

    [TestMethod]
    public async Task DownloadAndStage_FailedStage_DoesNotLaunch()
    {
        string dir = NewDir();
        int spawns = 0;
        var service = new UpdateService(
            downloadFile: async (_, dest, _, _) => await File.WriteAllTextAsync(dest, "corrupt").ConfigureAwait(false),
            sha256Matches: (_, _) => false,
            startProcess: _ => { spawns++; return null; },
            updatesRoot: dir);
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", "digest");

        bool ok = await service.DownloadAndStageAsync(info, new Progress<double>()).ConfigureAwait(false);

        Assert.IsFalse(ok, "a failed stage must not report success");
        Assert.AreEqual(0, spawns, "a failed stage must never launch");
    }

    [TestMethod]
    public void LaunchUpdater_LeavesTheSwapLockAlone()
    {
        // Startup clears a leftover swap lock; the launch path must not clear a
        // live one, or a second updater could swap beside the running one.
        string dir = NewDir();
        string stageDir = Path.Combine(dir, "staged", "0.5.0");
        Directory.CreateDirectory(stageDir);
        string stagedCmd = Path.Combine(stageDir, "apply-update.cmd");
        File.WriteAllText(stagedCmd, "echo 0.5.0\r\n{{RELAUNCH}}\r\n");
        File.WriteAllText(UpdateService.StagedCmdHashPath(stagedCmd), UpdateService.ComputeSha256(stagedCmd));
        string installDir = Path.Combine(Path.GetTempPath(), $"wmd-install-{Guid.NewGuid():N}");
        string lockDir = Path.Combine(installDir, ".update-running");
        Directory.CreateDirectory(lockDir);
        try
        {
            var service = new UpdateService(
                updatesRoot: dir,
                startProcess: _ => null);
            var info = new UpdateInfo("0.5.0", "https://x/app.zip", "digest");

            Assert.IsTrue(service.LaunchUpdater(info, installDir));
            Assert.IsTrue(Directory.Exists(lockDir), "the launch path must never remove a live swap lock");
        }
        finally
        {
            try { Directory.Delete(installDir, true); } catch (IOException) { /* best-effort */ }
        }
    }

    [TestMethod]
    public void ApplyUpdateCmd_ValidatesStageBeforeRenamingAndRestoresOnFailure()
    {
        // A lost race must not leave the install directory without an exe:
        // validate the staged payload before the rename-aside, and restore the
        // .old on a failed copy.
        using var stream = typeof(UpdateService).Assembly
            .GetManifestResourceStream("ModernWigiDash.App.Update.apply-update.cmd");
        Assert.IsNotNull(stream, "the updater cmd resource must be embedded");
        using var reader = new StreamReader(stream);
        string script = reader.ReadToEnd();

        int validate = script.IndexOf("if not exist \"%STAGE%\\ModernWigiDash-win-x64\\%EXE%\"", StringComparison.Ordinal);
        int moveAside = script.IndexOf("move /Y \"%INSTALL%\\%EXE%\" \"%INSTALL%\\%EXE%.old\"", StringComparison.Ordinal);
        int copyFail = script.IndexOf(":copyfail", StringComparison.Ordinal);
        int restore = script.IndexOf(
            "if exist \"%INSTALL%\\%EXE%.old\" move /Y \"%INSTALL%\\%EXE%.old\" \"%INSTALL%\\%EXE%\"",
            StringComparison.Ordinal);

        Assert.IsTrue(validate >= 0, "the staged-exe validation must exist");
        Assert.IsTrue(moveAside >= 0, "the rename-aside must exist");
        Assert.IsTrue(validate < moveAside, "the stage must be validated before the live exe is moved");
        Assert.IsTrue(restore > copyFail, "a failed copy must restore the previous exe");
        Assert.IsTrue(restore < script.IndexOf("exit /b 4", copyFail),
            "the restore must run before the copy-failure exit, not after it");
    }

    [TestMethod]
    public void LaunchUpdater_MissingHashStamp_ReturnsFalse()
    {
        // A staged cmd with no integrity stamp at all (the hash file is absent)
        // is refused: the stamp is what proves the cmd was written by staging.
        string dir = NewDir();
        string stageDir = Path.Combine(dir, "staged", "0.5.0");
        Directory.CreateDirectory(stageDir);
        string stagedCmd = Path.Combine(stageDir, "apply-update.cmd");
        File.WriteAllText(stagedCmd, "echo 0.5.0\r\n{{RELAUNCH}}\r\n");
        int spawns = 0;
        var service = new UpdateService(
            updatesRoot: dir,
            startProcess: _ => { spawns++; return null; });
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", "digest");

        bool ok = service.LaunchUpdater(info, Path.Combine(Path.GetTempPath(), "wmd-install"));

        Assert.IsFalse(ok, "a missing integrity stamp must refuse the launch");
        Assert.AreEqual(0, spawns);
    }

    [TestMethod]
    public void LaunchUpdater_MissingRelaunchMarker_LogsButStillLaunches()
    {
        // A staged cmd that passes the integrity check but lacks the {{RELAUNCH}}
        // marker still launches (the swap runs); the app just won't relaunch
        // itself. This is a logged warning, not a refusal.
        string dir = NewDir();
        string stageDir = Path.Combine(dir, "staged", "0.5.0");
        Directory.CreateDirectory(stageDir);
        string stagedCmd = Path.Combine(stageDir, "apply-update.cmd");
        File.WriteAllText(stagedCmd, "echo 0.5.0\r\n"); // no {{RELAUNCH}} marker
        File.WriteAllText(
            UpdateService.StagedCmdHashPath(stagedCmd),
            UpdateService.ComputeSha256(stagedCmd));
        int spawns = 0;
        var service = new UpdateService(
            updatesRoot: dir,
            startProcess: _ => { spawns++; return null; });
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", "digest");

        bool ok = service.LaunchUpdater(info, Path.Combine(Path.GetTempPath(), "wmd-install"));

        Assert.IsTrue(ok, "a marker-missing cmd still launches (the swap runs)");
        Assert.AreEqual(1, spawns);
    }

    [TestMethod]
    public void Getters_ExposeTheLogAndUpdatesRoot()
    {
        string dir = NewDir();
        var service = new UpdateService(updatesRoot: dir);

        Assert.IsNotNull(service.Log, "the update-category log is exposed so callers log through one owner");
        Assert.AreEqual(dir, service.UpdatesRoot);
    }

    [TestMethod]
    public void RunStartupPhase_HealsInterruptedSwapAndCleansStale()
    {
        string dir = NewDir();
        string installDir = Path.Combine(dir, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "ModernWigiDash.App.exe.old"), "old-exe");
        string staged = Path.Combine(dir, "staged", "0.5.0");
        Directory.CreateDirectory(staged);
        var service = new UpdateService(updatesRoot: dir);

        service.RunStartupPhase(installDir);

        Assert.IsTrue(File.Exists(Path.Combine(installDir, "ModernWigiDash.App.exe")),
            "an interrupted swap must be restored at startup");
        Assert.IsFalse(File.Exists(Path.Combine(installDir, "ModernWigiDash.App.exe.old")));
        Assert.IsFalse(Directory.Exists(Path.Combine(dir, "staged")), "stale stages must be cleaned at startup");
    }

    [TestMethod]
    public async Task DownloadAndStage_FailedExtractCleansStageAndRetryOverwritesCleanly()
    {
        string dir = NewDir();
        // First zip contains an unexpected file (no exe), leaving partial files if stage weren't cleaned/overwritten.
        string badZipPath = Path.Combine(dir, "bad.zip");
        Directory.CreateDirectory(dir);
#pragma warning disable S6966
        using (var badZip = await ZipFile.OpenAsync(badZipPath, ZipArchiveMode.Create))
        {
            var entry = badZip.CreateEntry("ModernWigiDash-win-x64/partial.txt");
            using var w = new StreamWriter(await entry.OpenAsync());
            await w.WriteAsync("stale");
        }

        string goodZipPath = Path.Combine(dir, "good.zip");
        using (var goodZip = await ZipFile.OpenAsync(goodZipPath, ZipArchiveMode.Create))
        {
            var entry1 = goodZip.CreateEntry("ModernWigiDash-win-x64/partial.txt");
            using (var w = new StreamWriter(await entry1.OpenAsync()))
                await w.WriteAsync("fresh");
            var entry2 = goodZip.CreateEntry("ModernWigiDash-win-x64/ModernWigiDash.App.exe");
            using (var w = new StreamWriter(await entry2.OpenAsync()))
                await w.WriteAsync("exe");
        }
#pragma warning restore S6966

        byte[] badBytes = await File.ReadAllBytesAsync(badZipPath);
        byte[] goodBytes = await File.ReadAllBytesAsync(goodZipPath);
        int attempt = 0;
        var service = new UpdateService(
            downloadFile: async (_, dest, _, _) =>
            {
                attempt++;
                await File.WriteAllBytesAsync(dest, attempt == 1 ? badBytes : goodBytes).ConfigureAwait(false);
            },
            sha256Matches: (_, _) => true,
            updatesRoot: dir);
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", "unused");

        bool firstOk = await service.DownloadAndStageAsync(info, new Progress<double>()).ConfigureAwait(false);
        string stagedVersionDir = Path.Combine(dir, "staged", "0.5.0");
        Assert.IsFalse(firstOk);
        Assert.IsFalse(Directory.Exists(stagedVersionDir), "failed staging must clean up the version stage directory");

        // Even if a stray file remains on disk before retry, overwrite extraction succeeds.
        Directory.CreateDirectory(Path.Combine(stagedVersionDir, "ModernWigiDash-win-x64"));
        await File.WriteAllTextAsync(Path.Combine(stagedVersionDir, "ModernWigiDash-win-x64", "partial.txt"), "stale").ConfigureAwait(false);

        bool secondOk = await service.DownloadAndStageAsync(info, new Progress<double>()).ConfigureAwait(false);
        Assert.IsTrue(secondOk, "retry after failed staging must overwrite cleanly without IOException");
        Assert.AreEqual("fresh", await File.ReadAllTextAsync(Path.Combine(stagedVersionDir, "ModernWigiDash-win-x64", "partial.txt")).ConfigureAwait(false));
    }

    [TestMethod]
    public void LaunchUpdater_TrimsTrailingDirectorySeparatorFromInstallDir()
    {
        string dir = NewDir();
        string stageDir = Path.Combine(dir, "staged", "0.5.0");
        Directory.CreateDirectory(stageDir);
        string stagedCmd = Path.Combine(stageDir, "apply-update.cmd");
        File.WriteAllText(stagedCmd, "echo 0.5.0\r\n{{RELAUNCH}}\r\n");
        File.WriteAllText(
            UpdateService.StagedCmdHashPath(stagedCmd),
            UpdateService.ComputeSha256(stagedCmd));

        ProcessStartInfo? started = null;
        var service = new UpdateService(
            updatesRoot: dir,
            startProcess: psi => { started = psi; return null; });
        var info = new UpdateInfo("0.5.0", "https://x/app.zip", "digest");
        string installDirWithSlash = Path.Combine(Path.GetTempPath(), "wmd-install") + Path.DirectorySeparatorChar;

        Assert.IsTrue(service.LaunchUpdater(info, installDirWithSlash));
        Assert.IsNotNull(started);
        Assert.IsFalse(started.Arguments.Contains("\\\""),
            $"installDir trailing backslash must be trimmed so quoted arg never ends with \\\": {started.Arguments}");
    }
}
