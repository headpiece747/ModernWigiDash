namespace ModernWigiDash.App;

/// <summary>
/// The launch-argument policy for the autostart path (ADR-0019): the one
/// recognized startup flag and its presence rule. <see cref="AutostartPolicy"/>
/// writes the Run entry's command line (the quoted exe path plus the flag);
/// this is the read side at process start, where <c>App.OnStartup</c> parses
/// the same spelling from the launch args, so the flag has one owner.
/// </summary>
internal static class StartupLaunchPolicy
{
    /// <summary>The autostart launch flag the Run entry appends to the exe
    /// path; under it the window opens minimized.</summary>
    public const string StartupMinimizedArg = "--startup";

    /// <summary>
    /// Whether the launch args request the minimized autostart shape.
    /// Case-insensitive: the flag is hand-typed (a launcher, an editor, a
    /// re-typed Run entry), and a typed <c>--STARTUP</c> must not silently
    /// launch full-size.
    /// </summary>
    public static bool RequestsMinimizedStart(IEnumerable<string>? args)
        => args is not null && args.Any(a => string.Equals(a, StartupMinimizedArg, StringComparison.OrdinalIgnoreCase));
}
