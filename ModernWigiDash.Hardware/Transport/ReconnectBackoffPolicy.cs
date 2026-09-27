using ModernWigiDash.Sdk;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// How long to wait before the next connect attempt, as a function of how many
/// have failed in a row. A flat period (the 5 s it used to be) is wrong in both
/// directions: when a display is genuinely absent it re-probes forever at full
/// rate, and when one is merely BUSY — the 2026-09-27 case, where the handle is
/// held by another app until that app exits — the re-probe is what keeps the
/// handle denied, because whoever wins the race keeps it.
///
/// So the delay doubles per consecutive failure and stops at a ceiling: an
/// absent display settles into one probe per ceiling instead of twelve per
/// minute, and a busy one stops fighting for the handle and picks it up within
/// the ceiling of the other client letting go.
///
/// A pure function of the attempt count — no clock, no timer, no state — so the
/// whole policy is drivable from a test and the engine owns only the counter.
/// The arithmetic is the shared <see cref="ReconnectBackoff.DelayFor"/>; the
/// two numbers below are this display's own configuration.
/// </summary>
internal static class ReconnectBackoffPolicy
{
    /// <summary>The wait after the first failure. Deliberately the old flat
    /// period, so the fast first retry (the common case: a cable reseat, a
    /// resume from sleep) is unchanged.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);

    /// <summary>The ceiling: the longest the policy will ever wait, so a display
    /// that comes back is still picked up promptly no matter how long it was
    /// gone. Two minutes reads as "still watching" while keeping a permanently
    /// absent device to half a probe a minute.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The delay before attempt number <paramref name="consecutiveFailures"/> + 1,
    /// computed by the one shared rule (<see cref="ReconnectBackoff"/>). This
    /// class owns the DISPLAY's two numbers and nothing else - the doubling
    /// arithmetic used to be spelled here and, separately, in the widget feed
    /// loops, in a different shape, and the two answers had drifted apart
    /// (2026-09-27).
    /// </summary>
    /// <param name="consecutiveFailures">Failures since the last success, the
    /// counter the engine owns.</param>
    public static TimeSpan DelayFor(int consecutiveFailures)
        => ReconnectBackoff.DelayFor(consecutiveFailures, FirstDelay, MaxDelay);
}
