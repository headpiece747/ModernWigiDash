using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModernWigiDash.Tests;

/// <summary>
/// The startup log's host stamp: the format, the Windows-release spelling, and
/// the fact that startup actually writes it.
///
/// The call-site half is the one that matters most. A format test alone would
/// still pass if someone deleted the <c>FileLog.Write</c> from
/// <c>OnStartup</c>, which is the exact regression the stamp exists to prevent,
/// so the call is pinned too (in
/// <c>DebtGuardTests.HouseRules_TheStartupLog_CarriesTheHostEnvironmentStamp</c>).
/// The Windows version used to appear nowhere in production code, so a report
/// of "the latest Windows 11 update broke it" could not be narrowed from
/// anything the app wrote (2026-09-27).
/// </summary>
[TestClass]
public class HostEnvironmentStampTests
{
    [TestMethod]
    public void Format_SpellseveryFieldAsKeyEqualsValue()
    {
        Assert.AreEqual(
            "win=25H2 (26200.9550) arch=X64 runtime=.NET 10.0.12 app=0.8.0",
            HostEnvironmentStamp.Format("25H2 (26200.9550)", "X64", ".NET 10.0.12", "0.8.0"),
            "the stamp is four key=value pairs, space separated, so one field can be found in the line without parsing the rest");
    }

    [TestMethod]
    public void Format_AnAbsentValueReadsUnknown_NeverBlank()
    {
        // A blank would leave "arch= runtime=" in the line, which reads as a
        // parse that succeeded and lost a field rather than one that never had
        // one - the difference between a diagnosable log and a misleading one.
        string stamp = HostEnvironmentStamp.Format("25H2 (26200.9550)", "  ", ".NET 10.0.12", "dev");
        StringAssert.Contains(stamp, "arch=unknown", "a missing architecture must be spelled, not left blank");
        StringAssert.Contains(stamp, "app=dev", "a dev build's app version is the literal dev, so the field is never empty");
    }

    [TestMethod]
    public void ComposeWindowsRelease_NamesTheReleaseAndTheBuildRevision()
    {
        // The UBR is the whole point: two machines on build 26200 are on
        // different cumulative updates, and only the revision tells them apart.
        Assert.AreEqual("25H2 (26200.9550)",
            HostEnvironmentStamp.ComposeWindowsRelease("25H2", "26200", "9550"),
            "the release name alone does not identify a cumulative update - the build revision must ride with it");

        Assert.AreEqual("26200.9550",
            HostEnvironmentStamp.ComposeWindowsRelease(null, "26200", "9550"),
            "with no release name the build revision is still worth reporting");
    }

    [TestMethod]
    public void ComposeWindowsRelease_WithoutTheRevision_IsUnknown_NotAMisleadingBuild()
    {
        // Reporting a bare "26200" here would claim to name a cumulative update
        // while naming none - the log would look diagnostic and answer nothing.
        Assert.AreEqual("unknown",
            HostEnvironmentStamp.ComposeWindowsRelease("25H2", "26200", null),
            "a build with no UBR cannot identify a cumulative update, so it must not be reported as if it could");
        Assert.AreEqual("unknown",
            HostEnvironmentStamp.ComposeWindowsRelease("25H2", null, "9550"),
            "a revision with no build is equally unidentifiable");
        Assert.AreEqual("unknown",
            HostEnvironmentStamp.ComposeWindowsRelease("25H2", "  ", "  "),
            "whitespace is not a value");
    }

    [TestMethod]
    public void Describe_CarriesThisMachinesOwnWindowsBuild()
    {
        // Not a snapshot of expected values - the numbers differ per machine by
        // design. What is pinned is that the LIVE reads reach the format, that
        // every field resolves, and - the part that would otherwise rot
        // unnoticed - that the Windows field names a build revision rather than
        // the misleading "Microsoft Windows 10.0.x" kernel string that .NET's
        // own APIs report on Windows 11.
        string stamp = HostEnvironmentStamp.Describe();
        foreach (string key in new[] { "win=", "arch=", "runtime=", "app=" })
        {
            StringAssert.Contains(stamp, key, "the live stamp must carry " + key);
        }
        Assert.IsFalse(stamp.Contains("=unknown", StringComparison.Ordinal),
            "every field resolves on a real machine, so 'unknown' here means a live read is broken: " + stamp);
        Assert.IsFalse(stamp.Contains("Microsoft Windows 10.", StringComparison.OrdinalIgnoreCase),
            "the stamp must not carry .NET's kernel-version string, which reads as Windows 10 on Windows 11: " + stamp);
        // Values legitimately contain spaces ("25H2 (26200.9550)", ".NET
        // 10.0.12"), so the separators are \s+ and each value is .+? - an empty
        // value leaves nothing for .+? to match and the whole pattern fails.
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(
                stamp, @"^win=.+?\s+arch=.+?\s+runtime=.+?\s+app=.+$"),
            "every field must carry a non-empty value, in the declared order: " + stamp);
    }
}
