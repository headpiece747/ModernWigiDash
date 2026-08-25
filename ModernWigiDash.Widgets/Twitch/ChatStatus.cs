namespace ModernWigiDash.Widgets.Twitch;

/// <summary>
/// The chat connection state — an enum so the render switches are exhaustive
/// and illegal states are unrepresentable.
/// </summary>
public enum ChatStatus
{
    /// <summary>No chat connection (never started, or stopped).</summary>
    Disconnected,
    /// <summary>The IRC connection is being established.</summary>
    Connecting,
    /// <summary>The chat is connected and receiving messages.</summary>
    Connected
}
