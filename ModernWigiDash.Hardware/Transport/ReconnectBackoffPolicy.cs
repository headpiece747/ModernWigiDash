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
    /// The delay before attempt number <paramref name="consecutiveFailures"/> + 1.
    /// </summary>
    /// <param name="consecutiveFailures">Failures since the last success. Zero
    /// (or anything below one) yields <see cref="FirstDelay"/>; the value
    /// doubles per failure and saturates at <see cref="MaxDelay"/>, so a long
    /// absence cannot overflow the interval into a negative or absurd delay.
    /// </param>
    public static TimeSpan DelayFor(int consecutiveFailures)
    {
        if (consecutiveFailures < 1)
            return FirstDelay;

        // Double from the first delay, saturating at the ceiling. Shifting by
        // the exponent is safe up to 31; past that the ceiling clamp applies
        // first, so the shift never runs off the end of the word.
        int doublings = Math.Min(consecutiveFailures - 1, 31);
        double scaled = FirstDelay.TotalMilliseconds * Math.Pow(2, doublings);
        return scaled >= MaxDelay.TotalMilliseconds
            ? MaxDelay
            : TimeSpan.FromMilliseconds(scaled);
    }
}
