namespace ModernWigiDash.Sdk;

/// <summary>
/// How a reader acquires a producer-owned named map's writer mutex: the budget,
/// the abandoned-mutex rule, the diagnostic when the budget is spent, and the
/// best-effort release.
///
/// Two producers publish into named maps this app reads - LibreHardwareService's
/// sensors map and G.SKILL's AIDA64 panel map - and their adapters each carried
/// their own copy of these four decisions. The two constants agreed by accident
/// and the two diagnostics did not: one said "within 100ms", the other "within
/// 100 ms", and each retyped the number the constant already held, so the budget
/// was written down three times per adapter and changing it meant finding all
/// three (found by the architecture review, 2026-09-27).
///
/// What stays in the adapters is the part that genuinely differs, and one point
/// where they DISAGREE on purpose:
///
/// <list type="bullet">
/// <item>LHM opens the mutex per read and copies the whole map under a size cap;
/// AIDA64 probes the mutex once for the source's lifetime and reads a byte range
/// through a cached accessor.</item>
/// <item>What an unusable handle means. AIDA64 treats a wedged mutex as a
/// licence to copy unlocked (a wedged handle is not a map failure). LHM lets it
/// propagate and reports the map unavailable. That is a real disagreement about
/// diagnostics, not a duplication, so <see cref="TryAcquire"/> lets every
/// exception except the abandoned one through and each adapter keeps its own
/// rule over it.</item>
/// </list>
///
/// The budget is deliberately short. This is a per-frame poll of somebody else's
/// live telemetry: a writer holding the mutex for longer than 100 ms is not
/// going to release it inside the next frame, so waiting longer would only make
/// the panel staler.
/// </summary>
internal static class NamedMapAcquirePolicy
{
    /// <summary>How long to wait for the producer's mutex before giving up on
    /// the frame. See the type doc for why it is short.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Acquires the producer's mutex within <see cref="Timeout"/>.
    /// </summary>
    /// <param name="mutex">The producer's named mutex.</param>
    /// <returns>True when the mutex is held by the caller (and must be released
    /// through <see cref="Release"/>), false when the budget was spent.</returns>
    /// <exception cref="AbandonedMutexException">Never: an abandoned mutex is
    /// reported as ACQUIRED, because only the producer writes this map and a dead
    /// producer's mutex is safe to continue under. Every other exception
    /// propagates, so the caller's own rule for an unusable handle applies.</exception>
    internal static bool TryAcquire(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(Timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    /// <summary>The diagnostic for a spent budget, with the number formatted from
    /// <see cref="Timeout"/> so the budget is written down once.</summary>
    /// <param name="subject">What was being acquired, e.g. "AIDA64 map mutex".</param>
    internal static string NotAcquiredMessage(string subject)
        => $"{subject} not acquired within {Timeout.TotalMilliseconds:0} ms (writer holds it)";

    /// <summary>Releases a mutex this caller holds. Best-effort: the producer
    /// may have exited while holding it, and releasing an orphaned mutex throws
    /// <see cref="ApplicationException"/>, which is not a failure of this read.</summary>
    internal static void Release(Mutex mutex)
    {
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The writer's process exited while the mutex was held.
        }
    }
}
