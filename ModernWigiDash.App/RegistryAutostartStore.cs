using Microsoft.Win32;

namespace ModernWigiDash.App;

/// <summary>
/// The production <see cref="IAutostartStore"/> (ADR-0019): the app's Run
/// entry under HKCU, the vendor Manager's verified shape. Each operation
/// opens the subkey fresh and disposes it on the way out; a missing value
/// reads as null, and <see cref="SetCommandLine"/> with null deletes the
/// value (deleting a missing value is a no-op). The root is injectable so the
/// round-trip test runs against a temp HKCU subkey instead of the machine's
/// real autostart entries (the <c>TwitchTokenStore</c> real-DPAPI precedent:
/// the adapter is pinned through its real API).
/// </summary>
internal sealed class RegistryAutostartStore : IAutostartStore
{
    private readonly RegistryKey _root;

    /// <summary>Production entry point: HKCU.</summary>
    public RegistryAutostartStore()
        : this(Registry.CurrentUser)
    {
    }

    /// <summary>Test seam: the root hive the Run subkey path resolves under
    /// (production passes the current-user hive).</summary>
    internal RegistryAutostartStore(RegistryKey root) => _root = root;

    public string? TryGetCommandLine()
    {
        using RegistryKey? subKey = _root.OpenSubKey(AutostartPolicy.RunSubKeyPath);
        return subKey?.GetValue(AutostartPolicy.RunValueName) as string;
    }

    public void SetCommandLine(string? commandLine)
    {
        using RegistryKey subKey = _root.CreateSubKey(AutostartPolicy.RunSubKeyPath)
            ?? throw new InvalidOperationException($"Could not open the autostart subkey ({AutostartPolicy.RunSubKeyPath}).");
        if (commandLine is null)
        {
            subKey.DeleteValue(AutostartPolicy.RunValueName, throwOnMissingValue: false);
        }
        else
        {
            subKey.SetValue(AutostartPolicy.RunValueName, commandLine, RegistryValueKind.String);
        }
    }
}
