namespace ModernWigiDash.Widgets;

/// <summary>
/// The capture-window guard: the stale-identity capture window (ADR-0006)
/// as a named, assertable module. Holds the resolution key a fetch started
/// for (captured ONCE before the first await — the key and the fetch resolve
/// from the same record, so no seam read can drift between them) and
/// re-checks it at every await boundary against the live key the seam hands
/// it. The re-check is the ADR-0006 predicate
/// (<see cref="WeatherQueryKey.SameKey"/>) applied to (start key, live key);
/// a live key that differs while the window is open is the DROP verdict —
/// the result belongs to the old identity and must not be applied or cached.
/// <para>
/// The two windows the ADRs split are two adapters of this guard: the
/// client's guard reads the fetch-control's identity state (the invalidation
/// the edit path lands through the client), and the flow's guard reads the
/// live location (a silent reassignment the client's state does not see).
/// Both re-checks are this one rule; the ADRs are not re-litigated — the
/// client's atomic stamp transitions (ConfirmAndStamp, Stamp) keep their
/// gate atomicity, and every site still routes through the ADR-0006
/// predicate.
/// </para>
/// </summary>
/// <param name="startKey">The resolution key this window started for (the
/// capture — built from the same location record the fetch runs on).</param>
/// <param name="liveKey">The live key source, read per re-check: the
/// client-side adapter reads the fetch-control's current identity query, the
/// flow-side adapter re-derives the key from the live location.</param>
internal sealed class CaptureWindowGuard(string startKey, Func<string> liveKey)
{
    private readonly Func<string> _liveKey = liveKey;

    /// <summary>The resolution key this window started for (the capture).</summary>
    public string StartKey { get; } = startKey;

    /// <summary>
    /// The single re-check, spelled once: the resolution identity is still
    /// the one this window started for. Every await boundary of the client's
    /// fetch and the flow's return-to-apply re-validates through this — one
    /// comparison shape, the ADR-0006 predicate, never a second rule.
    /// </summary>
    public bool StillCurrent()
        => WeatherQueryKey.SameKey(StartKey, _liveKey());

    /// <summary>
    /// The drop verdict: the identity changed while the window was open —
    /// the result belongs to the old identity and must be dropped (never
    /// applied or cached), with the new identity re-fetched immediately.
    /// </summary>
    public bool Dropped => !StillCurrent();
}
