using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

/// <summary>
/// The profile-import adapter pinned at its own interface: the swap leg
/// (the ReplaceProfile one-swap-site + the RawWrite funnel ordering, the
/// error leg on a throwing setter), the theme-apply leg (Replace +
/// SaveCurrent + the session-only failure line, the no-dialog success), and
/// the close-behavior operand read. The dialog legs run on the shared STA
/// host over a real shown owner (the house DialogHost pattern); the pure
/// legs run on the one-shot apartment runner.
/// </summary>
[TestClass]
public class ProfileImportAdapterTests
{
    private static readonly StaHost Host = new("ProfileImportAdapterTests-STA");

    [TestCleanup]
    public void Cleanup()
    {
        Host.DetachApplication();
        ThemeStore.PersistedPath = ThemeSettings.DefaultPath();
    }

    [TestMethod]
    public void SwapProfile_Success_SetsTheReplacedProfileAndRunsTheFunnel()
    {
        StaRunner.Run(() =>
        {
            var imported = new ProfileLayout { ProfileName = "Imported" };
            ProfileLayout? set = null;
            int rawWrites = 0;
            var adapter = new ProfileImportAdapter(
                () => new ProfileLayout(),
                p => set = p,
                () => rawWrites++,
                () => throw new NotSupportedException("the success path never touches the dialog"),
                new ThemeApplicator(),
                new Window());

            bool ok = adapter.SwapProfile(imported);

            Assert.IsTrue(ok);
            Assert.AreEqual(1, rawWrites, "the RawWrite funnel runs after the swap");
            Assert.IsNotNull(set);
            Assert.AreEqual("Imported", set.ProfileName, "the setter receives the replaced profile");
        });
    }

    [TestMethod]
    public void SwapProfile_WhenTheSetterThrows_SurfacesTheErrorDialogAndReturnsFalse()
    {
        bool ok = Host.Run(() =>
        {
            var owner = new Window();
            WpfWindow.ShowOwner(owner);
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ClickButton(owner, "OK"));
            var adapter = new ProfileImportAdapter(
                () => new ProfileLayout(),
                _ => throw new InvalidOperationException("the setter fault"),
                () => { },
                () => new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { }),
                new ThemeApplicator(),
                owner);

            return adapter.SwapProfile(new ProfileLayout());
        });

        Assert.IsFalse(ok, "a throwing setter surfaces the dialog and vetoes the swap");
    }

    [TestMethod]
    public void LocalCloseBehavior_ReadsTheProfileOperand()
    {
        StaRunner.Run(() =>
        {
            var adapter = new ProfileImportAdapter(
                () => new ProfileLayout { CloseBehavior = "hideToTray" },
                _ => { },
                () => { },
                () => throw new NotSupportedException(),
                new ThemeApplicator(),
                new Window());

            Assert.AreEqual("hideToTray", adapter.LocalCloseBehavior);
        });
    }

    [TestMethod]
    public void ApplyTheme_SaveFailure_AppliesForTheSessionAndSurfacesTheLine()
    {
        Host.Run(() =>
        {
            var owner = new Window();
            WpfWindow.ShowOwner(owner);
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ClickButton(owner, "OK"));

            // A save path under an existing regular file: the directory
            // create fails, so SaveCurrent returns false and the
            // session-only line shows.
            string blocker = Path.GetTempFileName();
            ThemeStore.PersistedPath = Path.Combine(blocker, "app_theme.json");
            try
            {
                var adapter = new ProfileImportAdapter(
                    () => new ProfileLayout(),
                    _ => { },
                    () => { },
                    () => new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { }),
                    new ThemeApplicator(),
                    owner);

                adapter.ApplyTheme(new ThemeSettings { AccentRed = "#010203" });

                Assert.AreEqual("#010203", ThemeStore.Current.AccentRed,
                    "the theme applies for the session even when the save fails");
            }
            finally
            {
                File.Delete(blocker);
            }
            return true;
        });
    }

    [TestMethod]
    public void ApplyTheme_SaveSuccess_PersistsAndShowsNoDialog()
    {
        Host.Run(() =>
        {
            var owner = new Window();
            WpfWindow.ShowOwner(owner);

            string path = Path.Combine(Path.GetTempPath(), "ModernWigiDash.Tests", $"adapter-theme-{Guid.NewGuid():N}.json");
            ThemeStore.PersistedPath = path;

            var adapter = new ProfileImportAdapter(
                () => new ProfileLayout(),
                _ => { },
                () => { },
                () => new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { }),
                new ThemeApplicator(),
                owner);

            adapter.ApplyTheme(new ThemeSettings { AccentRed = "#040506" });

            Assert.IsTrue(File.Exists(path), "the theme persisted to the injected path");
            Assert.AreEqual(0, owner.OwnedWindows.Count, "a successful save shows no error dialog");
            return true;
        });
    }

    /// <summary>Raises the Click routed event on the owned dialog's button
    /// whose Content equals <paramref name="content"/> — the same path a
    /// physical click takes, so the wired handler runs (the house
    /// DialogHost pattern).</summary>
    private static void ClickButton(Window owner, string content)
    {
        var dialog = owner.OwnedWindows.OfType<Window>().FirstOrDefault()
            ?? throw new InvalidOperationException("no owned dialog is showing");
        var button = FindDescendant<Button>(dialog, b => Equals(b.Content, content))
            ?? throw new InvalidOperationException($"button '{content}' not found in the dialog");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && predicate(match))
            {
                return match;
            }
            if (FindDescendant(child, predicate) is T inner)
            {
                return inner;
            }
        }
        return null;
    }
}
