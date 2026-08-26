using System.IO;
using System.Reflection;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App;

public partial class App : Application
{
    /// <summary>True once the window's close/teardown sequence begins. Teardown
    /// cancels in-flight work, so OperationCanceledExceptions raised while
    /// closing are expected; an OCE at any other time is benign only when its
    /// own token is cancelled (see <see cref="CrashSuppression"/>). Set by
    /// MainWindow's Closed handler before any teardown dispose runs.</summary>
    internal static volatile bool IsClosing;

    /// <summary>The single-instance guard (the primary owns the named
    /// handles; the kernel releases them on process death, so a force-killed
    /// instance can never wedge the next launch). Null on the secondary
    /// launch's early-exit path.</summary>
    private SingleInstanceGuard? _instanceGuard;

    /// <summary>
    /// The production constructor: pins the log paths next to the profile
    /// (LocalAppData, never next to the exe), arms the crash handlers, and
    /// binds the exit marker + final flush.
    /// </summary>
    public App()
    {
        // Logs live next to the profile, never next to the exe: a Program
        // Files install is read-only for standard users, and a single-file
        // host's BaseDirectory is the extraction dir under %TEMP%. Both paths
        // are pinned here before the first write below.
        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernWigiDash");
        try
        {
            Directory.CreateDirectory(appDataDir);
        }
        catch (IOException)
        {
            // Best-effort; the log write below surfaces the failure.
        }
        FileLog.LogPath = Path.Combine(appDataDir, "display_device.log");
        CrashLog.LogPath = Path.Combine(appDataDir, "crash.log");

        try
        {
            FileLog.Write($"[App] === Application starting === BaseDir={AppContext.BaseDirectory}");
        }
        catch (IOException)
        {
            // Startup log is best-effort; file may be locked. Surface to debug output.
            System.Diagnostics.Debug.WriteLine("Startup log write failed (file locked)");
        }

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            CrashLog.Append(e.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (s, e) =>
        {
            // Only cancellation-type failures are benign: an OCE whose token
            // is cancelled (the operation was cancelled by design, wherever
            // it lands) or any OCE raised during the close/teardown sequence.
            // Everything else propagates so a real crash is visible.
            bool benign = CrashSuppression.ShouldSuppress(e.Exception, IsClosing);
            CrashLog.Append(e.Exception, handled: benign);
            e.Handled = benign;
        };

        // The deterministic final flush (see FileLog.Flush): the cadence
        // flushes may leave the last lines — including the shutdown standby
        // verdict — buffered when the process exits. The exit marker doubles
        // as the "the app reached a clean exit" line for log analysis.
        Exit += (_, _) =>
        {
            _instanceGuard?.Dispose();
            WriteExitMarker();
        };
    }

    /// <summary>
    /// The exit ritual: the "[App] === Application exiting ===" marker plus
    /// the one final <see cref="FileLog.Flush"/> — the cadence flushes
    /// (8 KB / 250 ms) alone can leave the last lines buffered at process
    /// exit, so the flush is what lands them. Named so the marker + flush
    /// guarantee is assertable against a temp <c>FileLog.LogPath</c>.
    /// </summary>
    internal static void WriteExitMarker()
    {
        FileLog.Write("[App] === Application exiting ===");
        FileLog.Flush();
    }

    /// <summary>
    /// The single-instance guard runs before the window (the StartupUri) is
    /// created: a second launch signals the running instance to show itself
    /// and exits (a second engine would fight the same USB device). Then the
    /// persisted theme loads and applies before the first window shows.
    /// </summary>
    /// <param name="e">Startup event arguments.</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        // The guard is production-only: under a test host the entry assembly
        // is the test runner, and the guard's second-launch path (signal the
        // running instance, then Shutdown) would shut down the test's own
        // Application mid-invoke - a half-shut-down Application's static
        // state then makes the next test's App resource load FailFast. The
        // module's verdict + signal policy is pinned by
        // SingleInstanceGuardTests against the injected handle seam.
        if (IsProductionEntry)
        {
            _instanceGuard = new SingleInstanceGuard(ActivateMainWindow);
            if (!_instanceGuard.IsPrimary)
            {
                FileLog.Write("[App] Second launch: signaled the running instance, exiting");
                Shutdown();
                return;
            }
        }

        ThemeSettings.Theme = ThemeSettings.Load();
        ThemeManager.ApplyToApplication();
        base.OnStartup(e);
    }

    /// <summary>True when this App assembly is the process's entry assembly
    /// (the production WPF entry point). Under a test host the entry
    /// assembly is the test runner, so the single-instance guard - whose
    /// second-launch path calls the WPF Shutdown method - must not run.</summary>
    private static bool IsProductionEntry
        => Assembly.GetEntryAssembly() is { } entry
           && string.Equals(entry.FullName, typeof(App).Assembly.FullName, StringComparison.Ordinal);

    /// <summary>The primary's activation hop: the guard's signal arrives on
    /// a thread-pool thread, so the window work hops to the dispatcher
    /// (the window's <c>ShowFromTray</c> shows and activates it).</summary>
    private void ActivateMainWindow()
    {
        // Fire-and-forget by design: the hop targets the UI thread and the
        // callback cannot meaningfully be awaited from the thread-pool
        // caller (the dispatcher-hop discard shape, MainWindow's update
        // check precedent).
        _ = Dispatcher.InvokeAsync(() =>
        {
            foreach (Window window in Windows)
            {
                if (window is MainWindow main)
                {
                    main.ShowFromTray();
                    break;
                }
            }
        });
    }
}
