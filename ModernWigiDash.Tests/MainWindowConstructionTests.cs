using ModernWigiDash.App.Power;
using ModernWigiDash.Hardware.Transport;

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
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), ProfilePersistence.DefaultProfilePath(), new NoopPowerModeSource(), new FakeTraySurface(), UsbEngine: FakeTransport.InertEngine()));
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
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), ProfilePersistence.DefaultProfilePath(), new NoopPowerModeSource(), new FakeTraySurface(), UsbEngine: FakeTransport.InertEngine()));
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
    /// The window's USB engine seam (the test host's window must never wake,
    /// init, or sleep the user's attached display): the injected engine is
    /// the one the window drives, so with no standby probe the session-end
    /// verdict comes back from the injected fake transport's canned answer
    /// (a real engine here would put the physical device to sleep).
    /// </summary>
    [TestMethod]
    public void Construct_WithInjectedEngine_StandbyRoutesThroughTheInjectedTransport()
    {
        var (confirmed, error) = Host.Invoke(() =>
        {
            var fake = new FakeTransport
            {
                ConnectResult = true,
                ConnectedAfterConnect = true,
                GoToStandbyResult = true
            };
            var window = new MainWindow(new MainWindowTestOptions(
                new StubPresentMonNative(),
                ProfilePersistence.DefaultProfilePath(),
                new NoopPowerModeSource(),
                new FakeTraySurface(),
                UsbEngine: new DisplayDeviceEngine(fake, ConnectionState.Connected)));
            try
            {
                return (object)window.RunSessionEndStandby();
            }
            finally
            {
                window.QuitClose();
            }
        });

        Assert.IsNull(error, error?.ToString());
        Assert.IsNotNull(confirmed, "the verdict must be a boxed bool");
        Assert.IsTrue((bool)confirmed, "the standby verdict rides back from the injected fake transport");
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
