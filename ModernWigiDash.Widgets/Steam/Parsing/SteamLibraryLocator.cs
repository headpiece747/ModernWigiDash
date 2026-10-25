using Microsoft.Win32;
using ModernWigiDash.Widgets.Steam.Models;

namespace ModernWigiDash.Widgets.Steam.Parsing;

/// <summary>
/// The Steam library discovery owner: the registry anchors that locate the
/// Steam installation, the libraryfolders.vdf multi-volume parse, and the
/// appmanifest (ACF) enumeration + parse. Every entry degrades gracefully —
/// a missing registry key, a missing file, or a corrupt ACF yields an
/// empty result (plus one log line where a fault is worth observing),
/// never a throw into the caller.
/// </summary>
internal static class SteamLibraryLocator
{
    /// <summary>
    /// The registry anchors, in order: the 32-bit Steam's per-user SteamPath
    /// (HKCU), then the machine-wide InstallPath under the 32-bit redirect
    /// node (WOW6432Node), then the native 64-bit node. The first anchor that
    /// yields an existing directory wins; null when none do (Steam not
    /// installed).
    /// </summary>
    public static string? TryFindSteamPath()
    {
        try
        {
            // The anchor ladder, explicit steps (no loop: the first existing
            // directory wins, and the three anchors are distinct facts).
            string? path = RegistryValue(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            if (path is { } p && Directory.Exists(p))
                return p;

            path = RegistryValue(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            if (path is { } w && Directory.Exists(w))
                return w;

            path = RegistryValue(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            if (path is { } n && Directory.Exists(n))
                return n;

            return null;
        }
        catch (Exception)
        {
            // The registry surface is unavailable (a non-Windows host, a
            // blocked hive): no Steam path, never a throw.
            return null;
        }
    }

    /// <summary>
    /// Resolves the library roots for a Steam installation: every mounted
    /// volume from <c>{steamPath}\steamapps\libraryfolders.vdf</c>. A
    /// missing or unreadable VDF falls back to the single-root shape
    /// ([steamPath]).
    /// </summary>
    public static IReadOnlyList<string> GetLibraryRoots(string steamPath)
    {
        string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        try
        {
            if (!File.Exists(vdfPath))
                return [steamPath];

            string text = File.ReadAllText(vdfPath);
            List<string> roots = [];
            int index = 0;
            while (SteamVdfTokenizer.TryFindValue(text.AsSpan(), "path", index, out int start, out int length))
            {
                string path = SteamVdfTokenizer.Unescape(text.AsSpan()[start..(start + length)]);
                if (path.Length > 0)
                    roots.Add(path);
                index = start + length + 2; // past the quoted value.
            }

            return roots.Count > 0 ? roots.Distinct().ToList() : [steamPath];
        }
        catch (Exception)
        {
            // Unreadable VDF: the single-root fallback keeps the library
            // discoverable from the main volume.
            return [steamPath];
        }
    }

    /// <summary>
    /// Enumerates and parses every appmanifest under the library roots'
    /// <c>steamapps</c> folders. A corrupt or truncated ACF is skipped with
    /// one log line (the untrusted-input rule: a hostile or broken file is
    /// observable, not silent); the parse itself never throws. Results are
    /// deduped by app id and sorted by name for stable display order.
    /// </summary>
    public static IReadOnlyList<SteamAppManifest> LoadManifests(IReadOnlyList<string> libraryRoots, Action<string>? log = null)
    {
        var manifests = new List<SteamAppManifest>();
        var seen = new HashSet<uint>();
        foreach (string root in libraryRoots)
        {
            string steamApps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(steamApps))
                continue;

            string[] files;
            try
            {
                files = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").ToArray();
            }
            catch (Exception ex)
            {
                log?.Invoke($"Steam: library volume unreadable ({steamApps}): {ex.Message}");
                continue;
            }

            foreach (string file in files)
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Steam: ACF unreadable ({file}): {ex.Message}");
                    continue;
                }

                if (!TryParseManifest(text, file, out SteamAppManifest? manifest))
                {
                    log?.Invoke($"Steam: ACF unparseable, skipped ({file})");
                    continue;
                }

                if (manifest is not null && seen.Add(manifest.AppId))
                    manifests.Add(manifest);
            }
        }

        manifests.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return manifests;
    }

