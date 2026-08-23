namespace ModernWigiDash.App;

/// <summary>
/// The window's startup wiring (App): the ordered construction steps as
/// named items — one named artifact the constructor applies in order, the
/// <see cref="TeardownPlan"/> image for startup. The sequence is the
/// load-bearing knowledge (the host modules before the profile load — a
/// widget's InitializeAsync runs synchronously inside the load and calls
/// back into the context; the state resyncs before the wired arm, so their
/// XAML events stay guarded; the wired arm last, so the guarded handlers
/// arm only after every module exists) — pinned against this real list by
/// <c>StartupWiringTests</c>, the way <c>TeardownPlanTests</c> pins the
/// teardown sequence. The context's module-deref callbacks are null-tolerant
/// for the pre-module window (MainWindow.Context.cs), so a future reorder
/// degrades to a benign no-op instead of the historical startup NRE.
/// </summary>
internal sealed record StartupWiring(IReadOnlyList<WiringStep> OrderedSteps);

/// <summary>
/// One named wiring step: <see cref="Name"/> is the ordering fact the plan
/// pins (modules before profile load, resyncs before the wired arm, wired
/// last), <see cref="Run"/> is the action.
/// </summary>
internal sealed record WiringStep(string Name, Action Run);
