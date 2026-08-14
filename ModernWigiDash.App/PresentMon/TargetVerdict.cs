namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// The before-polling verdict of the target-trust policy. <see cref="TrackOnly"/>
/// means the caller must apply tracking to the candidates but must NOT poll
/// them (their samples are untrustworthy during the settling window) — the
/// ordering constraint is part of the verdict's contract.
/// </summary>
internal enum TargetVerdict
{
    TrackOnly,
    Poll,
}
