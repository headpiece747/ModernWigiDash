using ModernWigiDash.Core.Theming;
using ModernWigiDash.App.Theming;

namespace ModernWigiDash.App;

/// <summary>
/// The manual profile import flow (App): the SEQUENCE over the import
/// boundary's verdicts (<see cref="ProfileOps.ImportProfileFile"/> is the
/// one producer). For the <see cref="ProfileImportOutcome.Loaded"/> verdict
/// it merges the close behavior (an imported profile lacking it keeps the
/// local value, so the next export carries it), runs the one swap site
/// through the host seam, and only after a successful swap offers the
/// bundle's theme (the <see cref="ThemeRestorePolicy"/> gate, the user's
/// confirm, the apply). The rejection verdicts surface their error line and
/// the absent file is a silent no-op, so a declined or failed theme never
/// undoes the imported profile and a failed import never offers the theme
/// of a profile that was not applied. The window keeps only the file dialog
/// and the production-host implementation (the ADR-0008 image,
/// <see cref="IProfileImportHost"/>), and the sequence is pinned at this
/// module's interface without a window (ProfileImportFlowTests).
/// </summary>
internal static class ProfileImportFlow
{
    /// <summary>Runs the import flow over one boundary verdict. The dialog
    /// the user picked is already resolved; this owns everything between
    /// the verdict and the window's state.</summary>
    public static ProfileImportFlowOutcome Run(ProfileImportOutcome verdict, IProfileImportHost host)
    {
        switch (verdict)
        {
            case ProfileImportOutcome.Loaded(var loaded, var bundledTheme):
                return RunLoaded(loaded, bundledTheme, host);
            case ProfileImportOutcome.TooLarge:
                host.ShowError("Import Error", "The selected profile file is too large to import.");
                return new ProfileImportFlowOutcome.TooLarge();
            case ProfileImportOutcome.Failed(var detail):
                host.ShowError("Import Error", $"Error importing profile: {detail}");
                return new ProfileImportFlowOutcome.Failed(detail);
            case ProfileImportOutcome.Absent:
                // A delete between the dialog and the read is a benign no-op:
                // the file the dialog handed back is gone.
                return new ProfileImportFlowOutcome.Absent();
        }
        // The boundary's verdict type is open to derivation; this tail is a
        // construction fact, not a reachable path.
        throw new InvalidOperationException($"Unhandled import verdict: {verdict}");
    }

    private static ProfileImportFlowOutcome RunLoaded(ProfileLayout loaded, ThemeSettings? bundledTheme, IProfileImportHost host)
    {
        // The close behavior travels with the JSON, but an imported profile
        // lacking it ("no opinion") must not drop the local value: the merge
        // re-stamps the local close behavior onto the imported profile before
        // the swap, so the next export carries it.
        loaded.CloseBehavior = CloseBehaviorPolicy.MergeImport(loaded.CloseBehavior, host.LocalCloseBehavior);

        // One swap site: the host disposes the old profile's widget instances
        // and runs the post-mutation refresh; a failure keeps the old profile
        // live and vetoes the theme offer below.
        if (!host.SwapProfile(loaded))
            return new ProfileImportFlowOutcome.SwapFailed();

        // The bundle's theme item runs only after a successful profile swap:
        // the offer gate (null + fingerprint), the user's confirm, the apply.
        bool themeRestored = false;
        if (ThemeRestorePolicy.ShouldOffer(bundledTheme, host.CurrentTheme)
            && host.ConfirmThemeRestore())
        {
            host.ApplyTheme(bundledTheme);
            themeRestored = true;
        }
        return new ProfileImportFlowOutcome.Imported(themeRestored);
    }
}
