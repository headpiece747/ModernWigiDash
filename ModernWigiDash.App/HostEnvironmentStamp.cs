using System.Runtime.InteropServices;
using Microsoft.Win32;
using ModernWigiDash.App.Update;

namespace ModernWigiDash.App;

/// <summary>
/// The host stamp the startup log records: which Windows release and
/// cumulative update, which architecture, which runtime, which app build the
/// session ran on.
///
/// It exists because the app writes a rich, tagged log and still could not
/// answer the one question a field report always asks: "which Windows update
/// was this?" Before this, the OS version appeared NOWHERE in production code -
/// not in the log, not in the crash log - so an environment-specific report
/// (a Windows 11 update reported as "it stopped working", 2026-09-27) could
/// not be narrowed down from anything the app itself produced, and every such
/// report had to be resolved by asking the reporter instead of by reading a
/// file they already had.
///
/// <para>
/// WHY THE REGISTRY, AND WHY <c>win=</c> RATHER THAN <c>os=</c>: the obvious
/// sources do not answer the question. <c>Environment.OSVersion.Version</c>
/// reports <c>10.0.26200.0</c> on Windows 11 - the "10.0" is the NT kernel
/// version, and the Revision is a hardcoded 0, never the update revision.
/// <c>RuntimeInformation.OSDescription</c> reports the same
/// "Microsoft Windows 10.0.26200" string, which reads as Windows 10 to anyone
/// scanning a log. Neither carries the UBR, and the UBR is precisely what
/// distinguishes one cumulative update from the next: this machine is build
/// 26200 <b>9550</b>, while an earlier 26200 was a different update entirely.
/// The CurrentVersion key's <c>DisplayVersion</c> ("25H2") and
/// <c>CurrentBuild.UBR</c> ("26200.9550") are what actually name the update,
/// so the stamp carries those and nothing that pretends to be an OS name.
/// </para>
///
/// <para>
/// The shape is split so the format is testable with no OS and no window:
/// <see cref="Format"/> and <see cref="ComposeWindowsRelease"/> are pure and
/// take their values as arguments, and only <see cref="Describe"/> reads the
/// live machine. A dev build's app version is the literal <c>dev</c> rather
/// than an empty field, so a line never carries a blank where a reader expects
/// a value.
/// </para>
/// </summary>
internal static class HostEnvironmentStamp
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>The app version as the log spells it: the parsed release
    /// version, or <c>dev</c> for a build with no parseable stamp.</summary>
    public static string AppVersionText => AppVersion.Current?.ToString() ?? "dev";

    /// <summary>The live machine's identity, formatted. Every value but the
    /// Windows release comes from <see cref="RuntimeInformation"/>; the release
    /// is a best-effort registry read that degrades to <c>unknown</c> rather
    /// than throwing, and the caller still writes the whole line inside the
    /// startup log's own best-effort try.</summary>
    public static string Describe() => Format(
        ReadWindowsRelease(),
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        AppVersionText);

    /// <summary>The one spelling of the stamp. Key=value pairs, space separated,
    /// so a reader can find one field in a long line without parsing the rest,
    /// and so a new field is an addition rather than a reformat of every
    /// existing reader's expectations.</summary>
    internal static string Format(string windowsRelease, string osArchitecture, string framework, string appVersion)
        => $"win={Blank(windowsRelease)} arch={Blank(osArchitecture)} runtime={Blank(framework)} app={Blank(appVersion)}";

    /// <summary>The Windows release identity: the marketing release plus the
    /// full build-with-revision, e.g. <c>25H2 (26200.9550)</c>. Both halves are
    /// reported only when both are present, because a release name without the
    /// UBR does not identify a cumulative update - which is the whole reason
    /// this value exists.</summary>
    internal static string ComposeWindowsRelease(string? displayVersion, string? currentBuild, string? ubr)
    {
        if (string.IsNullOrWhiteSpace(currentBuild) || string.IsNullOrWhiteSpace(ubr)) return "unknown";
        string build = currentBuild.Trim() + "." + ubr.Trim();
        return string.IsNullOrWhiteSpace(displayVersion)
            ? build
            : displayVersion.Trim() + " (" + build + ")";
    }

    /// <summary>Reads the release identity from the CurrentVersion key. A
    /// missing key, a missing value, or a denied read all degrade to
    /// <c>unknown</c>: the stamp is diagnostics, and diagnostics must never be
    /// the thing that stops the app from starting.</summary>
    private static string ReadWindowsRelease()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            if (key is null) return "unknown";
            return ComposeWindowsRelease(
                ReadValue(key, "DisplayVersion"),
                ReadValue(key, "CurrentBuild"),
                ReadValue(key, "UBR"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            return "unknown";
        }
    }

    /// <summary>One registry value as text, whatever it is stored as. This
    /// exists because the three values do NOT share a type:
    /// <c>DisplayVersion</c> is a string, while <c>CurrentBuild</c> and
    /// <c>UBR</c> are DWORDs - so reading them with <c>as string</c> returns
    /// null for two of the three and the release field reads "unknown" on every
    /// machine. A live-read assertion in the test class is what caught that,
    /// which is the argument for having one.</summary>
    private static string? ReadValue(RegistryKey key, string name)
        => key.GetValue(name) switch
        {
            null => null,
            string text => text,
            var other => System.Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>An absent value reads as <c>unknown</c> rather than collapsing
    /// the key/value spacing - a blank would make <c>arch= runtime=</c> look
    /// like a parse that succeeded and lost a field.</summary>
    private static string Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
}
