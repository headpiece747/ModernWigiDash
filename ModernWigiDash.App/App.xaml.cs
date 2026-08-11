using ModernWigiDash.Sdk;
using System.IO;
using System.Windows;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

    /// <summary>True once the window's close/teardown sequence begins. Only
    /// OperationCanceledExceptions raised while closing are benign (expected
    /// during teardown); an OCE at any other time propagates so a genuine
    /// cancellation crash stays visible. Set by MainWindow's Closed handler
    /// before any teardown dispose runs.</summary>
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
            // Only cancellation-type failures raised during the close/teardown
            // sequence are benign; everything else propagates so a real crash
            // is visible.
            bool benign = e.Exception is OperationCanceledException && IsClosing;
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
