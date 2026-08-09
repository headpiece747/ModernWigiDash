using System.Reflection;
using System.Threading;
using System.Windows;
using ModernWigiDash.App;
using ModernWigiDash.App.PresentMon;
using AppClass = ModernWigiDash.App.App;

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
    private static readonly StaHost Host = new();

    [TestMethod]
    public void Construct_OnStaThread_SetsTitleAndDoesNotThrow()
    {
        var (title, error) = Host.Invoke(() =>
        {
            var window = new MainWindow(new StubPresentMonNative());
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
            var window = new MainWindow(new StubPresentMonNative());
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
    /// The window's FRAMETIME PollLoop probes PresentMonNative on its first
    /// tick. A fake keeps the real PresentMonAPI2.dll (and its load-time side
    /// effects) entirely out of the test host.
    /// </summary>
    private sealed class StubPresentMonNative : IPresentMonNative
    {
        public bool IsAvailable => false;
        public string? UnavailableReason => "stub (test)";
        public bool OpenSession() => false;
        public void CloseSession() { }
        public bool TrackProcess(int processId) => false;
        public PresentMonPollResult PollDynamic(int processId) => new(null, PmStatus.Success);
        public IReadOnlyList<double> DrainFrameTimes(int processId) => [];
        public void Dispose() { }
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
    /// Owns the App on a dedicated STA thread. WPF object creation requires
    /// STA, Application.Current is process-wide (created once), and window
    /// BAML resource lookup only sees Application resources on its own thread —
    /// so every construction runs here, on the App's thread.
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
            _thread = new Thread(Run) { IsBackground = true, Name = "MainWindowTests-STA" };
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

        private void EnsureApp()
        {
            // `new App()` does not load App.xaml — the generated Main calls
            // InitializeComponent separately. Without it, Application resources
            // (e.g. the window's PrimaryFont StaticResource) are missing.
            var app = Application.Current as AppClass;
            if (app == null)
            {
                app = new AppClass();
            }
            app.Resources.Clear(); // a reused App (ThemeManagerTests) only holds theme keys
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

        public (object? Result, Exception? Error) Invoke(Func<object?> work)
        {
            lock (_gate)
            {
                _result = null;
                _workError = null;
                _done = false;
                _work = work;
                Monitor.PulseAll(_gate);
                while (!_done) Monitor.Wait(_gate);

                return (_result, _workError);
            }
        }
    }
}
