using System.IO;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App;

/// <summary>
/// The manual profile export flow (App): the SEQUENCE over one chosen file path
/// -- compose the export bundle (the profile's bare JSON plus the active theme
/// as the optional ADR-0021 section), write it to the path, and surface the
/// success or error line. The window keeps only the file dialog and the
/// production-host implementation (<see cref="IProfileExportHost"/>); the
/// sequence is pinned at this module's interface without a window
/// (ProfileExportFlowTests). The persisted profile.json stays bare (the theme
/// rides only the manual export bundle), so the composition here is the one
/// site that adds the section.
/// </summary>
internal static class ProfileExportFlow
{
    /// <summary>Runs the export flow for one resolved file path (the dialog the
    /// user picked is already resolved; this owns everything between the path
    /// and the window's state).</summary>
    public static ProfileExportFlowOutcome Run(string filePath, IProfileExportHost host)
    {
        try
        {
            // The theme rides the export bundle (ADR-0021) as a per-item restore
            // item; the persisted profile.json stays bare.
            string json = ProfileExportTheme.WithTheme(ProfileOps.ExportJson(host.Profile), host.CurrentTheme);
            File.WriteAllText(filePath, json);
            host.ShowSuccess("Export Complete", "Profile exported successfully!");
            return new ProfileExportFlowOutcome.Exported();
        }
        catch (Exception ex)
        {
            host.ShowError("Export Error", $"Error exporting profile: {ex.Message}");
            return new ProfileExportFlowOutcome.Failed(ex.Message);
        }
    }
}
