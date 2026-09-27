using System.Windows;
using ModernWigiDash.App.Power;
using ModernWigiDash.App.Update;

namespace ModernWigiDash.Tests;

/// <summary>
/// The update button pinned at the window seam: the XAML element exists in the
/// header, it starts hidden, and applying an <see cref="UpdateUiState"/> puts
/// the state's visibility and the state's icon on the REAL button.
///
/// The flow that decides the state (<see cref="UpdateFlow"/>, 13 tests) and the
/// model that decides the presentation (<c>UpdateBadgeModel</c>, 4 tests) are
/// both module-tested with no window, so what is left here is the binding and
/// nothing else. That binding is one element's whole visible lifecycle, which
/// is one window: the two former tests built two windows to observe the same
/// button before and after a state was applied (the architecture review's
/// "wire nothing" seam candidate, 2026-09-27).
/// </summary>
[TestClass]
public class MainWindowUpdateTests
{
    private static readonly StaHost Host = new("MainWindowUpdateTests-STA");

    [TestCleanup]
    public void Cleanup() => Host.DetachApplication();

    [TestMethod]
    public void TheButtonStartsHidden_AndTheAvailableStateShowsItWithItsIcon()
    {
        Host.Run<object?>(() =>
        {
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), ProfilePersistence.DefaultProfilePath(), new NoopPowerModeSource(), new FakeTraySurface(), UsbEngine: FakeTransport.InertEngine()));
            try
            {
                Assert.IsNotNull(window.UpdateButton, "the update button must exist in the header");
                Assert.AreEqual(Visibility.Collapsed, window.UpdateButton.Visibility,
                    "the update button must be hidden when no update is known");

                window.ApplyUpdateState(new UpdateUiState(UpdateState.Available, "Update v0.5.0 available"));

                Assert.AreEqual(Visibility.Visible, window.UpdateButton.Visibility,
                    "applying the Available state must reveal the real button, not just record the state");
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
