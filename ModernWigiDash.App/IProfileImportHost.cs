using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App;

/// <summary>
/// The manual profile import flow's named host seam (the ADR-0008 image):
/// the one contract between <see cref="ProfileImportFlow"/> and the window
/// that hosts it. The flow reads the merge operand and the current theme
/// through the two getters, routes the one swap site, the theme-restore
/// confirm, and the theme apply through the named members, and reaches the
/// user's dialog surface only through <see cref="ShowError"/>, so the flow's
/// sequence is testable against an in-memory fake host and the window is the
/// production host (a thin adapter over its state and dialog host).
/// </summary>
internal interface IProfileImportHost
{
    /// <summary>The local profile's raw close-behavior value (null when the
    /// profile has no opinion): the merge operand for an imported profile
    /// that lacks one.</summary>
    string? LocalCloseBehavior { get; }

    /// <summary>The machine's active theme (never null: a corrupt or absent
    /// state file degrades to the defaults): the fingerprint gate's current
    /// side of the theme-restore offer decision.</summary>
    ThemeSettings CurrentTheme { get; }

    /// <summary>The one swap site: apply the imported profile to the window
    /// (disposing the old profile's widget instances) and run the
    /// post-mutation refresh. Returns false when the swap or the refresh
    /// threw; the host has already surfaced the error line in that case.</summary>
    bool SwapProfile(ProfileLayout imported);

    /// <summary>The theme-restore confirm (the user's decision); the flow
    /// calls it only after the offer gate passed.</summary>
    bool ConfirmThemeRestore();

    /// <summary>Apply a confirmed bundle theme: replace the active theme,
    /// persist the state-dir file (a failed write surfaces the host's
    /// session-only line), and re-apply the resources.</summary>
    void ApplyTheme(ThemeSettings theme);

    /// <summary>The flow's one dialog surface: the error lines for the
    /// rejection verdicts (the host owns the dialog chrome).</summary>
    void ShowError(string title, string message);
}
