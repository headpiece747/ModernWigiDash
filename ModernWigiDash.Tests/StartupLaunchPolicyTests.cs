using System.Reflection;
using ModernWigiDash.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// The autostart launch-argument policy pinned without a process (ADR-0019):
/// the --startup flag's presence rule (case-insensitive, null/empty-safe,
/// exact-match only) and the flag's constant spelling, which the Run entry's
/// command line is built from, so the written side and the read side can
/// never drift.
/// </summary>
[TestClass]
public class StartupLaunchPolicyTests
{
    [TestMethod]
    public void RequestsMinimizedStart_FlagPresent_True()
        => Assert.IsTrue(StartupLaunchPolicy.RequestsMinimizedStart(["--startup"]));

    [TestMethod]
    public void RequestsMinimizedStart_FlagAmongOtherArgs_True()
        => Assert.IsTrue(StartupLaunchPolicy.RequestsMinimizedStart(["--other", "--startup", "x"]));

    [TestMethod]
    public void RequestsMinimizedStart_FlagIsCaseInsensitive_True()
        => Assert.IsTrue(StartupLaunchPolicy.RequestsMinimizedStart(["--STARTUP"]));

    [TestMethod]
    public void RequestsMinimizedStart_FlagAbsent_False()
        => Assert.IsFalse(StartupLaunchPolicy.RequestsMinimizedStart(["--other", "x"]));

    [TestMethod]
    public void RequestsMinimizedStart_EmptyOrNullArgs_False()
    {
        Assert.IsFalse(StartupLaunchPolicy.RequestsMinimizedStart([]));
        Assert.IsFalse(StartupLaunchPolicy.RequestsMinimizedStart(null));
    }

    [TestMethod]
    public void RequestsMinimizedStart_SimilarArgs_DoNotMatch()
        => Assert.IsFalse(StartupLaunchPolicy.RequestsMinimizedStart(["--startup-extras", "startup", "x --startup"]));

    [TestMethod]
    public void StartupMinimizedArg_IsTheDocumentedSpelling()
        => Assert.AreEqual("--startup", ConstValue(nameof(StartupLaunchPolicy.StartupMinimizedArg)));

    // The const value is read through reflection (the ArchitectureTests
    // ConstValue shape): a direct literal comparison folds into the constant
    // the test would police.
    private static string ConstValue(string name)
        => (string)typeof(StartupLaunchPolicy).GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
}
