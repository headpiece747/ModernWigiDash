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
    public void Start_DeadSurface_LogsNotShown_NotIconShown()
    {
        var lines = new List<string>();
        var deadSurface = new FakeTraySurface(showBringsUp: false);
        var controller = new TrayIconController(() => { }, () => { }, new DiagLog("TRAY", 1, logFirst: true, write: lines.Add), deadSurface);

        controller.Start();

        Assert.AreEqual(1, lines.Count);
        StringAssert.Contains(lines[0], "NOT shown", "a Show that never brought the icon up must log the dead-tray verdict, not the shown line (the N1 guard reads the same live state the verdict derives from)");
    }

    [TestMethod]
    public void Start_LiveSurface_LogsIconShown()
    {
        var lines = new List<string>();
        var liveSurface = new FakeTraySurface();
        var controller = new TrayIconController(() => { }, () => { }, new DiagLog("TRAY", 1, logFirst: true, write: lines.Add), liveSurface);

        controller.Start();

        Assert.AreEqual("[TRAY] icon shown", lines[0], "a Show that brought the icon up logs the shown line (the honest verdict reads the surface's live state, so it cannot claim more than the N1 guard would trust)");
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
}
