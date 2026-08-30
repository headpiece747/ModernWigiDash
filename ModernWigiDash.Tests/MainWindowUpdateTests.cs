using System.Windows;
using ModernWigiDash.App.Power;
using ModernWigiDash.App.Update;

namespace ModernWigiDash.Tests;

[TestClass]
public class MainWindowUpdateTests
{
    private static readonly StaHost Host = new("MainWindowUpdateTests-STA");

    [TestCleanup]
    public void Cleanup() => Host.DetachApplication();

    [TestMethod]
    public void Ctor_UpdateButton_ExistsAndHiddenByDefault()
    {
        Host.Run<object?>(() =>
        {
            var window = new MainWindow(new StubPresentMonNative(), ProfilePersistence.DefaultProfilePath(), new NoopPowerModeSource(), new FakeTraySurface(), null, null, null, null, FakeTransport.InertEngine());
            try
            {
                Assert.IsNotNull(window.UpdateButton, "the update button must exist in the header");
                Assert.AreEqual(Visibility.Collapsed, window.UpdateButton.Visibility,
                    "the update button must be hidden when no update is known");
                return null;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ApplyUpdateState_Available_ShowsButtonWithGriddyIcon()
    {
        Host.Run<object?>(() =>
        {
            var window = new MainWindow(new StubPresentMonNative(), ProfilePersistence.DefaultProfilePath(), new NoopPowerModeSource(), new FakeTraySurface(), null, null, null, null, FakeTransport.InertEngine());
            try
            {
                window.ApplyUpdateState(new UpdateUiState(UpdateState.Available, "Update v0.5.0 available"));
                Assert.AreEqual(Visibility.Visible, window.UpdateButton.Visibility);
                Assert.IsNotNull(window.UpdateIconPath?.Data, "the arrow-circle-down geometry must be set");
                return null;
            }
            finally
            {
                window.Close();
            }
        });
    }
}
