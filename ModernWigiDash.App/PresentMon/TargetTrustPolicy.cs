namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// The target-trust policy behind the frame-time producer: only the pid that
/// produced the last live sample may report live data. PresentMon's poll
/// returns the departed target's frozen data for every pid after a foreground
/// switch (observed on-device), so a new foreground is adopted only after it
/// survives the settling window, and an adopted target's sample is only
/// reported once it differs from the departed target's last values (the
/// frozen-data guard). The producer owns the sequencing — session, tracking
/// reconciliation, polling — and asks this module for the two decisions:
/// <see cref="Decide"/> before polling, <see cref="IsFrozenSample"/> and
/// <see cref="NoteLive"/> after. The <see cref="TrackOnly"/> verdict carries
/// the ordering contract: apply tracking, never poll.
/// </summary>
internal sealed class TargetTrustPolicy
{
    /// <summary>
    /// Consecutive polls a new foreground must survive before its root is
    /// adopted as the live target (see the type doc).
    /// </summary>
    internal const int AdoptAfterPolls = 3;

    private int _liveRootPid;
    private int _foreignStreak;
    private bool _checkingAdopted;
    private string _adoptedKey = "";
    private string _lastLiveKey = "";

    /// <summary>The pid that produced the last live sample (0 = none).</summary>
    public int LiveRootPid => _liveRootPid;

    /// <summary>Consecutive polls the live root has been absent (diagnostics).</summary>
    public int ForeignStreak => _foreignStreak;

    /// <summary>True while an adopted target's sample is still held to the
    /// frozen-data guard (diagnostics: distinguishes settling from frozen-hold).</summary>
    public bool CheckingAdopted => _checkingAdopted;

    /// <summary>
    /// Before-polling decision for the current candidate set. Resets the
    /// settling streak when the live target is still present; adopts the new
    /// foreground's root once the settling window elapses.
    /// </summary>
    public TargetVerdict Decide(IReadOnlyList<int> candidates)
    {
        bool sameTarget = _liveRootPid != 0 && candidates.Contains(_liveRootPid);
        bool firstTarget = _liveRootPid == 0;
        if (sameTarget || firstTarget)
        {
            _foreignStreak = 0;
            return TargetVerdict.Poll;
        }

        if (++_foreignStreak < AdoptAfterPolls)
        {
            return TargetVerdict.TrackOnly;
        }

        // Adopt the new foreground's root: from here its tree is polled, but
        // its sample must first differ from the departed target's last values.
        _foreignStreak = 0;
        _liveRootPid = candidates[0];
        _checkingAdopted = true;
        _adoptedKey = _lastLiveKey;
        return TargetVerdict.Poll;
    }

    /// <summary>
    /// After-polling guard: true while the sample is still the departed
    /// target's frozen data (the adopted target has not presented yet) — the
    /// caller holds the zero state. Clears on the first differing sample.
    /// </summary>
    public bool IsFrozenSample(PresentMonDynamicSample sample)
    {
        if (!_checkingAdopted)
        {
            return false;
        }
        if (SampleKey(sample) == _adoptedKey)
        {
            return true;
        }
        _checkingAdopted = false;
        return false;
    }

    /// <summary>Records a live sample so the next poll knows the trusted
    /// target and the frozen-data key it must differ from.</summary>
    public void NoteLive(int processId, PresentMonDynamicSample sample)
    {
        _liveRootPid = processId;
        _lastLiveKey = SampleKey(sample);
    }

    /// <summary>The frozen-data guard key: presented + displayed FPS. The
    /// departed target's data comes back byte-stable for every pid, so an
    /// adopted target that still reads the departed target's values is
    /// detected by exact key equality.</summary>
    private static string SampleKey(PresentMonDynamicSample sample)
        => $"{sample.Fps:0.0}|{sample.DisplayedFps:0.0}";
}
