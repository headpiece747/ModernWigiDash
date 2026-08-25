namespace ModernWigiDash.Widgets;

/// <summary>
/// The neutral repeat-mode vocabulary the media session seam speaks: the one
/// projection of the WinRT SMTC auto-repeat mode that the monitor, the
/// presentation, and the widget read. The edge mapping in
/// <c>WinRtMediaSessionSource</c> projects by name (the repeat enum happens
/// to share its ordinals with this one, but the mapping does not rely on
/// it), and <see cref="Unknown"/> is the fallback for any value the OS
/// reports that the named set does not cover; it degrades to the repeat
/// cycle's start through <c>NowPlayingPresentation.NextRepeatMode</c>.
/// </summary>
public enum MediaRepeatMode
{
    None = 0,

    Track = 1,

    List = 2,

    Unknown = 3
}
