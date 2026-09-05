using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

/// <summary>
/// The manual profile import flow pinned at the module's interface (no
/// window, no filesystem): the Loaded verdict's close-behavior merge (an
/// imported profile lacking one keeps the local value, a present imported
/// value wins), the one swap site, and the post-swap theme offer (the
/// ThemeRestorePolicy gate, the user's confirm, the apply) with its vetoes
/// (a failed swap, a declined confirm, and a same-fingerprint bundle never
/// reach the confirm); the rejection verdicts surface their error line and
/// the absent file is a silent no-op. The sequence is driven through the
/// in-memory fake host (the ADR-0008 image).
/// </summary>
[TestClass]
public class ProfileImportFlowTests
{
    private sealed class FakeHost : IProfileImportHost
    {
        public string? LocalCloseBehavior { get; set; }
        public ThemeSettings CurrentTheme { get; set; } = new ThemeSettings();
        public bool SwapSucceeds { get; set; } = true;
        public bool ConfirmResult { get; set; } = true;
        public ProfileLayout? Swapped { get; private set; }
        public int ConfirmCalls { get; private set; }
        public ThemeSettings? AppliedTheme { get; private set; }
        public List<(string Title, string Message)> Errors { get; } = [];

        public bool SwapProfile(ProfileLayout imported)
        {
            if (!SwapSucceeds) return false;
            Swapped = imported;
            return true;
        }

        public bool ConfirmThemeRestore()
        {
            ConfirmCalls++;
            return ConfirmResult;
        }

        public void ApplyTheme(ThemeSettings theme) => AppliedTheme = theme;

        public void ShowError(string title, string message) => Errors.Add((title, message));
    }

    /// <summary>A bundled theme that differs from the default (the
    /// fingerprint gate's offer case).</summary>
    private static ThemeSettings DistinctTheme() => new() { BgPanel = "#FF0000" };

    [TestMethod]
    public void Run_Loaded_KeepsTheLocalCloseBehavior_WhenTheImportedProfileLacksOne()
    {
        var host = new FakeHost { LocalCloseBehavior = CloseBehaviorPolicy.HideToTray };
        var outcome = ProfileImportFlow.Run(new ProfileImportOutcome.Loaded(new ProfileLayout()), host);
        Assert.AreEqual(CloseBehaviorPolicy.HideToTray, host.Swapped!.CloseBehavior,
            "an imported profile lacking the field keeps the local value (the next export carries it)");
        Assert.AreEqual(new ProfileImportFlowOutcome.Imported(false), outcome);
        Assert.AreEqual(0, host.ConfirmCalls, "a bundle without a theme never reaches the confirm");
    }

    [TestMethod]
    public void Run_Loaded_TheImportedCloseBehaviorWins_OverTheLocalValue()
    {
        var host = new FakeHost { LocalCloseBehavior = CloseBehaviorPolicy.HideToTray };
        var imported = new ProfileLayout { CloseBehavior = CloseBehaviorPolicy.Quit };
        ProfileImportFlow.Run(new ProfileImportOutcome.Loaded(imported), host);
        Assert.AreEqual(CloseBehaviorPolicy.Quit, host.Swapped!.CloseBehavior,
            "a present imported value wins over the local one");
    }

    [TestMethod]
    public void Run_Loaded_ThemeOfferConfirmed_AppliesTheBundledTheme()
    {
        var host = new FakeHost();
        var bundled = DistinctTheme();
        var outcome = ProfileImportFlow.Run(new ProfileImportOutcome.Loaded(new ProfileLayout(), bundled), host);
        Assert.AreEqual(new ProfileImportFlowOutcome.Imported(true), outcome);
        Assert.AreEqual(1, host.ConfirmCalls);
        Assert.AreSame(bundled, host.AppliedTheme);
    }

    [TestMethod]
    public void Run_Loaded_ThemeOfferDeclined_SkipsTheApply()
    {
        var host = new FakeHost { ConfirmResult = false };
        var outcome = ProfileImportFlow.Run(new ProfileImportOutcome.Loaded(new ProfileLayout(), DistinctTheme()), host);
        Assert.AreEqual(new ProfileImportFlowOutcome.Imported(false), outcome,
            "a declined confirm is a no-op: the machine's theme stays");
        Assert.AreEqual(1, host.ConfirmCalls);
        Assert.IsNull(host.AppliedTheme);
    }

    [TestMethod]
    public void Run_Loaded_SameFingerprintTheme_NeverOffers()
    {
        var host = new FakeHost();
        var outcome = ProfileImportFlow.Run(
            new ProfileImportOutcome.Loaded(new ProfileLayout(), new ThemeSettings()), host);
        Assert.AreEqual(new ProfileImportFlowOutcome.Imported(false), outcome);
        Assert.AreEqual(0, host.ConfirmCalls, "a default-theme export never prompts a default-themed machine");
        Assert.IsNull(host.AppliedTheme);
    }

    [TestMethod]
    public void Run_Loaded_SwapFailure_NeverOffersTheTheme()
    {
        var host = new FakeHost { SwapSucceeds = false };
        var outcome = ProfileImportFlow.Run(new ProfileImportOutcome.Loaded(new ProfileLayout(), DistinctTheme()), host);
        Assert.AreEqual(new ProfileImportFlowOutcome.SwapFailed(), outcome);
        Assert.AreEqual(0, host.ConfirmCalls, "a failed import never offers the theme of a profile that was not applied");
        Assert.IsNull(host.AppliedTheme);
    }

    [TestMethod]
    public void Run_TooLarge_SurfacesTheErrorLine()
    {
        var host = new FakeHost();
        var outcome = ProfileImportFlow.Run(new ProfileImportOutcome.TooLarge(2_000_000), host);
        Assert.AreEqual(new ProfileImportFlowOutcome.TooLarge(), outcome);
        CollectionAssert.AreEqual(
            new[] { ("Import Error", "The selected profile file is too large to import.") },
            host.Errors);
        Assert.IsNull(host.Swapped);
    }

    [TestMethod]
    public void Run_Failed_SurfacesTheDetailError()
    {
        var host = new FakeHost();
        var outcome = ProfileImportFlow.Run(new ProfileImportOutcome.Failed("unparseable json"), host);
        Assert.AreEqual(new ProfileImportFlowOutcome.Failed("unparseable json"), outcome);
        CollectionAssert.AreEqual(
            new[] { ("Import Error", "Error importing profile: unparseable json") },
            host.Errors);
        Assert.IsNull(host.Swapped);
    }

    [TestMethod]
    public void Run_Absent_IsASilentNoOp()
    {
        var host = new FakeHost();
        var outcome = ProfileImportFlow.Run(new ProfileImportOutcome.Absent(), host);
        Assert.AreEqual(new ProfileImportFlowOutcome.Absent(), outcome);
        Assert.AreEqual(0, host.Errors.Count, "a delete between the dialog and the read is a benign no-op");
        Assert.IsNull(host.Swapped);
    }
}
