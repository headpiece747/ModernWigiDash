using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ModernWigiDash.Tests;

/// <summary>
/// The page-tabs strip module through its real Panel/ScrollViewer and the
/// switch/rename/delete seams: tab clicks route to the right index, the close
/// button obeys the delete rule, the wheel scrolls the strip inverted, and
/// ScrollToPage brings the tab into view. WPF objects need STA, so every test
/// runs on a throwaway STA thread via <see cref="StaRunner"/>.
/// </summary>
[TestClass]
public class PageTabsViewTests
{
    /// <summary>Window tests Show real windows; an earlier class whose window
    /// Close() shut down the process-wide Application leaves
    /// Application.IsShuttingDown set, which silently disables every later
    /// Show — reset the host's Application state before each test.</summary>
    [TestInitialize]
    public void ResetProcessApplicationState() => StaHost.ResetApplicationState();

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;
    private sealed record TabsHarness(
        StackPanel Panel,
        ScrollViewer Viewer,
        PageTabsView View,
        List<int> Switched,
        List<int> Renamed,
        List<int> Deleted);

    /// <summary>ScrollViewer whose OnMouseWheel does not mark the event
    /// handled: the base class swallows the wheel (skipping the strip's
    /// instance handler), which would starve the test of the strip's own
    /// inversion math.</summary>
    private sealed class WheelPassingScrollViewer : ScrollViewer
    {
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            // Intentionally no-op: the strip's handler must see the event.
        }
    }

    private static TabsHarness Create(ProfileLayout profile, StackPanel? panel = null, ScrollViewer? viewer = null)
    {
        panel ??= new StackPanel { Orientation = Orientation.Horizontal };
        viewer ??= new ScrollViewer();
        var switched = new List<int>();
        var renamed = new List<int>();
        var deleted = new List<int>();
        var resources = new Dictionary<object, object>
        {
            ["AccentButton"] = new Style(typeof(Button)),
            [typeof(Button)] = new Style(typeof(Button)),
            ["TextSecondary"] = new SolidColorBrush(Colors.Gray)
        };
        var view = new PageTabsView(panel, viewer, key => resources.GetValueOrDefault(key), switched.Add, renamed.Add, deleted.Add);
        view.Rebuild(profile);
        return new TabsHarness(panel, viewer, view, switched, renamed, deleted);
    }

    /// <summary>A profile wide enough to overflow the 400px window the wheel
    /// and scroll-into-view tests host in.</summary>
    private static ProfileLayout WideProfile()
    {
        var profile = new ProfileLayout();
        for (int i = 0; i < 30; i++)
        {
            ProfileOps.AddPage(profile, $"Page {i}");
        }
        profile.ActivePageIndex = 0;
        return profile;
    }

    /// <summary>Runs short nested message pumps (DispatcherTimer +
    /// DispatcherFrame) until <paramref name="condition"/> holds or five
    /// seconds elapse: the shown window's template is applied, its layout
    /// passes run with the tabs in place, and the ScrollViewer's queued scroll
    /// commands get applied at LayoutUpdated — the only way a ScrollViewer
    /// outside a running app becomes scrollable. Condition-based, so timing
    /// noise on a busy test host cannot starve it. Reusable: unlike
    /// Dispatcher.Run it never shuts the dispatcher down.</summary>
    private static void PumpUntil(Window window, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var pump = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(20)
            };
            pump.Tick += (_, _) =>
            {
                pump.Stop();
                frame.Continue = false;
            };
            pump.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    [TestMethod]
    public void Rebuild_TabButtonClick_FiresSwitchSeamWithTabIndex()
    {
        StaRunner.Run(() =>
        {
            var profile = new ProfileLayout();
            ProfileOps.AddPage(profile, "A");
            ProfileOps.AddPage(profile, "B");
            var h = Create(profile);

            var tabContainer = (Grid)h.Panel.Children[2];
            var pageButton = tabContainer.Children.OfType<Button>().First();
            pageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            CollectionAssert.AreEqual(new[] { 2 }, h.Switched);
        });
    }

    [TestMethod]
    public void Rebuild_RenameButtonClick_FiresRenameSeamWithTabIndex()
    {
        StaRunner.Run(() =>
        {
            var profile = new ProfileLayout();
            ProfileOps.AddPage(profile, "A");
            var h = Create(profile);

            var tabContainer = (Grid)h.Panel.Children[1];
            var renameButton = tabContainer.Children.OfType<Button>().Single(b => Equals(b.Content, "✏️"));
            renameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            CollectionAssert.AreEqual(new[] { 1 }, h.Renamed);
        });
    }

    [TestMethod]
    public void Rebuild_DeleteButtonClick_FiresDeleteSeamWithTabIndex()
    {
        StaRunner.Run(() =>
        {
            var profile = new ProfileLayout();
            ProfileOps.AddPage(profile, "A");
            var h = Create(profile);

            var tabContainer = (Grid)h.Panel.Children[1];
            var deleteButton = tabContainer.Children.OfType<Button>().Single(b => Equals(b.Content, "✕"));
            deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            CollectionAssert.AreEqual(new[] { 1 }, h.Deleted);
        });
    }

    [TestMethod]
    public void Rebuild_SinglePage_SuppressesDeleteButton()
    {
        StaRunner.Run(() =>
        {
            var profile = new ProfileLayout(); // one page — the last page is never deletable
            var h = Create(profile);

            var tabContainer = (Grid)h.Panel.Children[0];
            object[] contents = tabContainer.Children.OfType<Button>().Select(b => b.Content).ToArray();
            Assert.AreEqual(2, contents.Length, "a non-deletable tab shows the page and rename buttons only");
            Assert.IsFalse(contents.Contains("✕"), "the close button must not exist when deletion is not allowed");
        });
    }

    [TestMethod]
    public void Rebuild_TwoPages_ShowsDeleteButton()
    {
        StaRunner.Run(() =>
        {
            var profile = new ProfileLayout();
            ProfileOps.AddPage(profile, "A");
            var h = Create(profile);

            var tabContainer = (Grid)h.Panel.Children[1];
            Assert.IsTrue(tabContainer.Children.OfType<Button>().Any(b => Equals(b.Content, "✕")));
        });
    }

    [TestMethod]
    public void Wheel_PositiveDelta_ScrollsStripLeft_Inverted()
    {
        StaRunner.Run(() =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var viewer = new WheelPassingScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panel
            };
            var window = new Window { Content = viewer, Width = 400, Height = 120 };
            window.Show();
            try
            {
                Create(WideProfile(), panel, viewer);
                PumpUntil(window, () => viewer.ScrollableWidth > 0);

                viewer.ScrollToHorizontalOffset(300);
                PumpUntil(window, () => Near(viewer.HorizontalOffset, 300)); // commands apply at LayoutUpdated
                Assert.AreEqual(300, viewer.HorizontalOffset);

                viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.MouseWheelEvent });
                PumpUntil(window, () => Near(viewer.HorizontalOffset, 180));
                Assert.AreEqual(180, viewer.HorizontalOffset, "a positive wheel delta must scroll the strip left (inverted)");

                viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
                PumpUntil(window, () => Near(viewer.HorizontalOffset, 300));
                Assert.AreEqual(300, viewer.HorizontalOffset, "a negative wheel delta must scroll the strip right");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ScrollToPage_BringsDistantTabIntoView()
    {
        StaRunner.Run(() =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var viewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panel
            };
            var window = new Window { Content = viewer, Width = 400, Height = 120 };
            window.Show();
            try
            {
                var h = Create(WideProfile(), panel, viewer);
                PumpUntil(window, () => viewer.ScrollableWidth > 0);

                Assert.AreEqual(0, viewer.HorizontalOffset, "the active tab at index 0 sits at the left edge");

                h.View.ScrollToPage(29);
                PumpUntil(window, () => viewer.HorizontalOffset > 0);

                Assert.IsTrue(viewer.HorizontalOffset > 0, "a distant tab must be scrolled into view");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
