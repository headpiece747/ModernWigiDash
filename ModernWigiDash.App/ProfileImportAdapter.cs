using System.Windows;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App;

/// <summary>
/// The named adapter over the profile-import flow's host seam (the ADR-0008
/// image): owns the swap logic (ReplaceProfile + the RawWrite funnel + the
/// error leg) and the theme-apply logic (Replace + SaveCurrent + the
/// session-only failure line + the resource re-apply), so the flow's
/// production host is this module, not a second copy of the flow's order in
/// the window's interface impl. The live providers read the window's wired
/// state at call time (the wiring steps run after construction, so the
/// dialog host in particular is read through the provider, never captured).
/// </summary>
internal sealed class ProfileImportAdapter(
    Func<ProfileLayout> profileProvider,
    Action<ProfileLayout> profileSetter,
    Action applyRawWrite,
    Func<DialogHost> dialogHostProvider,
    ThemeApplicator themeApplicator,
    Window window) : IProfileImportHost
{
    /// <inheritdoc/>
    public string? LocalCloseBehavior => profileProvider().CloseBehavior;

    /// <inheritdoc/>
    public ThemeSettings CurrentTheme => ThemeStore.Current;

    /// <inheritdoc/>
    public bool SwapProfile(ProfileLayout imported)
    {
        try
        {
            // One swap site: ReplaceProfile disposes the old profile's widget
            // instances and returns the imported profile active.
            profileSetter(ProfileOps.ReplaceProfile(profileProvider(), imported));

            // The funnel owns everything after the swap - tab strip, picker,
            // and the snap-to-grid resync (a RawWrite): the old force-write of
            // the checkbox is gone.
            applyRawWrite();
            return true;
        }
        catch (Exception ex)
        {
            dialogHostProvider().Error("Import Error", $"Error importing profile: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public bool ConfirmThemeRestore()
        => dialogHostProvider().Confirm("Restore Theme", "The imported profile includes a theme. Apply it to this machine?");

    /// <inheritdoc/>
    public void ApplyTheme(ThemeSettings theme)
    {
        ThemeStore.Replace(theme);
        if (!ThemeStore.SaveCurrent())
        {
            dialogHostProvider().Error(
                "Theme Save Failed",
                "Could not write app_theme.json to the user state directory. The colors apply for this session only.");
        }
        themeApplicator.Apply(window);
    }

    /// <inheritdoc/>
    public void ShowError(string title, string message) => dialogHostProvider().Error(title, message);
}
