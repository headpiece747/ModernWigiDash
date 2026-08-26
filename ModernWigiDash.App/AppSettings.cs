using System.IO;
using System.Text.Json;

namespace ModernWigiDash.App;

/// <summary>
/// The app's own persisted settings (app_settings.json beside the profile in
/// %LOCALAPPDATA%\ModernWigiDash): the machine-local settings a profile
/// import must never overwrite (the profile is a traveling artifact; these
/// are not). The global-hotkey kill switch (ADR-0019) and the user's
/// AutoHotkey interpreter path live here, deliberately outside the profile.
/// </summary>
internal sealed record AppSettings
{
    /// <summary>
    /// The global-hotkey integration's kill switch: true (checked) means the
    /// integration is killed - no global-hotkey registration and no AHK
    /// script spawn, even from a tap; every other action keeps running.
    /// False (the default, the vendor parity) means the integration is live.
    /// </summary>
    public bool KillSwitch { get; init; }

    /// <summary>
    /// The user's AutoHotkey interpreter (autohotkey.exe) path; blank means
    /// unset (the AHK action refuses with a log line - nothing is bundled or
    /// auto-detected).
    /// </summary>
    public string AhkInterpreterPath { get; init; } = "";
}

/// <summary>
/// The app_settings.json store (the ProfilePersistence shape): a corrupt or
/// absent file repairs to the defaults on load, and the save is atomic
/// (tmp + replace) so a mid-write crash can never corrupt the file. The
/// path is injectable for tests (a temp dir); production uses the LocalAppData
/// default.
/// </summary>
internal sealed class AppSettingsStore
{
    public const string FileName = "app_settings.json";

    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Action<string>? _log;

    public AppSettingsStore(string? path = null, Action<string>? log = null)
    {
        _path = path ?? DefaultPath();
        _log = log;
    }

    public static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProfilePersistence.DirectoryName, FileName);

    /// <summary>
    /// Loads the settings; an absent file returns the defaults and a corrupt
    /// or unreadable one repairs to the defaults with one log line (the
    /// absent-service house pattern: a bad machine-local file is a degraded
    /// default, never a throw into the wiring).
    /// </summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<AppSettings>(stream) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
        {
            _log?.Invoke($"app_settings.json unreadable; using defaults ({ex.Message})");
            return new AppSettings();
        }
    }

    /// <summary>Persists the settings atomically; a failed write logs one line (best-effort).</summary>
    public void Save(AppSettings settings)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (directory is not null)
                Directory.CreateDirectory(directory);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SaveOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            _log?.Invoke($"app_settings.json save failed: {ex.Message}");
        }
    }
}