    /// <summary>
    /// Parses one ACF file's content into a manifest. The app id comes from
    /// the <c>appid</c> key, falling back to the file name
    /// (<c>appmanifest_{id}.acf</c> — the canonical Steam shape); a file with
    /// no app id at all yields false. Missing optional fields read as their
    /// defaults (flags 0, counters 0, no name).
    /// </summary>
    internal static bool TryParseManifest(string text, string fileName, out SteamAppManifest? manifest)
    {
        manifest = null;
        ReadOnlySpan<char> span = text.AsSpan();

        uint appId = AppIdFromFileName(fileName);
        if (appId == 0
            && SteamVdfTokenizer.TryFindValue(span, "appid", 0, out int appIdStart, out int appIdLength)
            && SteamVdfTokenizer.TryParseUInt32(span[appIdStart..(appIdStart + appIdLength)], out uint keyAppId))
        {
            appId = keyAppId;
        }

        if (appId == 0)
            return false;

        string name = ReadStringField(span, "name", appId.ToString());
        string installDir = ReadStringField(span, "installdir", "");
        string executable = ReadStringField(span, "executable", "");
        SteamAppState flags = ReadUIntField(span, "StateFlags");
        long bytesDownloaded = ReadInt64Field(span, "BytesDownloaded");
        long bytesToDownload = ReadInt64Field(span, "BytesToDownload");
        long lastPlayed = ReadInt64Field(span, "LastPlayed");
        double hours = 0;
        if (SteamVdfTokenizer.TryFindValue(span, "hours_on_record", 0, out int hoursStart, out int hoursLength)
            && SteamVdfTokenizer.TryParseDouble(span[hoursStart..(hoursStart + hoursLength)], out double parsedHours))
        {
            hours = parsedHours;
        }

        manifest = new SteamAppManifest(appId, name, installDir, executable, flags, bytesDownloaded, bytesToDownload, lastPlayed, hours);
        return true;
    }

    private static string ReadStringField(ReadOnlySpan<char> span, string key, string fallback)
    {
        if (!SteamVdfTokenizer.TryFindValue(span, key, 0, out int start, out int length))
            return fallback;
        return SteamVdfTokenizer.Unescape(span[start..(start + length)]);
    }

    private static SteamAppState ReadUIntField(ReadOnlySpan<char> span, string key)
    {
        if (SteamVdfTokenizer.TryFindValue(span, key, 0, out int start, out int length)
            && SteamVdfTokenizer.TryParseUInt32(span[start..(start + length)], out uint value))
        {
            return (SteamAppState)value;
        }
        return SteamAppState.None;
    }

    private static long ReadInt64Field(ReadOnlySpan<char> span, string key)
    {
        if (SteamVdfTokenizer.TryFindValue(span, key, 0, out int start, out int length)
            && SteamVdfTokenizer.TryParseInt64(span[start..(start + length)], out long value))
        {
            return value;
        }
        return 0;
    }

    /// <summary>
    /// The app id from the canonical ACF file name
    /// (<c>appmanifest_{id}.acf</c>), 0 when the name does not carry one.
    /// </summary>
    private static uint AppIdFromFileName(string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        int separator = baseName.LastIndexOf('_');
        if (separator < 0 || separator == baseName.Length - 1)
            return 0;

        return uint.TryParse(baseName[(separator + 1)..], out uint id) ? id : 0;
    }

    private static string? RegistryValue(RegistryKey root, string subKey, string valueName)
    {
        using RegistryKey? key = root.OpenSubKey(subKey);
        return key?.GetValue(valueName) as string;
    }
}
