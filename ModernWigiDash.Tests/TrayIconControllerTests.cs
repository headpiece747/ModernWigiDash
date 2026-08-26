namespace ModernWigiDash.Tests;

/// <summary>
/// The tray icon controller's policy pinned without a notification area:
/// the menu shape (the approved tray contract), the click/menu routing to
/// the show and quit delegates, the IsLive guard, the Start idempotence,
/// and the dispose contract — all driven through the ITrayIconSurface seam
/// with an in-memory fake (the production NotifyIconTraySurface is the thin
/// WinForms binding, the OS boundary).
/// </summary>
[TestClass]
public class TrayIconControllerTests
{
    [TestMethod]
    public void TrayMenu_Default_IsShowSeparatorQuit()
    {
        TrayMenu menu = TrayMenu.Default();

        Assert.AreEqual(3, menu.Items.Count);
        Assert.AreEqual("ModernWigiDash", menu.Items[0].Label);
        Assert.AreEqual(TrayMenuCommand.Show, menu.Items[0].Command);
        Assert.AreEqual(string.Empty, menu.Items[1].Label);
        Assert.AreEqual(TrayMenuCommand.Separator, menu.Items[1].Command);
        Assert.AreEqual("Quit", menu.Items[2].Label);
        Assert.AreEqual(TrayMenuCommand.Quit, menu.Items[2].Command);
    }

    [TestMethod]
    public void Start_SingleClick_RoutesToOnShow_NotOnQuit()
    {
        int shows = 0;
        int quits = 0;
        var fake = new FakeTraySurface();
        var controller = new TrayIconController(() => shows++, () => quits++, null, fake);
        controller.Start();

        fake.RaiseSingleClick();

        Assert.AreEqual(1, shows, "a single left click is the show affordance");
        Assert.AreEqual(0, quits);
    }

    [TestMethod]
    public void Start_MenuShow_RoutesToOnShow()
    {
        int shows = 0;
        int quits = 0;
        var fake = new FakeTraySurface();
        var controller = new TrayIconController(() => shows++, () => quits++, null, fake);
        controller.Start();

        fake.RaiseMenu(TrayMenuCommand.Show);

        Assert.AreEqual(1, shows, "the menu's show item routes to the show delegate");
        Assert.AreEqual(0, quits);
    }

    [TestMethod]
    public void Start_MenuQuit_RoutesToOnQuit_NotOnShow()
    {
        int shows = 0;
        int quits = 0;
        var fake = new FakeTraySurface();
        var controller = new TrayIconController(() => shows++, () => quits++, null, fake);
        controller.Start();

        fake.RaiseMenu(TrayMenuCommand.Quit);

        Assert.AreEqual(1, quits, "the menu's Quit item routes to the quit delegate");
        Assert.AreEqual(0, shows);
    }

    [TestMethod]
    public void Start_ShowsTheSurface_AndIsLive_TracksTheSurface()
    {
        var fake = new FakeTraySurface();
        var controller = new TrayIconController(() => { }, () => { }, null, fake);

        Assert.IsFalse(controller.IsLive, "before Start there is no surface - the N1 guard reads dead");

        controller.Start();

        Assert.AreEqual(1, fake.ShowCount, "Start shows the icon");
        Assert.IsTrue(controller.IsLive, "after Start the live state tracks the surface");
    }

    [TestMethod]
    public void Start_ASecondStart_IsANoOp()
    {
        var fake = new FakeTraySurface();
        var controller = new TrayIconController(() => { }, () => { }, null, fake);
        controller.Start();
        controller.Start();

        Assert.AreEqual(1, fake.ShowCount, "a second Start must not re-show the icon (re-wiring would double-fire the show on one click)");
    }

    [TestMethod]
    public void Dispose_HidesAndReleasesTheSurface_AndIsLiveFallsBack()
    {
        var fake = new FakeTraySurface();
        var controller = new TrayIconController(() => { }, () => { }, null, fake);
        controller.Start();
        Assert.IsTrue(controller.IsLive);

        controller.Dispose();

        Assert.IsTrue(fake.HideCalled, "dispose hides the icon before releasing it");
        Assert.IsTrue(fake.Disposed, "dispose releases the surface (the NotifyIcon owns the icon handle)");
        Assert.IsFalse(controller.IsLive, "after dispose the N1 guard reads dead - a close path must not trust a removed tray");
    }

    [TestMethod]
    public void Dispose_WithoutStart_IsANoOp()
    {
        var fake = new FakeTraySurface();
        var controller = new TrayIconController(() => { }, () => { }, null, fake);

        controller.Dispose();

        Assert.IsFalse(fake.HideCalled);
        Assert.IsFalse(fake.Disposed, "a never-started controller holds no surface to release");
    }

    /// <summary>
    /// The in-memory tray surface the controller's tests drive: records the
    /// show/hide/dispose calls and re-raises the seam's events on demand.
    /// </summary>
    private sealed class FakeTraySurface : ITrayIconSurface
    {
        public int ShowCount { get; private set; }
        public bool HideCalled { get; private set; }
        public bool Disposed { get; private set; }
        public bool IsLive { get; private set; }

        public event Action? SingleClicked;
        public event Action<TrayMenuCommand>? MenuSelected;

        public void Show()
        {
            ShowCount++;
            IsLive = true;
        }

        public void Hide()
        {
            HideCalled = true;
            IsLive = false;
        }

        public void RaiseSingleClick() => SingleClicked?.Invoke();

        public void RaiseMenu(TrayMenuCommand command) => MenuSelected?.Invoke(command);

        public void Dispose() => Disposed = true;
    }
}
