namespace ModernWigiDash.Widgets;

/// <summary>
/// The neutral playback-status vocabulary the media session seam speaks: the
/// one projection of the WinRT SMTC status that the monitor, the
/// presentation, and the widget read. The edge mapping in
/// <c>WinRtMediaSessionSource</c> projects by name: the members mirror the
/// SMTC member NAMES, not the SMTC ordinals (the status enum in particular
/// does not share them, and the mapping does not rely on them), and
/// <see cref="Unknown"/> is the fallback for any value the OS reports that
/// the named set does not cover; it reads like neither a playing nor a
/// terminal state (it is not idle and it is not playing).
/// </summary>
public enum MediaPlaybackStatus
{
    Opened = 0,

    Playing = 1,

    Paused = 2,

    Stopped = 3,

    Closed = 4,

    Changing = 5,

    Unknown = 6
}
