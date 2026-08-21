using ModernWigiDash.Sdk;
using System.IO;
using System.Windows;
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
            FileLog.Write("[App] === Application exiting ===");
            FileLog.Flush();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeSettings.Theme = ThemeSettings.Load();
        ThemeManager.ApplyToApplication();
        base.OnStartup(e);
    }
}
