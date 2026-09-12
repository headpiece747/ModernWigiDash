using System.IO;

namespace ModernWigiDash.Tests;

[TestClass]
public class FileLogTests
{
    private string _tempDir = "";
    private string _logPath = "";
    private string _rotatedPath = "";

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wmd-filelog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _logPath = Path.Combine(_tempDir, "display_device.log");
        _rotatedPath = _logPath + ".1";
        FileLog.LogPath = _logPath;
    }

    [TestCleanup]
    public void Cleanup()
    {
        FileLog.LogPath = Path.Combine(AppContext.BaseDirectory, "display_device.log");
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [TestMethod]
    public void Flush_FlushesBelowThreshold_LandsInFile()
    {
        // A short line stays under both flush cadences (8 KB or 250 ms), so
        // it would sit in the buffer at process exit — the app's exit handler
        // calls Flush exactly for this (the on-device close that lost the
        // standby line: the line was written, the buffer never flushed).
        FileLog.Write("the line the exit flush must land");
        FileLog.Flush();

        string content = ReadLog(_logPath);
        Assert.IsTrue(content.Contains("the line the exit flush must land"), "Flush must land buffered lines immediately");
    }

    [TestMethod]
    public void Flush_WhenNoWriterOpened_IsNoOp()
    {
        // Must never throw, even with nothing buffered or written — and a
        // flush must not corrupt the writer state: the next write still lands.
        FileLog.Flush();
        FileLog.Flush();

        FileLog.Write("write after a writer-less flush");
        FileLog.Flush();
        Assert.IsTrue(ReadLog(_logPath).Contains("write after a writer-less flush"),
            "a no-op flush must leave the writer path intact");
    }

    [TestMethod]
    public void Write_MultiLineTokenValue_FlattenAndRedactLandInTheFile()
    {
        // FileLog.Write is the line-policy owner: the sanitize pass (flatten
        // + bound + redact) runs at the seam, so a multi-line, token-bearing
        // value lands as one flattened, redacted line. If the Sanitize call
        // were ever removed from the seam, this pin fails.
        FileLog.Write("line one\nline two token=secret123");
        FileLog.Flush();

        string content = ReadLog(_logPath);
        Assert.IsTrue(content.Contains("line one line two token=<redacted>"),
            "the flattened, redacted line must land intact");
        Assert.IsFalse(content.Contains("secret123"), "the token value must never reach the file");
        Assert.AreEqual(1, content.TrimEnd('\r', '\n').Split('\n').Length,
            "embedded newlines must flatten to spaces - one value, one line");
    }

    [TestMethod]
    public void Write_FileOverRotationCap_RotatesToDotOneAndContinuesFresh()
    {
        // Seed an oversized file directly; the size check runs on a write
        // cadence (every 100 writes), not per write.
        File.WriteAllBytes(_logPath, new byte[FileLog.RotationCapBytes + 1]);

        // The rotation check fires within the first 100 writes: the seeded
        // file is moved to .1 (buffered lines flushed first) and logging
        // continues into a fresh file.
        for (int i = 0; i < 100; i++)
            FileLog.Write($"pre-rotation line {i}");

        // The fresh file's writes are buffered until the 8 KB flush cadence
        // fires; write enough padding to cross it, forcing a flush, then
        // verify the post-rotation lines landed in the fresh file.
        FileLog.Write("post-rotation line 100");
        for (int i = 0; i < 1000; i++)
            FileLog.Write("padding line to cross the flush threshold");

        Assert.IsTrue(File.Exists(_rotatedPath), "The oversized log must rotate to display_device.log.1");
        Assert.IsTrue(File.Exists(_logPath), "A fresh log file must exist after rotation");
        Assert.IsTrue(new FileInfo(_rotatedPath).Length >= FileLog.RotationCapBytes, "The .1 file must carry the oversized content");
        Assert.IsTrue(new FileInfo(_logPath).Length < FileLog.RotationCapBytes, "The active log must start fresh after rotation");

        // FileLog keeps its write stream open, so reads must share read+write
        // access (File.ReadAllText's FileShare.Read would be rejected).
        string fresh = ReadLog(_logPath);
        Assert.IsTrue(fresh.Contains("post-rotation line 100"), "Writes after rotation must continue into the fresh file");

        string rotated = ReadLog(_rotatedPath);
        Assert.IsFalse(rotated.Contains("post-rotation line 100"), "Post-rotation lines must not land in the .1 backup");
    }

    [TestMethod]
    public void RotateIfOverCap_TheSharedRotationStep_BeholdsItsContract()
    {
        // The primitive is the one owner of the rotation step (cap check +
        // delete-backup + move) — both display_device.log and crash.log
        // rotate through it. Its contract is pinned directly, without the
        // FileLog writer lifecycle.
        string path = Path.Combine(_tempDir, "crash.log");

        Assert.IsFalse(FileLog.RotateIfOverCap(path), "an absent file never rotates");

        File.WriteAllBytes(path, new byte[1024]);
        Assert.IsFalse(FileLog.RotateIfOverCap(path), "an under-cap file never rotates");
        Assert.IsTrue(File.Exists(path));

        File.WriteAllBytes(path + ".1", new byte[10]);
        File.WriteAllBytes(path, new byte[FileLog.RotationCapBytes + 1]);
        Assert.IsTrue(FileLog.RotateIfOverCap(path), "an over-cap file rotates");
        Assert.IsFalse(File.Exists(path), "the active file is gone after the move");
        Assert.AreEqual(FileLog.RotationCapBytes + 1, new FileInfo(path + ".1").Length,
            "an existing backup is replaced, not appended to");
    }

    [TestMethod]
    public void Rotate_WhenBackupTargetIsADirectory_SwallowsTheMoveFailure()
    {
        // A rotation whose .1 target is a directory makes File.Move throw
        // (you cannot move a file onto a directory): TryRotateIfNeeded's catch
        // reports the rotation failure once and keeps appending past the cap
        // (best-effort - a locked/odd backup must not wedge logging). Seed an
        // over-cap file, block the .1 target with a directory, then drive the
        // rotation cadence; the write must not throw and must keep landing.
        File.WriteAllBytes(_logPath, new byte[FileLog.RotationCapBytes + 1]);
        Directory.CreateDirectory(_rotatedPath); // blocks the move target

        for (int i = 0; i < 100; i++)
            FileLog.Write($"pre-failed-rotation line {i}"); // must not throw

        FileLog.Flush();
        Assert.IsTrue(File.Exists(_logPath), "the active log must still exist after a failed rotation");
        string content = ReadLog(_logPath);
        Assert.IsTrue(content.Contains("pre-failed-rotation line 99"),
            "logging must continue into the same file when the rotation move fails");
    }

    [TestMethod]
    public void Write_WhenLogPathIsADirectory_SwallowsTheFailureAndRecovers()
    {
        // Pointing LogPath at a directory makes the writer's FileStream throw
        // (a directory is not a writable file): the Write catch block reports
        // the failure once and resets the writer so logging recovers. The
        // best-effort contract is "never throw" - this pins the failure leg
        // (the catch block runs without throwing) and the recovery (a later
        // write to a real path lands), neither of which the happy-path tests
        // exercise.
        FileLog.LogPath = _tempDir; // a directory, not a file
        FileLog.Write("this write must fail but never throw"); // must not throw

        // Recovery: pointing back at a real file must resume logging.
        FileLog.LogPath = _logPath;
        FileLog.Write("recovered write after a failed one");
        FileLog.Flush();
        Assert.IsTrue(ReadLog(_logPath).Contains("recovered write after a failed one"),
            "logging must recover after a failed write resets the writer");
    }

    [TestMethod]
    public void Flush_WhenWriterFails_SwallowsTheFailureAndRecovers()
    {
        // A write + flush against a directory-backed path must swallow the
        // IOException (the locked-file leg) and reset the writer, never
        // throwing. The recovery assertion (a later write to a real path
        // lands) is what proves the writer was reset rather than left wedged.
        FileLog.LogPath = _tempDir; // a directory, not a file
        FileLog.Write("write into the bad path"); // must not throw
        FileLog.Flush(); // must swallow the failure

        FileLog.LogPath = _logPath;
        FileLog.Write("recovered after a failed flush");
        FileLog.Flush();
        Assert.IsTrue(ReadLog(_logPath).Contains("recovered after a failed flush"),
            "logging must recover after a failed flush resets the writer");
    }

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
