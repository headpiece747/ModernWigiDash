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

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
