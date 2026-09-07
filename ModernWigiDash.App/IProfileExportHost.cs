using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App;

/// <summary>
/// The manual profile export flow's named host seam (the ADR-0008 image): the
/// one contract between <see cref="ProfileExportFlow"/> and the window that
/// hosts it. The flow reads the active profile and the machine's theme through
/// the two getters, routes the success and error lines through the named
/// members, and reaches the user's dialog surface only through them, so the
/// flow's sequence is testable against an in-memory fake host and the window is
/// the production host (a thin adapter over its state and dialog host).
/// </summary>
internal interface IProfileExportHost
{
    /// <summary>The active profile to export (never null: a corrupt or absent
    /// file degrades to the defaults at load).</summary>
    ProfileLayout Profile { get; }

    /// <summary>The machine's active theme (never null: a corrupt or absent
    /// state file degrades to the defaults): the section the export bundle
    /// carries as its per-item restore item.</summary>
    ThemeSettings CurrentTheme { get; }

    /// <summary>The flow's one dialog surface for the success line (the host
    /// owns the dialog chrome).</summary>
    void ShowSuccess(string title, string message);

    /// <summary>The flow's one dialog surface for the error line (the host
    /// owns the dialog chrome).</summary>
    void ShowError(string title, string message);
}
