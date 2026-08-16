namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// The per-poll outcome kind of the frame-time producer, carried in
/// <see cref="FrameTimePollDiagnostics"/>. Distinguishes the three Idle DTO
/// shapes the log would otherwise conflate: no candidates, the settling
/// window, and the frozen-data hold.
/// </summary>
internal enum FrameTimeDiagOutcome
{
    /// <summary>Library missing or the session could not be opened.</summary>
    Unavailable,

    /// <summary>No candidates (desktop / own window) — idle.</summary>
    NoCandidates,

    /// <summary>The live target left the foreground; the new one is settling
    /// (tracked, never polled).</summary>
    Settling,

    /// <summary>An adopted target's sample is still the departed target's
    /// frozen data — held at zero.</summary>
    FrozenHold,

    /// <summary>The target presents but nothing reaches the display
    /// (PM_METRIC_DISPLAYED_FPS = 0) — the zero state.</summary>
    NotDisplayed,

    /// <summary>A live sample was reported.</summary>
    Live,

    /// <summary>The capture grace window elapsed with no present data.</summary>
    CaptureDead,

    /// <summary>The service session/pipe broke mid-poll.</summary>
    SessionLost,

    /// <summary>Tracked with no data yet, inside the grace window.</summary>
    Idle,
}
