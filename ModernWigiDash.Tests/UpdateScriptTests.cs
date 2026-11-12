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
        try { Directory.Delete(TempRoot, true); } catch { /* best-effort */ }
    }

    /// <summary>Runs the real apply-update.cmd against a temp install dir with a
    /// fake exe, verifying the rename-aside swap, preservation of user files,
    /// relaunch, and the swap-lock release. The fake exe uses a unique name so a
    /// running real app (image-name match in the cmd's tasklist wait) can't
    /// interfere.</summary>
    [TestMethod]
    public void ApplyUpdateCmd_SwapsExePreservesUserFilesAndReleasesTheLock()
    {
        const string fakeExe = "WmdTestApp.exe";
        (string root, string install, string stage) = Arrange(fakeExe);
        string cmdPath = WriteCmd(root);

        (int exit, string output, string logPath) = Run(cmdPath, install, stage, fakeExe);

        Assert.AreEqual(0, exit, $"updater failed ({exit}): {output}");
        Assert.AreEqual("new-exe", File.ReadAllText(Path.Combine(install, fakeExe)));
        Assert.IsFalse(File.Exists(Path.Combine(install, fakeExe + ".old")), "the .old must be cleaned after a successful swap");
        Assert.AreEqual("user-theme", File.ReadAllText(Path.Combine(install, "user-note.txt")), "user files must survive");
        Assert.IsTrue(File.Exists(Path.Combine(install, "Resources", "font.ttf")), "staged Resources must be copied");
        Assert.IsFalse(File.Exists(Path.Combine(stage, "ModernWigiDash-win-x64", fakeExe)),
            "the stage must be deleted after applying");
        Assert.IsFalse(Directory.Exists(Path.Combine(install, ".update-running")),
            "the swap lock must be released after a successful swap");
        Assert.IsTrue(File.Exists(logPath), "the updater must log under WMD_UPDATE_LOG, never the real update log");
        StringAssert.Contains(File.ReadAllText(logPath), "swap complete");
    }

    [TestMethod]
    public void ApplyUpdateCmd_SecondUpdaterExitsWithoutTouchingTheInstall()
    {
        const string fakeExe = "WmdTestApp.exe";
        (string root, string install, string stage) = Arrange(fakeExe);
        // A live swap lock: the second updater must exit before any swap, keep
        // the winner's lock, and leave the install and stage untouched.
        Directory.CreateDirectory(Path.Combine(install, ".update-running"));
        string cmdPath = WriteCmd(root);

        (int exit, string output, string logPath) = Run(cmdPath, install, stage, fakeExe);

        Assert.AreEqual(0, exit, $"a lost lock race is a clean exit, not a failure: {output}");
        Assert.AreEqual("old-exe", File.ReadAllText(Path.Combine(install, fakeExe)),
            "the loser must not touch the installed exe");
        Assert.IsTrue(Directory.Exists(Path.Combine(install, ".update-running")),
            "the loser must not release the winner's lock");
        Assert.IsTrue(File.Exists(Path.Combine(stage, "ModernWigiDash-win-x64", fakeExe)),
            "the loser must not consume the stage");
        StringAssert.Contains(File.ReadAllText(logPath), "another updater is already running");
    }

    private static (string Root, string Install, string Stage) Arrange(string fakeExe)
    {
        string root = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "install");
        string stage = Path.Combine(root, "staged");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(Path.Combine(stage, "ModernWigiDash-win-x64"));

        // Old install: exe + a user file that must survive. (The theme file no
        // longer lives in the install dir - ADR-0021 moved it to the user
        // state dir - so a neutral name keeps the pin honest.)
        File.WriteAllText(Path.Combine(install, fakeExe), "old-exe");
        File.WriteAllText(Path.Combine(install, "user-note.txt"), "user-theme");
        // Staged new exe + Resources.
        File.WriteAllText(Path.Combine(stage, "ModernWigiDash-win-x64", fakeExe), "new-exe");
        Directory.CreateDirectory(Path.Combine(stage, "ModernWigiDash-win-x64", "Resources"));
        File.WriteAllText(Path.Combine(stage, "ModernWigiDash-win-x64", "Resources", "font.ttf"), "font");
        return (root, install, stage);
    }

    private static string WriteCmd(string root)
    {
        // The real cmd, extracted from the embedded resource.
        string cmdPath = Path.Combine(root, "apply-update.cmd");
        var asm = typeof(UpdateService).Assembly;
        using var stream = asm.GetManifestResourceStream("ModernWigiDash.App.Update.apply-update.cmd")!;
        using var reader = new StreamReader(stream);
        File.WriteAllText(cmdPath, reader.ReadToEnd()
            .Replace("{{VERSION}}", "0.5.0")
            .Replace("{{RELAUNCH}}", ""));
        return cmdPath;
    }

    private static (int ExitCode, string Output, string LogPath) Run(string cmdPath, string install, string stage, string fakeExe)
    {
        // /S /c: with the whole command quoted, cmd preserves the inner quotes
        // and passes the args through verbatim. The plain "/c \"path\" \"arg\""
        // form triggers cmd's quote-stripping (the script path gets mangled into
        // "The filename, directory name, or volume label syntax is incorrect").
        string logPath = Path.Combine(Path.GetDirectoryName(install)!, "update.log");
        var psi = new ProcessStartInfo("cmd.exe", $"/S /C \"\"{cmdPath}\" \"{install}\" \"{stage}\" {fakeExe}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["WMD_UPDATE_LOG"] = logPath;
        using var proc = Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        Assert.IsTrue(proc.WaitForExit(30_000), $"updater timed out; output: {output}");
        return (proc.ExitCode, output, logPath);
    }
}
