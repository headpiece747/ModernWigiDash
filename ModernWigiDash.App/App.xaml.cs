using ModernWigiDash.Sdk;
using System.IO;
using System.Windows;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

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
            LogCrash(e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeSettings.Theme = ThemeSettings.Load();
        ThemeManager.ApplyToApplication();
        base.OnStartup(e);
    }

    private static void LogCrash(Exception? ex)
    {
        string msg = $"[{TimeProvider.System.GetUtcNow().UtcDateTime:yyyy-MM-dd HH:mm:ss}] UNHANDLED EXCEPTION: {ex}{Environment.NewLine}";
        Console.WriteLine(msg);
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
