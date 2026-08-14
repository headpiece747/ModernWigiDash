namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// One poll's target diagnostics — the evidence for target-transition bugs:
/// what the resolver reported, the trust-policy state, and the outcome kind.
/// The telemetry tick composes the single log format from this record; the
/// producer never formats strings for the log.
/// </summary>
/// <param name="ForegroundTitle">The foreground window's title (capped), or null.</param>
/// <param name="Candidates">The resolved candidate pids.</param>
/// <param name="LiveRootPid">The pid that produced the last live sample.</param>
/// <param name="ForeignStreak">Consecutive polls the live root has been absent.</param>
/// <param name="CheckingAdopted">True while the frozen-data guard holds.</param>
/// <param name="Outcome">What this poll decided.</param>
public sealed record FrameTimePollDiagnostics(
    string? ForegroundTitle,
    IReadOnlyList<int> Candidates,
    int LiveRootPid,
    int ForeignStreak,
    bool CheckingAdopted,
    FrameTimeDiagOutcome Outcome);
