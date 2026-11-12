namespace ModernWigiDash.App;

/// <summary>
/// The one rule for attributing a window close that stands: a cause recorded
/// before the close (tray quit, update restart, second launch, an actually
/// ending session) wins, a cancelled close records nothing, and an unattributed
/// standing close is the user closing the window. Pure, so the attribution is
/// pinned independently of the window and the process static.
/// </summary>
internal static class ExitReasonPolicy
{
    internal const string UserClose = "user-close";
    internal const string TrayQuit = "tray-quit";
    internal const string UpdateRestart = "update-restart";
    internal const string SessionEnd = "session-end";
    internal const string SecondLaunch = "second-launch";

    /// <summary>
    /// The reason to record for a close event: the existing reason unchanged
    /// when the close was cancelled or already attributed, otherwise the user
    /// close.
    /// </summary>
    public static string? ForClose(bool cancelled, string? recorded)
        => cancelled ? recorded : recorded ?? UserClose;
}
