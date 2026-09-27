namespace ModernWigiDash.Sdk;

/// <summary>
/// How long to wait before the next connect attempt, as a function of how many
/// have failed in a row. A pure function of the failure count - no clock, no
/// timer, no state - so the whole rule is drivable from a test and the caller
/// owns only the counter.
///
/// A flat period is wrong in both directions: when a target is genuinely absent
/// it re-probes forever at full rate, and when one is merely BUSY - the
/// 2026-09-27 case, where the display handle is held by another app until that
/// app exits - the re-probe is what keeps the handle denied, because whoever
/// wins the race keeps it.
///
/// This is the ONE spelling of that arithmetic. It used to exist twice, in two
/// assemblies, in two shapes: a pure static here in the display engine's project
/// and a stateful policy class in the widget feed loops, and the two production
/// answers disagreed - the display grew its wait to 2 minutes while the price
/// feeds retried at a flat 5 s forever, with nothing in the code, the glossary or
/// a test saying that was deliberate (found by the architecture review,
/// 2026-09-27). The first delay and the ceiling are per-caller CONFIGURATION and
/// live with the caller; the doubling rule is the domain and lives here.
/// </summary>
internal static class ReconnectBackoff
{
    /// <summary>
    /// The wait before attempt number <paramref name="consecutiveFailures"/> + 1.
    /// </summary>
    /// <param name="consecutiveFailures">Failures since the last success. Zero
    /// (or anything below one) yields <paramref name="first"/>; the value doubles
    /// per failure and saturates at <paramref name="max"/>, so a long absence
    /// cannot overflow the interval into a negative or absurd delay.</param>
    /// <param name="first">The wait after the first failure - the fast first
    /// retry, the common case (a cable reseat, a resume from sleep, an exchange
    /// that blipped).</param>
    /// <param name="max">The ceiling: the longest this will ever wait, so a
    /// target that comes back is still picked up promptly no matter how long it
    /// was gone. The contract is <c>max &gt;= first</c> - a caller that inverts
    /// them gets the ceiling on every attempt, which is a pinned
    /// ceiling-is-longer-than-the-first-delay case, not a rule the core silently
    /// repairs.</param>
    public static TimeSpan DelayFor(int consecutiveFailures, TimeSpan first, TimeSpan max)
    {
        if (consecutiveFailures < 1)
            return first;

        // Double from the first delay, saturating at the ceiling. Shifting by the
        // exponent is safe up to 31; past that the ceiling clamp applies first,
        // so the shift never runs off the end of the word.
        int doublings = Math.Min(consecutiveFailures - 1, 31);
        double scaled = first.TotalMilliseconds * Math.Pow(2, doublings);
        return scaled >= max.TotalMilliseconds
            ? max
            : TimeSpan.FromMilliseconds(scaled);
    }
}
