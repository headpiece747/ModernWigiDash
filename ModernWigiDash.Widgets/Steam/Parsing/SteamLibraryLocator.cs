using System.Collections.Concurrent;
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
    /// <summary>How many folder levels below an install directory the
    /// executable scan descends (Unreal ships the game under
    /// <c>Binaries\Win64</c>, two levels down).</summary>
    private const int MaxScanDepth = 3;

    /// <summary>The directory ceiling for one executable scan (a deep tree must
    /// not turn one refresh into a disk walk; an Unreal game needs ~64 to reach
    /// <c>Binaries\Win64</c>, so 128 leaves margin).</summary>
    private const int MaxScanDirectories = 128;

    /// <summary>The shipped helper binaries that must never become a game's
    /// process key: installers, crash reporters, anticheat bootstrappers, and
    /// redistributables. A prefix match keeps the list short while covering the
    /// versioned names (<c>vcredist_x64.exe</c>, <c>UE4PrereqSetup_x64.exe</c>).
    /// </summary>
    private static readonly string[] HelperExecutablePrefixes =
    [
        "unins", "vcredist", "vc_redist", "dxsetup", "dxwebsetup", "ue4prereqsetup",
        "ueprereqsetup", "unitycrashhandler", "crashreportclient", "crashpad_handler",
        "easyanticheat", "battleye", "physx", "steamerrorreporter", "dotnetfx",
        "oalinst", "audiodg",
    ];

    /// <summary>The resolved primary executable per install directory, keyed by
    /// the directory path and invalidated when its stamp changes. The ACF has
    /// no executable field in the current Steam format, so the process key is
    /// derived from the install folder once instead of rescanning every
    /// 3-second tick.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, string Executable)> ExecutableCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The playtime map's cache (localconfig.vdf is multi-megabyte, so
    /// it is reparsed only when the file's stamp or length changes).</summary>
    private static readonly System.Threading.Lock PlaytimeGate = new();
    private static string? _playtimeFile;
    private static DateTime _playtimeStamp;
    private static long _playtimeLength;
    private static Dictionary<uint, double> _playtimes = [];

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
    public static IReadOnlyList<SteamAppManifest> LoadManifests(IReadOnlyList<string> libraryRoots, Action<string>? log = null, string? steamPath = null)
    {
        Dictionary<uint, double> playtimes = LoadPlaytimes(steamPath, log);
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
                    manifests.Add(EnrichManifest(manifest, steamApps, playtimes));
            }
        }

        manifests.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return manifests;
    }

    /// <summary>
    /// Fills the two facts the ACF does not carry: the process-matching
    /// executable (the install folder's largest non-helper exe; the ACF's
    /// optional <c>executable</c> key wins when a fixture supplies one) and
    /// the playtime (Steam's <c>localconfig.vdf</c>, in hours).
    /// </summary>
    private static SteamAppManifest EnrichManifest(SteamAppManifest manifest, string steamApps, Dictionary<uint, double> playtimes)
    {
        string executable = manifest.Executable.Length > 0
            ? manifest.Executable
            : ResolveExecutable(steamApps, manifest.InstallDir);

        double hours = manifest.HoursOnRecord;
        if (hours <= 0 && playtimes.TryGetValue(manifest.AppId, out double known))
            hours = known;

        if (string.Equals(executable, manifest.Executable, StringComparison.Ordinal)
            && Math.Abs(hours - manifest.HoursOnRecord) < 1e-9)
        {
            return manifest;
        }

        return manifest with { Executable = executable, HoursOnRecord = hours };
    }

    /// <summary>
    /// Resolves the process-matching executable for an installed app: the
    /// largest non-helper <c>.exe</c> under the install folder (three levels
    /// deep, which covers the Unreal <c>Binaries\Win64</c> shape). The ACF
    /// carries no executable fact in the current format, and the largest
    /// binary is the game's own: launchers, crash handlers, and bundled
    /// redistributables are smaller. The result is the file NAME (the shape
    /// <see cref="SteamAppManifest.ProcessName"/> reduces to a process key).
    /// Cached per install directory, invalidated by the directory's write
    /// stamp, so the 3-second tick never rescans.
    /// </summary>
    private static string ResolveExecutable(string steamApps, string installDir)
    {
        if (installDir.Length == 0)
            return "";

        string directory;
        try
        {
            directory = Path.Combine(steamApps, "common", installDir);
        }
        catch (ArgumentException)
        {
            return ""; // an unusable installdir value (a path-invalid character).
        }

        DateTime stamp;
        try
        {
            if (!Directory.Exists(directory))
                return "";
            stamp = Directory.GetLastWriteTimeUtc(directory);
        }
        catch (Exception)
        {
            return "";
        }

        if (ExecutableCache.TryGetValue(directory, out (DateTime Stamp, string Executable) cached) && cached.Stamp == stamp)
            return cached.Executable;

        string executable = ScanForExecutable(directory);
        ExecutableCache[directory] = (stamp, executable);
        return executable;
    }

    /// <summary>
    /// The bounded breadth-first scan behind <see cref="ResolveExecutable"/>:
    /// the largest non-helper <c>.exe</c> within <see cref="MaxScanDepth"/>
    /// folder levels and <see cref="MaxScanDirectories"/> directories (a deep
    /// tree must not turn one refresh into a disk walk).
    /// </summary>
    private static string ScanForExecutable(string directory)
    {
        string best = "";
        long largest = -1;
        int visited = 0;
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((directory, 0));
        while (pending.Count > 0 && visited < MaxScanDirectories)
        {
            (string current, int depth) = pending.Dequeue();
            visited++;
            foreach (string file in SafeGetFiles(current))
            {
                string name = Path.GetFileName(file);
                if (IsHelperExecutable(name))
                    continue;

                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    continue;
                }

                if (length > largest)
                {
                    largest = length;
                    best = name;
                }
            }

            if (depth < MaxScanDepth)
            {
                foreach (string child in SafeGetDirectories(current))
                    pending.Enqueue((child, depth + 1));
            }
        }

        return best;
    }

    /// <summary>The <c>.exe</c> files directly in a folder; empty when the
    /// folder is unreadable or denied (the install folder is user content, so
    /// one denied child must not abort the whole scan).</summary>
    private static string[] SafeGetFiles(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The child folders of one directory; empty when it is unreadable.</summary>
    private static string[] SafeGetDirectories(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether an executable is a shipped helper rather than the game itself
    /// (installers, crash handlers, anticheat bootstrappers, redistributables).
    /// Matching one of these would report a false Running, so they never
    /// become the process key.
    /// </summary>
    private static bool IsHelperExecutable(string name)
        => HelperExecutablePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Loads the playtime map (app id → hours) from Steam's
    /// <c>userdata\&lt;account&gt;\config\localconfig.vdf</c>, the only local
    /// source of playtime (the ACF has no <c>hours_on_record</c> in the
    /// current format). Reparsed only when the file's stamp or length
    /// changes: localconfig.vdf runs to several megabytes.
    /// </summary>
    private static Dictionary<uint, double> LoadPlaytimes(string? steamPath, Action<string>? log)
    {
        if (string.IsNullOrEmpty(steamPath))
            return [];

        string? file = FindNewestLocalConfig(steamPath);
        if (file is null)
            return [];

        DateTime stamp;
        long length;
        try
        {
            var info = new FileInfo(file);
            stamp = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (Exception)
        {
            return [];
        }

        lock (PlaytimeGate)
        {
            if (string.Equals(_playtimeFile, file, StringComparison.OrdinalIgnoreCase)
                && _playtimeStamp == stamp
                && _playtimeLength == length)
            {
                return _playtimes;
            }
        }

        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Steam: playtime file unreadable ({file}): {ex.Message}");
            return [];
        }

        Dictionary<uint, double> parsed = ParsePlaytimes(text);
        lock (PlaytimeGate)
        {
            _playtimeFile = file;
            _playtimeStamp = stamp;
            _playtimeLength = length;
            _playtimes = parsed;
            return _playtimes;
        }
    }

    /// <summary>
    /// The most recently written <c>localconfig.vdf</c> across the machine's
    /// Steam accounts: the signed-in account's file is the freshest one. Null
    /// when no account config exists.
    /// </summary>
    private static string? FindNewestLocalConfig(string steamPath)
    {
        try
        {
            string userdata = Path.Combine(steamPath, "userdata");
            if (!Directory.Exists(userdata))
                return null;

            string? newest = null;
            DateTime newestStamp = DateTime.MinValue;
            foreach (string account in Directory.EnumerateDirectories(userdata, "*", SearchOption.TopDirectoryOnly))
            {
                string candidate = Path.Combine(account, "config", "localconfig.vdf");
                if (LocalConfigStamp(candidate) is not DateTime stamp || stamp <= newestStamp)
                    continue;

                newestStamp = stamp;
                newest = candidate;
            }

            return newest;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The config file's write stamp, or null when it is absent or
    /// locked (a vanished account folder simply loses the race).</summary>
    private static DateTime? LocalConfigStamp(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the <c>"apps" { "&lt;appid&gt;" { "Playtime" "&lt;minutes&gt;" } }</c>
    /// blocks of a localconfig.vdf into hours per app id. Every <c>apps</c>
    /// block is merged (the config nests them under different owners, and an
    /// app id's playtime is consistent wherever it appears); the first value
    /// wins. Unparseable entries are skipped, never thrown.
    /// </summary>
    internal static Dictionary<uint, double> ParsePlaytimes(string text)
    {
        var playtimes = new Dictionary<uint, double>();
        ReadOnlySpan<char> span = text.AsSpan();

        int appsIndex = 0;
        while (SteamVdfTokenizer.TryFindBlock(span, "apps", appsIndex, out int appsStart, out int appsEnd))
        {
            ReadOnlySpan<char> apps = span[appsStart..appsEnd];
            int childIndex = 0;
            while (SteamVdfTokenizer.TryFindChildBlock(apps, childIndex, out string key, out int childStart, out int childEnd))
            {
                childIndex = childEnd + 1;
                if (!SteamVdfTokenizer.TryParseUInt32(key.AsSpan(), out uint appId) || playtimes.ContainsKey(appId))
                    continue;

                ReadOnlySpan<char> child = apps[childStart..childEnd];
                if (SteamVdfTokenizer.TryFindValue(child, "Playtime", 0, out int start, out int length)
                    && SteamVdfTokenizer.TryParseInt64(child[start..(start + length)], out long minutes)
                    && minutes > 0)
                {
                    playtimes[appId] = minutes / 60.0;
                }
            }

            appsIndex = appsEnd + 1;
        }

        return playtimes;
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
