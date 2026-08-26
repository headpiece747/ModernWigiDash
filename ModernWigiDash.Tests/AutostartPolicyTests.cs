using System.Reflection;
using ModernWigiDash.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Start-with-Windows policy pinned without the registry (ADR-0019): the
/// Run entry's location constants and the command line's shape - the quoted
/// exe path plus the autostart flag, built from the launch policy's constant
/// so the two halves of the round trip cannot drift.
/// </summary>
[TestClass]
public class AutostartPolicyTests
{
    [TestMethod]
    public void RunEntryLocation_IsTheHkcuRunSubkey_WithTheAppValueName()
    {
        // The const values are read through reflection (the
        // ArchitectureTests ConstValue shape): a direct literal comparison
        // folds into the constant the test would police.
        Assert.AreEqual(@"Software\Microsoft\Windows\CurrentVersion\Run", ConstValue(nameof(AutostartPolicy.RunSubKeyPath)));
        Assert.AreEqual("ModernWigiDash", ConstValue(nameof(AutostartPolicy.RunValueName)));
    }

    [TestMethod]
    public void BuildCommandLine_QuotesTheExePath_AndAppendsTheAutostartFlag()
        => Assert.AreEqual(
            "\"C:\\Program Files\\ModernWigiDash\\ModernWigiDash.App.exe\" --startup",
            AutostartPolicy.BuildCommandLine(@"C:\Program Files\ModernWigiDash\ModernWigiDash.App.exe"));

    [TestMethod]
    public void BuildCommandLine_QuotesEvenAPathWithoutSpaces()
        => Assert.AreEqual("\"C:\\wmd.exe\" --startup", AutostartPolicy.BuildCommandLine(@"C:\wmd.exe"));

    [TestMethod]
    public void BuildCommandLine_UsesTheLaunchPolicyFlag()
        => Assert.AreEqual(
            $"\"C:\\wmd.exe\" {StartupLaunchPolicy.StartupMinimizedArg}",
            AutostartPolicy.BuildCommandLine(@"C:\wmd.exe"));

    [TestMethod]
    public void BuildCommandLine_BlankExePath_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => AutostartPolicy.BuildCommandLine(""));
        Assert.ThrowsExactly<ArgumentException>(() => AutostartPolicy.BuildCommandLine("   "));
    }

    private static string ConstValue(string name)
        => (string)typeof(AutostartPolicy).GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
}
