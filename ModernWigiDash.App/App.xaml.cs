using System;
using System.IO;
using System.Windows;

namespace ModernWigiDash.App;

public partial class App : Application
{
    private static readonly string CrashLogPath = @"c:\Users\tobia\.gemini\antigravity\scratch\ModernWigiDash\crash.log";

    public App()
    {
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

    private static void LogCrash(Exception? ex)
    {
        string msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED EXCEPTION: {ex}{Environment.NewLine}";
        Console.WriteLine(msg);
        try
        {
            File.AppendAllText(CrashLogPath, msg);
        }
        catch { }
    }
}
