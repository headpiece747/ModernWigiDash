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
    /// <summary>The session is open and idle.</summary>
    Opened = 0,

    /// <summary>Playback is running.</summary>
    Playing = 1,

    /// <summary>Playback is paused.</summary>
    Paused = 2,

    /// <summary>Playback has stopped.</summary>
    Stopped = 3,

    /// <summary>The session is closed (no active media).</summary>
    Closed = 4,

    /// <summary>The status is in transition.</summary>
    Changing = 5,

    /// <summary>A status the OS reports that the named set does not cover.</summary>
    Unknown = 6
}
