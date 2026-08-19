namespace ModernWigiDash.Widgets;

/// <summary>
/// The host seam the weather fetch flow carries its host concerns across.
/// The flow owns the SEQUENCE (ADR-0007); the host owns the state — the
/// display state and its gate, the identity's write-back, the context. One
/// named seam replaces the former bag of anonymous delegate parameters, so
/// the gate discipline (the apply and the version read run under the host's
/// gate; the write-back guard's check + set is one critical section) is
/// declared once here at the seam instead of re-spelled in comments at every
/// wiring site.
/// <para>
/// The widget implements the seam directly — the host concerns are the
/// widget's own. The flow's tests carry an adapter over the same seam, so
/// the mirror host's fidelity is the adapter's discipline, not a copy of the
/// wiring.
/// </para>
/// </summary>
internal interface IWeatherFetchHost
{
    /// <summary>The property → identity-input coercion (the host's location
    /// properties, shaped into the fetch's input).</summary>
    WeatherLocation CurrentLocation { get; }

    /// <summary>
    /// Applies the fetched/cached snapshot to the display state under the
    /// host's gate: the version-then-identity guard first, then the merge
    /// and the resolved-identity copies as one atomic step. Returns whether
    /// the snapshot was applied.
    /// </summary>
    bool TryApply(WeatherApplyRequest request);

    /// <summary>The display state's data version (read under the host's gate
    /// — that is where the fetch thread writes it).</summary>
    int DataVersion { get; }

    /// <summary>The widget's Static Snapshot property (the cadence gate's
    /// veto input).</summary>
    bool IsStaticSnapshot { get; }

    /// <summary>The fetch cancellation token (teardown).</summary>
    CancellationToken RunToken { get; }

    /// <summary>
    /// Queues a resolved-label write-back for the UI thread, only when the
    /// identity guard still passes — the check + set under the host's gate
    /// are one critical section.
    /// </summary>
    void QueueLabelWriteback(Func<bool> identityGuard, string value);

    /// <summary>Requests a canvas repaint.</summary>
    void RequestRender();

    /// <summary>Requests an inspector refresh (the Location Match candidates
    /// changed).</summary>
    void RequestInspectorRefresh();
}