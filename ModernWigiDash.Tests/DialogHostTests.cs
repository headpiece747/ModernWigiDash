using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ModernWigiDash.App;
using AppClass = ModernWigiDash.App.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// STA-window tests for the themed host dialogs. DialogHost needs a Window
/// owner and a resource-lookup delegate; the owner window is created (never
/// shown) on the App's STA thread, and the OK/Cancel click is scheduled on the
/// owner's dispatcher so it runs inside the dialog's own modal pump. A live
/// Application is required because showing a dialog fires SourceInitialized →
/// WindowChrome.ApplyDarkTitleBar, which resolves the logo via a
/// pack://application URI.
/// </summary>
[TestClass]
public class DialogHostTests
{
    private static readonly StaHost Host = new();

    [TestMethod]
    public void Confirm_ClickOk_ReturnsTrue()
    {
        bool confirmed = Host.Invoke(() =>
        {
            var owner = new Window();
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            var host = new DialogHost(owner, _ => null, (_, _) => { });
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ClickOwnedButton(owner, "OK"));
            return host.Confirm("Title", "Message");
        });

        Assert.IsTrue(confirmed);
    }

    [TestMethod]
    public void Confirm_ClickCancel_ReturnsFalse()
    {
        bool confirmed = Host.Invoke(() =>
        {
            var owner = new Window();
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            var host = new DialogHost(owner, _ => null, (_, _) => { });
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ClickOwnedButton(owner, "Cancel"));
            return host.Confirm("Title", "Message");
        });

        Assert.IsFalse(confirmed);
    }

    [TestMethod]
    public void PromptForText_EnterTextAndOk_ReturnsEnteredText()
    {
        string? result = Host.Invoke(() =>
        {
            var owner = new Window();
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            var host = new DialogHost(owner, _ => null, (_, _) => { });
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
        string? result = Host.Invoke(() =>
        {
            var owner = new Window();
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            var host = new DialogHost(owner, _ => null, (_, _) => { });
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ClickOwnedButton(owner, "Cancel"));
            return host.PromptForText("Rename Page", "New name:", "initial");
        });

        Assert.IsNull(result);
    }

    [TestMethod]
    public void PromptForText_OkWithoutEditing_ReturnsInitialValue()
    {
        string? result = Host.Invoke(() =>
        {
            var owner = new Window();
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            var host = new DialogHost(owner, _ => null, (_, _) => { });
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
    /// Owns the App on a dedicated STA thread (the same shape as
    /// MainWindowConstructionTests.StaHost): WPF object creation requires STA,
    /// Application.Current is process-wide (created once), and the dialog's
    /// pack://application icon URI only resolves while an Application lives on
    /// this thread.
    /// </summary>
    private sealed class StaHost
    {
        private readonly object _gate = new();
        private readonly Thread _thread;
        private Func<object?>? _work;
        private object? _result;
        private Exception? _workError;
        private bool _done;

        public StaHost()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "DialogHostTests-STA" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Run()
        {
            while (true)
            {
                lock (_gate)
                {
                    // STA pump: no exit — the loop dies with the test process.
                    // (S2190 intentionally suppressed: this is a message-pump loop,
                    // not recursion.)
#pragma warning disable S2190
                    while (_work == null) Monitor.Wait(_gate);
#pragma warning restore S2190
                    var work = _work ?? throw new InvalidOperationException("work was signaled without a delegate");
                    _work = null;
                    try
                    {
                        EnsureApp(); // Cleanup detaches the App between tests — recreate if needed
                        _result = work();
                        _workError = null;
                    }
                    catch (Exception ex)
                    {
                        _workError = ex;
                    }
                    _done = true;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        private static void EnsureApp()
        {
            // `new App()` does not load App.xaml — the generated Main calls
            // InitializeComponent separately. Without it, Application resources
            // and the pack://application icon URI are missing.
            var app = Application.Current as AppClass;
            if (app == null)
            {
                app = new AppClass();
            }
            app.Resources.Clear(); // a reused App only holds theme keys
            app.InitializeComponent();
        }

        /// <summary>
        /// Nulls the private Application._appInstance / _appCreatedInThisAppDomain
        /// fields so a later class can create its own Application instance.
        /// </summary>
        public void DetachApplication()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            FieldInfo appInstance = typeof(Application).GetField("_appInstance", flags)
                ?? throw new InvalidOperationException("Application._appInstance field not found");
            FieldInfo createdHere = typeof(Application).GetField("_appCreatedInThisAppDomain", flags)
                ?? throw new InvalidOperationException("Application._appCreatedInThisAppDomain field not found");
            appInstance.SetValue(null, null);
            createdHere.SetValue(null, false);
        }

        public T Invoke<T>(Func<T> work)
        {
            lock (_gate)
            {
                _result = null;
                _workError = null;
                _done = false;
                _work = () => work();
                Monitor.PulseAll(_gate);
                while (!_done) Monitor.Wait(_gate);

                if (_workError != null)
                {
                    Assert.Fail($"STA work failed: {_workError}");
                }
                return (T)_result!;
            }
        }
    }

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
