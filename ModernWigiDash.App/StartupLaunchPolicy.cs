using System.Runtime.InteropServices;

namespace ModernWigiDash.App;

/// <summary>
/// The launch-argument and environment policy for the autostart and minimized path (ADR-0019):
/// the recognized startup flags, Windows STARTUPINFO checks, and presence rules.
/// <see cref="AutostartPolicy"/> writes the Run entry's command line (the quoted exe
/// path plus <see cref="StartupMinimizedArg"/>); this is the read side at process start,
/// where <c>App.OnStartup</c> parses the recognized spellings from the launch args and
/// checks whether Windows requested a minimized start, so the policy has one owner.
/// </summary>
internal static class StartupLaunchPolicy
{
    /// <summary>The autostart launch flag the Run entry appends to the exe
    /// path; under it the window opens minimized.</summary>
    public const string StartupMinimizedArg = "--startup";

    /// <summary>
    /// Recognized startup flags that request a minimized start.
    /// Case-insensitive: flags can be hand-typed (a launcher, a shortcut, an editor,
    /// a re-typed Run entry), and a typed <c>--STARTUP</c>, <c>--minimized</c>, or
    /// <c>-m</c> must not silently launch full-size.
    /// </summary>
    private static readonly HashSet<string> MinimizedArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        StartupMinimizedArg,
        "-startup",
        "/startup",
        "--minimized",
        "-minimized",
        "/minimized",
        "--minimize",
        "-minimize",
        "/minimize",
        "-m",
        "/m",
        "--tray",
        "-tray",
        "/tray",
        "--hidden",
        "-hidden",
        "/hidden",
    };

    /// <summary>
    /// Whether the launch args request the minimized autostart shape.
    /// Case-insensitive: recognized flags like <c>--startup</c>, <c>--minimized</c>,
    /// or <c>-m</c> launch minimized.
    /// </summary>
    public static bool RequestsMinimizedStart(IEnumerable<string>? args)
        => args is not null && args.Any(a => MinimizedArgs.Contains(a));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetStartupInfoW", CharSet = CharSet.Unicode)]
    private static extern void GetStartupInfo(out StartupInfo lpStartupInfo);

    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_SHOWMINIMIZED = 2;
    private const short SW_SHOWMINNOACTIVE = 7;
    private const short SW_MINIMIZE = 6;

    /// <summary>
    /// Checks whether Windows STARTUPINFO requested starting minimized (e.g. from a Windows
    /// shortcut with Run set to Minimized, or a parent process launching with SW_SHOWMINIMIZED).
    /// </summary>
    public static bool WindowStartupInfoRequestsMinimized()
    {
        try
        {
            GetStartupInfo(out var si);
            return (si.dwFlags & STARTF_USESHOWWINDOW) != 0 &&
                   si.wShowWindow is SW_SHOWMINIMIZED or SW_SHOWMINNOACTIVE or SW_MINIMIZE;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the environment (command-line arguments or Windows STARTUPINFO) requests
    /// starting the application in a minimized state.
    /// </summary>
    public static bool RequestsMinimizedFromEnvironment(IEnumerable<string>? args)
        => RequestsMinimizedStart(args) || WindowStartupInfoRequestsMinimized();
}
