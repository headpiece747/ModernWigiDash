using System.IO;
using ModernWigiDash.App.Power;

namespace ModernWigiDash.Tests;

[TestClass]
public class MainWindowConstructionTests
{
    /// <summary>
    /// A live STA thread that owns the process-wide Application and executes
    /// each window construction. WPF's StaticResource resolution silently skips
    /// Application resources loaded on a different thread, so the App (and its
    /// InitializeComponent) and the window must run on one thread.
    /// </summary>
    private static readonly StaHost Host = new("MainWindowTests-STA");

    [TestMethod]
    public void Construct_OnStaThread_SetsTitleAndDoesNotThrow()
    {
        var (title, error) = Host.Invoke(() =>
        {
            var window = new MainWindow(new StubPresentMonNative(), ProfilePersistence.DefaultProfilePath(), new NoopPowerModeSource(), new FakeTraySurface());
            string title = window.Title;
            try
            {
                window.Close();
            }
            catch (Exception)
            {
                // Close before Show is safe; if it throws, construction is what we verify.
            }
            return (object?)title;
        });

        Assert.IsNull(error, error?.ToString());
        Assert.AreEqual("ModernWigiDash", title);
    }

    [TestMethod]
    public void Construct_XamlInitOrder_DoesNotNre()
    {
        var (_, error) = Host.Invoke(() =>
        {
            var window = new MainWindow(new StubPresentMonNative(), ProfilePersistence.DefaultProfilePath(), new NoopPowerModeSource(), new FakeTraySurface());
            try
            {
                window.Close();
            }
            catch (Exception)
            {
                // Close before Show is safe; if it throws, construction is what we verify.
            }
            return null;
        });

        Assert.IsNull(error, error?.ToString());
    }

    /// <summary>
    /// Leaves the process without an Application so other test classes (whose
    /// SharedApp Lazy unconditionally calls new App()) can still create theirs.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        Host.DetachApplication();
    }
}
