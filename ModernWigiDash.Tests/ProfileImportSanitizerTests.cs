
namespace ModernWigiDash.Tests;

/// <summary>
/// The untrusted-import sanitizer's pure rule pins: the InstanceId safety
/// rule (the widgets' cache-file key) and the import file-size guard. The
/// import BEHAVIOR tests (through ProfileOps.ImportJson: caps, null
/// collections, ActionCommand clear, path repair, channel sanitization) stay
/// in ProfileOpsTests — they exercise the entry point, not the rules.
/// </summary>
[TestClass]
public class ProfileImportSanitizerTests
{
    // ── InstanceId safety (the weather cache-file key) ─────

    [TestMethod]
    public void IsSafeInstanceId_RejectsEveryUnsafeShape()
    {
        Assert.IsFalse(ProfileImportSanitizer.IsSafeInstanceId(null), "null is not a safe token");
        Assert.IsFalse(ProfileImportSanitizer.IsSafeInstanceId(""), "empty is not a safe token");
        Assert.IsFalse(ProfileImportSanitizer.IsSafeInstanceId(new string('a', 65)), "65 chars exceed the 64-char cap");
        Assert.IsFalse(ProfileImportSanitizer.IsSafeInstanceId("a/b"), "path separators are rejected");
        Assert.IsFalse(ProfileImportSanitizer.IsSafeInstanceId("a\\b"), "backslash separators are rejected");
        Assert.IsFalse(ProfileImportSanitizer.IsSafeInstanceId(".."), "dot segments are rejected");
        Assert.IsFalse(ProfileImportSanitizer.IsSafeInstanceId("id.with.dots"), "dots are rejected");
        Assert.IsFalse(ProfileImportSanitizer.IsSafeInstanceId("C:evil"), "colon is rejected");
        Assert.IsFalse(ProfileImportSanitizer.IsSafeInstanceId("über"), "non-ASCII is rejected");
    }

    [TestMethod]
    public void IsSafeInstanceId_AcceptsGuidAndTokenShapes()
    {
        Assert.IsTrue(ProfileImportSanitizer.IsSafeInstanceId("ab-12_CD"), "letters/digits/-/_ are safe");
        Assert.IsTrue(ProfileImportSanitizer.IsSafeInstanceId(Guid.NewGuid().ToString()), "a GUID is safe (the regenerated form)");
        Assert.IsTrue(ProfileImportSanitizer.IsSafeInstanceId("x"), "a single char is safe");
    }

    // ── import size guard ─────────────────────────────────────

    [TestMethod]
    public void IsImportFileTooLarge_ExactCap_IsAllowed()
    {
        Assert.IsFalse(ProfileImportSanitizer.IsImportFileTooLarge(ProfileImportSanitizer.MaxImportFileBytes),
            "a file of exactly the cap bytes is the largest allowed import");
    }

    [TestMethod]
    public void IsImportFileTooLarge_OneByteOverCap_IsRejected()
    {
        Assert.IsTrue(ProfileImportSanitizer.IsImportFileTooLarge(ProfileImportSanitizer.MaxImportFileBytes + 1),
            "anything past the cap is untrusted junk and must be rejected before parsing");
    }

    [TestMethod]
    public void IsImportFileTooLarge_EmptyFile_IsAllowed()
    {
        Assert.IsFalse(ProfileImportSanitizer.IsImportFileTooLarge(0));
        Assert.IsFalse(ProfileImportSanitizer.IsImportFileTooLarge(-1));
    }
}
