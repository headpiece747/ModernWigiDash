using System.Windows;
using AppClass = ModernWigiDash.App.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// Window chrome smoke coverage: applying the dark DWM title bar to a
/// constructed window must never throw (ThemeApplicatorTests' STA pattern).
/// An unshown window has no HWND, so the DWM attribute calls early-return; in
/// the test host the pack://application icon URI cannot resolve (the
/// Application's ResourceAssembly is the test host, which has no logo.ico) —
/// which exercises the catch: a missing icon resource must not crash window
/// setup.
/// </summary>
[TestClass]
public class WindowChromeTests
{
    /// <summary>The pack://application icon URI needs an Application host —
    /// the shared-App pattern from ThemeManagerTests (reuses an existing App
    /// so the process-wide Application singleton is created at most once).</summary>
    private static readonly Lazy<AppClass> SharedApp = new(() =>
        Application.Current as AppClass ?? StaRunner.Run(() => new AppClass()));

    [TestMethod]
    public void ApplyDarkTitleBar_ConstructedWindow_DoesNotThrow()
    {
        _ = SharedApp.Value;

        StaRunner.Run(() =>
        {
            var window = new Window { Title = "WindowChromeTests" };
            WindowChrome.ApplyDarkTitleBar(window, "#0F111A");
        });
    }
}
