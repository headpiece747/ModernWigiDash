namespace ModernWigiDash.Widgets.Twitch;

/// <summary>
/// The chat connection state — an enum so the render switches are exhaustive
/// and illegal states are unrepresentable.
/// </summary>
public enum ChatStatus
{
    Disconnected,
    Connecting,
    Connected
}
