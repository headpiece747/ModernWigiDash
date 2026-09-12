using System.IO;

namespace ModernWigiDash.Tests;

/// <summary>
/// The crash-log writer pinned against a temp path: the happy-path append
/// (a sanitized exception line lands in the file), the null-exception leg,
/// the handled/unhandled kind label, and the best-effort fault absorption
/// (a locked file keeps failing silently, never throwing into the handler).
/// </summary>
[TestClass]
public class CrashLogTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "wmd-crashlog-" + Guid.NewGuid().ToString("N") + ".log");

    [TestMethod]
    public void Append_UnhandledException_WritesTheSanitizedLine()
    {
        string path = TempPath();
        CrashLog.LogPath = path;

        CrashLog.Append(new InvalidOperationException("boom"), handled: false);

        Assert.IsTrue(File.Exists(path));
        string content = File.ReadAllText(path);
        StringAssert.Contains(content, "UNHANDLED EXCEPTION", "the unhandled kind label is spelled");
        StringAssert.Contains(content, "InvalidOperationException", "the exception type name rides the line");
        StringAssert.Contains(content, "boom", "the message lands");
        File.Delete(path);
    }

    [TestMethod]
    public void Append_HandledException_WritesTheHandledLabel()
    {
        string path = TempPath();
        CrashLog.LogPath = path;

        CrashLog.Append(new TimeoutException("slow"), handled: true);

        string content = File.ReadAllText(path);
        StringAssert.Contains(content, "HANDLED EXCEPTION", "the handled kind label is spelled");
        File.Delete(path);
    }

    [TestMethod]
    public void Append_NullException_WritesANullTypeLine()
    {
        string path = TempPath();
        CrashLog.LogPath = path;

        CrashLog.Append(null);

        string content = File.ReadAllText(path);
        StringAssert.Contains(content, "null", "a null exception writes the null type name");
        File.Delete(path);
    }

    [TestMethod]
    public void Append_LockedFile_AbsorbsTheFaultSilently()
    {
        // A directory at the log path makes File.AppendAllText throw an
        // IOException (or UnauthorizedAccessException); the writer must absorb
        // it (best-effort: the crash log is surfaced to debug output, never a
        // throw into the unhandled-exception handler).
        string dir = Path.Combine(Path.GetTempPath(), "wmd-crashlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        CrashLog.LogPath = dir;

        // Must not throw; the best-effort writer absorbs the fault silently.
        bool threw = false;
        try
        {
            CrashLog.Append(new InvalidOperationException("locked"), handled: false);
        }
        catch (Exception)
        {
            threw = true;
        }

        Assert.IsFalse(threw, "a locked crash-log file must not throw into the unhandled-exception handler");
        Directory.Delete(dir);
    }
}
