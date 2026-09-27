using System.Reflection;
using System.Runtime.InteropServices;
using ModernWigiDash.App.Update;

namespace ModernWigiDash.App;

/// <summary>
/// The host stamp the startup log records: which OS build, which architecture,
/// which runtime, which app build the session ran on.
///
/// It exists because the app writes a rich, tagged log and still could not
/// answer the one question a field report always asks: "which Windows update
/// was this?" Before this, the OS build appeared NOWHERE in production code -
/// not in the log, not in the crash log - so an environment-specific report
/// (a Windows 11 update reported as "it stopped working", 2026-09-27) could
/// not be narrowed down from anything the app itself produced, and every such
/// report had to be resolved by asking the reporter instead of by reading a
/// file they already had.
///
/// The shape is split so the format is testable with no OS and no window:
/// <see cref="Format"/> is pure and takes its four values as arguments, and
/// only <see cref="Describe"/> reads the live process. A dev build's app
/// version is the literal <c>dev</c> rather than an empty field, so a line
/// never carries a blank where a reader expects a value.
/// </summary>
internal static class HostEnvironmentStamp
{
    /// <summary>The app version as the log spells it: the parsed release
    /// version, or <c>dev</c> for a build with no parseable stamp.</summary>
    public static string AppVersionText => AppVersion.Current?.ToString() ?? "dev";

    /// <summary>The live process's identity, formatted. Every value comes from
    /// <see cref="RuntimeInformation"/> except the app version; nothing here
    /// can throw, and the caller still writes it inside the startup log's
    /// best-effort try.</summary>
    public static string Describe() => Format(
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        AppVersionText);

    /// <summary>The one spelling of the stamp. Key=value pairs, space separated,
    /// so a reader can find one field in a long line without parsing the rest,
    /// and so a new field is an addition rather than a reformat of every
    /// existing reader's expectations.</summary>
    internal static string Format(string osDescription, string osArchitecture, string framework, string appVersion)
        => $"os={Blank(osDescription)} arch={Blank(osArchitecture)} runtime={Blank(framework)} app={Blank(appVersion)}";

    /// <summary>An absent value reads as <c>unknown</c> rather than collapsing
    /// the key/value spacing - a blank would make <c>arch= runtime=</c> look
    /// like a parse that succeeded and lost a field.</summary>
    private static string Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
}
