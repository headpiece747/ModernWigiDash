namespace ModernWigiDash.App;

/// <summary>
/// The process-lifecycle state owner (candidate 4 of the architecture review):
/// one module that owns the two process-level flags that used to leak through
/// mutable statics on <c>App</c> (<see cref="IsClosing"/> and
/// <see cref="StartMinimized"/>). Before this module, a background reader could
/// observe a half-updated flag and nothing structural prevented a writer from
/// skipping the read; now the value has one owner with a single test surface.
/// The flags are still process-wide (one per process), but they live behind a
/// named module instead of scattered statics, so the close/teardown latch and
/// the autostart-minimized start flag have one owner each.
/// </summary>
internal static class ProcessLifecycle
{
    /// <summary>True once the window's close/teardown sequence begins. Teardown
    /// cancels in-flight work, so OperationCanceledExceptions raised while
    /// closing are expected; an OCE at any other time is benign only when its
    /// own token is cancelled (see <see cref="CrashSuppression"/>). Set by
    /// MainWindow's Closed handler before any teardown dispose runs.</summary>
    internal static volatile bool IsClosing;

    /// <summary>The autostart-minimized start flag (ADR-0019): set from the
    /// launch args (<see cref="StartupLaunchPolicy.StartupMinimizedArg"/>, the
    /// flag the HKCU Run entry appends to the exe path) before the StartupUri
    /// window is constructed, and read once by MainWindow's ctor, which opens
    /// the window minimized under it. Under a test host the flag is set
    /// directly (the host's launch args never carry it).</summary>
    internal static volatile bool StartMinimized;
}
