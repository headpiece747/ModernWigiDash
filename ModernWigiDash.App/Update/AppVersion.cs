using System.Reflection;

namespace ModernWigiDash.App.Update;

/// <summary>
/// The app's own version, read from the build-time informational stamp
/// (written by build-release.ps1 from the release tag). Dev builds embed
/// the 0.0.0 csproj default (with the SDK's git-sha suffix), which
/// <see cref="Parse"/> maps to null — so <see cref="IsDevBuild"/> disables the updater.
/// </summary>
public static class AppVersion
{
    /// <summary>Parsed major.minor version, or null for dev/unparseable builds.</summary>
    public static Version? Current { get; } = Parse(ReadInformationalVersion());

    /// <summary>True when the build carries no parseable release version (dev).</summary>
    public static bool IsDevBuild => Current is null;

    /// <summary>Parses "v0.5.0", "0.5.0", or "0.5.0-suffix" into a Version (suffix stripped);
    /// also strips "+metadata" (the SDK appends the git commit hash to
    /// InformationalVersion when building in a repo). Null for unparseable
    /// input and 0.0.0 dev builds.</summary>
    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim().TrimStart('v', 'V');
        int cut = trimmed.IndexOfAny(['-', '+']);
        if (cut >= 0) trimmed = trimmed[..cut];
        if (!Version.TryParse(trimmed, out var v)) return null;
        return v.Major == 0 && v.Minor == 0 && v.Build == 0 ? null : v;
    }

    private static string ReadInformationalVersion()
        => Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "";
}
