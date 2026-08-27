using System.Diagnostics.CodeAnalysis;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App.Theming;

/// <summary>
/// The export bundle's theme-restore offer rule (ADR-0021): the bundled theme
/// is offered only when it is present AND differs from the current theme (the
/// applicator's fingerprint is the one change signal, so a default-theme
/// export never prompts a default-themed machine). The confirm and the
/// apply sequence stay in the window (the thin adapter); this is the pure
/// decision pinned at its interface (ThemeRestorePolicyTests).
/// </summary>
internal static class ThemeRestorePolicy
{
    /// <summary>Whether to offer the bundle's theme: false for a bundle that
    /// carries no theme, false when its fingerprint matches the current one,
    /// true otherwise (a different theme is worth one confirm).</summary>
    internal static bool ShouldOffer([NotNullWhen(true)] ThemeSettings? bundled, ThemeSettings current)
        => bundled is not null
        && !string.Equals(
            ThemeApplicator.Fingerprint(bundled),
            ThemeApplicator.Fingerprint(current),
            StringComparison.Ordinal);
}
