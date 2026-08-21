using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ModernWigiDash.App.Theming;

namespace ModernWigiDash.Tests;

/// <summary>
/// STA-window tests for the themed host dialogs. DialogHost needs a Window
/// owner and a resource-lookup delegate; the owner window is created and
/// shown on the App's STA thread (WPF requires a shown owner —
/// WpfWindow.ShowOwner), and the OK/Cancel click is scheduled on the
/// owner's dispatcher so it runs inside the dialog's own modal pump. A live
/// Application is required because showing a dialog fires SourceInitialized →
/// WindowChrome.ApplyDarkTitleBar, which resolves the logo via a
/// pack://application URI.
/// </summary>
[TestClass]
public class DialogHostTests
{
    private static readonly StaHost Host = new("DialogHostTests-STA");

    [TestMethod]
    public void Confirm_ClickOk_ReturnsTrue()
    {
        bool confirmed = Host.Run(() =>
        {
            var owner = new Window();
            WpfWindow.ShowOwner(owner);
            var host = new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { });
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ClickOwnedButton(owner, "OK"));
            return host.Confirm("Title", "Message");
        });

        Assert.IsTrue(confirmed);
    }

    [TestMethod]
    public void Confirm_ClickCancel_ReturnsFalse()
    {
        bool confirmed = Host.Run(() =>
        {
            var owner = new Window();
            WpfWindow.ShowOwner(owner);
            var host = new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { });
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ClickOwnedButton(owner, "Cancel"));
            return host.Confirm("Title", "Message");
        });

        Assert.IsFalse(confirmed);
    }

    [TestMethod]
    public void PromptForText_EnterTextAndOk_ReturnsEnteredText()
    {
        string? result = Host.Run(() =>
        {
            var owner = new Window();
            WpfWindow.ShowOwner(owner);
            var host = new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { });
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                var dialog = RequireOwnedDialog(owner);
                var box = FindDescendant<TextBox>(dialog, _ => true)
                    ?? throw new InvalidOperationException("prompt TextBox not found in the dialog");
                box.Text = "Round-trip value";
                ClickOwnedButton(owner, "OK");
            });
            return host.PromptForText("Rename Page", "New name:", "initial");
        });

        Assert.AreEqual("Round-trip value", result);
    }

    [TestMethod]
    public void PromptForText_ClickCancel_ReturnsNull()
    {
        string? result = Host.Run(() =>
        {
            var owner = new Window();
            WpfWindow.ShowOwner(owner);
            var host = new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { });
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ClickOwnedButton(owner, "Cancel"));
            return host.PromptForText("Rename Page", "New name:", "initial");
        });

        Assert.IsNull(result);
    }

    [TestMethod]
    public void PromptForText_OkWithoutEditing_ReturnsInitialValue()
    {
        string? result = Host.Run(() =>
        {
            var owner = new Window();
            WpfWindow.ShowOwner(owner);
            var host = new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { });
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ClickOwnedButton(owner, "OK"));
            return host.PromptForText("Rename Page", "New name:", "kept value");
        });

        Assert.AreEqual("kept value", result);
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

    /// <summary>
    /// The shared TestDoubles.StaHost owns the App on a dedicated STA thread:
    /// WPF object creation requires STA, Application.Current is process-wide
    /// (created once), and the dialog's pack://application icon URI only
    /// resolves while an Application lives on this thread.
    /// </summary>
    private static Window RequireOwnedDialog(Window owner)
        => owner.OwnedWindows.OfType<Window>().FirstOrDefault()
           ?? throw new InvalidOperationException("no owned dialog is showing");

    /// <summary>Raises the Click routed event on the dialog's button whose
    /// Content equals <paramref name="content"/> — the same path a physical
    /// click takes, so the wired handler runs.</summary>
    private static void ClickOwnedButton(Window owner, string content)
    {
        var dialog = RequireOwnedDialog(owner);
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
