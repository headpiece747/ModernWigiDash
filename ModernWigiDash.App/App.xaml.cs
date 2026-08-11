using ModernWigiDash.Sdk;
using System.IO;
using System.Windows;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

    /// <summary>True once the window's close/teardown sequence begins. Teardown
    /// cancels in-flight work, so OperationCanceledExceptions raised while
    /// closing are expected; an OCE at any other time is benign only when its
    /// own token is cancelled (see <see cref="CrashSuppression"/>). Set by
    /// MainWindow's Closed handler before any teardown dispose runs.</summary>
    internal static volatile bool IsClosing;

    public App()
    {
        // Log startup so we know the app actually launched
        try
        {
            FileLog.Write($"[App] === Application starting === BaseDir={AppDomain.CurrentDomain.BaseDirectory}");
        }
        catch (IOException)
        {
            // Startup log is best-effort; file may be locked. Surface to debug output.
            System.Diagnostics.Debug.WriteLine("Startup log write failed (file locked)");
        }

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogCrash(e.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (s, e) =>
        {
            // Only cancellation-type failures are benign: an OCE whose token
            // is cancelled (the operation was cancelled by design, wherever
            // it lands) or any OCE raised during the close/teardown sequence.
            // Everything else propagates so a real crash is visible.
            bool benign = CrashSuppression.ShouldSuppress(e.Exception, IsClosing);
            LogCrash(e.Exception, handled: benign);
            e.Handled = benign;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeSettings.Theme = ThemeSettings.Load();
        ThemeManager.ApplyToApplication();
        base.OnStartup(e);
    }

    private static void LogCrash(Exception? ex, bool handled = false)
    {
        string kind = handled ? "HANDLED EXCEPTION" : "UNHANDLED EXCEPTION";
        string msg = $"[{TimeProvider.System.GetUtcNow().UtcDateTime:yyyy-MM-dd HH:mm:ss}] {kind}: {ex}{Environment.NewLine}";
        try
        {
            File.AppendAllText(CrashLogPath, msg);
        }
        catch (IOException)
        {
            // Crash log is best-effort; file may be locked. Surface to debug output.
            System.Diagnostics.Debug.WriteLine("Crash log write failed (file locked)");
        }
    }
}
