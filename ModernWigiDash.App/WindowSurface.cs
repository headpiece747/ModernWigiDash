namespace ModernWigiDash.App;

/// <summary>
/// The window-surface facet of <see cref="WindowLifecycle"/>: the window
/// operations the module's rules execute (hide, minimize, show, activate,
/// force-Normal, close, shutdown). The window binds its own WPF members;
/// the module never sees a <c>Window</c>.
/// </summary>
internal sealed record WindowSurface(
    Action Hide,
    Action Minimize,
    Action Show,
    Action Activate,
    Action ForceNormal,
    Action Close,
    Action Shutdown);
