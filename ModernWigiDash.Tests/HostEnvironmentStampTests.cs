using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModernWigiDash.Tests;

/// <summary>
/// The startup log's host stamp: the format, and the fact that startup
/// actually writes it.
///
/// The second half is the one that matters. A format test alone would still
/// pass if someone deleted the <c>FileLog.Write</c> from <c>OnStartup</c>,
/// which is the exact regression the stamp exists to prevent - so the call
/// site is pinned too. The OS build used to appear nowhere in production code,
/// so a report of "the latest Windows 11 update broke it" could not be
/// narrowed from anything the app wrote (2026-09-27).
/// </summary>
[TestClass]
public class HostEnvironmentStampTests
{
    [TestMethod]
    public void Format_SpellseveryFieldAsKeyEqualsValue()
    {
        Assert.AreEqual(
            "os=Microsoft Windows 11.0.26200 arch=X64 runtime=.NET 10.0.10 app=0.8.0",
            HostEnvironmentStamp.Format(
                "Microsoft Windows 11.0.26200", "X64", ".NET 10.0.10", "0.8.0"),
            "the stamp is four key=value pairs, space separated, so one field can be found in the line without parsing the rest");
    }

    [TestMethod]
    public void Format_AnAbsentValueReadsUnknown_NeverBlank()
    {
        // A blank would leave "arch= runtime=" in the line, which reads as a
        // parse that succeeded and lost a field rather than one that never had
        // one - the difference between a diagnosable log and a misleading one.
        string stamp = HostEnvironmentStamp.Format("Microsoft Windows 11.0.26200", "  ", ".NET 10.0.10", "dev");
        StringAssert.Contains(stamp, "arch=unknown", "a missing architecture must be spelled, not left blank");
        StringAssert.Contains(stamp, "app=dev", "a dev build's app version is the literal dev, so the field is never empty");
    }

    [TestMethod]
    public void Describe_CarriesThisMachinesOwnOsBuild()
    {
        // Not a snapshot of expected values - the numbers differ per machine by
        // design. What is pinned is that the LIVE read reaches the format and
        // that every field resolves: a Describe that returned a constant, or
        // read a property that does not exist, would leave a field blank or
        // "unknown" here.
        string stamp = HostEnvironmentStamp.Describe();
        foreach (string key in new[] { "os=", "arch=", "runtime=", "app=" })
        {
            StringAssert.Contains(stamp, key, "the live stamp must carry " + key);
        }
        Assert.IsFalse(stamp.Contains("=unknown", StringComparison.Ordinal),
            "every field resolves on a real machine, so 'unknown' here means the live read is broken");
        // Values legitimately contain spaces ("Microsoft Windows 11.0.26200",
        // ".NET 10.0.10"), so the separators are \s+ and each value is .+? -
        // an empty value leaves nothing for .+? to match and the whole pattern
        // fails, which is the point.
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(
                stamp, @"^os=.+?\s+arch=.+?\s+runtime=.+?\s+app=.+$"),
            "every field must carry a non-empty value, in the declared order: " + stamp);
    }
}
