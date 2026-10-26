using System.Windows;

namespace ModernWigiDash.App;

/// <summary>
/// The window-state probe facet of <see cref="WindowLifecycle"/>: the live
/// reads of the window's WPF state the intercept decisions consult (the
/// wiring flag, the resolved close behavior, the tray liveness, the
/// owner-enabled flag, visibility, and the window state). Each probe reads
/// at call time, so a provider bound late (the tray icon) is picked up
/// without a rewire.
/// </summary>
internal sealed record WindowStateProbes(
    Func<bool> Wired,
    Func<string?> CloseBehavior,
    Func<bool> TrayLive,
    Func<bool> IsEnabled,
    Func<bool> IsVisible,
    Func<WindowState> State);
