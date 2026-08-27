using System.Globalization;
using System.Reflection;
using System.Text.Json;

using System.Runtime.InteropServices;

namespace ModernWigiDash.Core.Theming;

/// <summary>
/// WPF-free color value produced by <see cref="ThemeSettings.ParseColor"/>.
/// Kept as a plain record struct so the Core library has no UI framework dependency.
/// The sequential layout is the MA0008 analyzer pin: every field is a byte
/// (blittable), and the deterministic layout keeps the record cheap to copy.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct RgbaColor(byte A, byte R, byte G, byte B);

/// <summary>
/// Serializable theme definition for the WPF chrome (surfaces, accents, and text)
/// outside the widget preview canvas. Colors are stored as #RRGGBB hex strings and
/// persisted to app_theme.json in the user state dir
/// (%LOCALAPPDATA%\ModernWigiDash, ADR-0021: beside profile.json and
/// app_settings.json; the exe-dir copy is a read-only one-time migration source).
/// Property names map directly to the resource keys (prop name + "Color") used
/// by App.xaml. The model is display-free: the dialog's labels, groups, and
/// descriptions live on the App side (<c>ThemePresentation</c>), beside the UI
/// that renders them.
/// </summary>
public class ThemeSettings
{
    // Surfaces
    /// <summary>App background behind the panels.</summary>
    public string BgDark { get; set; } = "#121214";
    /// <summary>Panel background (inspector, dialogs).</summary>
    public string BgPanel { get; set; } = "#1A1A1E";
    /// <summary>Card background (widgets, list rows).</summary>
    public string BgCard { get; set; } = "#26262B";
    /// <summary>Default border color.</summary>
    public string Border { get; set; } = "#3F3F46";

    // Accents
    /// <summary>Warning/red accent.</summary>
    public string AccentRed { get; set; } = "#F59E0B";
    /// <summary>Material primary accent.</summary>
    public string M3Primary { get; set; } = "#FBBF24";
    /// <summary>Material primary container fill.</summary>
    public string M3PrimaryContainer { get; set; } = "#3F3F46";
    /// <summary>Material on-primary-container color (text and icons on the primary container).</summary>
    public string M3OnPrimaryContainer { get; set; } = "#FBBF24";
    /// <summary>Success/green accent.</summary>
    public string AccentGreen { get; set; } = "#10B981";

    // Text
    /// <summary>Primary text color.</summary>
    public string TextPrimary { get; set; } = "#FAFAFA";
    /// <summary>Secondary text color.</summary>
    public string TextSecondary { get; set; } = "#A1A1AA";

    // Interactive states & chrome extras
    /// <summary>Hover fill for controls.</summary>
    public string ControlHover { get; set; } = "#3F3F46";
    /// <summary>Hover fill for dropdown items.</summary>
    public string DropdownHover { get; set; } = "#2A2A30";
    /// <summary>Custom title bar background.</summary>
    public string TitleBar { get; set; } = "#0B0B0C";
    /// <summary>Status bar background.</summary>
    public string StatusBarBackground { get; set; } = "#0E0E10";
    /// <summary>Danger-state fill (confirmation dialogs).</summary>
    public string DangerBackground { get; set; } = "#7F1D1D";
    /// <summary>Danger-state border.</summary>
    public string DangerBorder { get; set; } = "#EF4444";
    /// <summary>Success-state fill (confirmation dialogs).</summary>
    public string SuccessBackground { get; set; } = "#064E3B";
    /// <summary>Success-state border.</summary>
    public string SuccessBorder { get; set; } = "#10B981";

    /// <summary>
    /// Every themeable property: the string-typed instance properties of this
    /// type. The single enumeration of the "themeable = string-typed property"
    /// rule, shared by the resource applier, the change fingerprint, and the
    /// theme dialog — so they can never drift apart. Static, so it never
    /// enumerates itself.
    /// </summary>
    public static IReadOnlyList<PropertyInfo> StringProperties { get; } =
        typeof(ThemeSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .ToArray();

    /// <summary>
    /// The active theme. Lazily loaded on first access so consumers never
    /// observe the default unloaded state, regardless of when they touch it
    /// relative to App startup.
    /// </summary>
    public static ThemeSettings Theme
    {
        get => field ??= Load();
        set => field = value;
    }

    /// <summary>The theme file name (ADR-0021: it lives in the user state dir,
    /// beside profile.json and app_settings.json).</summary>
    public const string FileName = "app_theme.json";

    /// <summary>The user state directory name: the same dir as the profile and
    /// app-settings files. Pinned in lockstep against the App's
    /// <c>ProfilePersistence.DirectoryName</c> by test (ThemeSettingsTests), so
    /// the state dir can never split between the two owners.</summary>
    public const string StateDirectoryName = "ModernWigiDash";

    /// <summary>The one theme file location: %LOCALAPPDATA%\ModernWigiDash\app_theme.json
    /// (ADR-0021). The old exe-dir location is a read-only legacy migration
    /// source (<see cref="LegacyPath"/>), never a read/write target.</summary>
    public static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            StateDirectoryName, FileName);

    /// <summary>The legacy exe-dir theme file (pre-ADR-0021): the one-time
    /// migration source <see cref="Load()"/> reads when the state file is absent.
    /// Never written by the app.</summary>
    public static string LegacyPath()
        => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// Loads the theme from the state dir, falling back to the default theme
    /// when the file is missing or unparseable. When the state file is absent,
    /// the one-time legacy migration runs first (a parseable exe-dir copy is
    /// carried across and logged, so an upgraded install keeps the colors the
    /// user last saw; a corrupt or absent legacy copy is a no-op).
    /// </summary>
    /// <returns>The loaded theme, or a default theme when the file is missing or corrupt.</returns>
    public static ThemeSettings Load()
        => Load(DefaultPath(), LegacyPath(), FileLog.Write);

    /// <summary>
    /// The load composition with explicit paths (the test seam; production
    /// binds <see cref="DefaultPath"/> + <see cref="LegacyPath"/> + FileLog):
    /// the state file wins when present (a corrupt one degrades to the
    /// defaults with one log line, never repaired from the legacy copy); the
    /// legacy migration runs only when the state file is ABSENT.
    /// </summary>
    internal static ThemeSettings Load(string targetPath, string legacyPath, Action<string>? log = null)
    {
        if (!File.Exists(targetPath)
            && MigrateLegacyCopy(targetPath, legacyPath, log) is { } migrated)
        {
            return migrated;
        }

        try
        {
            if (File.Exists(targetPath))
            {
                string json = File.ReadAllText(targetPath);
                var loaded = JsonSerializer.Deserialize<ThemeSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable theme file: fall back to defaults
            string message = $"Theme load failed, falling back to defaults: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(message);
            log?.Invoke(message);
        }
        return new ThemeSettings();
    }

    /// <summary>
    /// The one-time legacy migration (ADR-0021): when <paramref name="targetPath"/>
    /// is absent and the legacy copy at <paramref name="legacyPath"/> parses, the
    /// copy is carried across (best-effort write; the in-memory copy is returned
    /// either way, so a failed migration write still honors the colors the user
    /// last saw for this session) and one log line names the outcome. Returns
    /// the migrated theme, or null when there is nothing to migrate (either side
    /// absent, the same file, or an unparseable legacy copy).
    /// </summary>
    internal static ThemeSettings? MigrateLegacyCopy(string targetPath, string legacyPath, Action<string>? log = null)
    {
        if (File.Exists(targetPath)) return null;
        if (!File.Exists(legacyPath)) return null;
        if (string.Equals(targetPath, legacyPath, StringComparison.OrdinalIgnoreCase)) return null;

        ThemeSettings? legacy = LoadFrom(legacyPath);
        if (legacy is null)
        {
            // The legacy copy exists but does not parse: one line, no migration.
            log?.Invoke($"[THEME] Legacy theme file '{legacyPath}' is unparseable; not migrating");
            return null;
        }

        bool persisted = Save(legacy, targetPath);
        log?.Invoke(persisted
            ? $"[THEME] Migrated legacy theme file from '{legacyPath}' to '{targetPath}'"
            : $"[THEME] Legacy theme file '{legacyPath}' parsed but the migration write to '{targetPath}' failed; using the in-memory copy");
        return legacy;
    }

    /// <summary>Loads a theme from an explicit path (the read half of the test
    /// seam; production routes through <see cref="Load(string, string, Action{string}?)"/>):
    /// absent or unparseable returns null.</summary>
    internal static ThemeSettings? LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Persists the active <see cref="Theme"/> to the state dir (creating
    /// the directory when missing).</summary>
    /// <returns>True when the write succeeded, false on a write failure (the caller surfaces it).</returns>
    public static bool Save()
        => Save(Theme, DefaultPath());

    /// <summary>Persists <paramref name="theme"/> to an explicit path (the write
    /// half of the test seam; production binds <see cref="DefaultPath"/>).</summary>
    internal static bool Save(ThemeSettings theme, string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(theme, SaveOptions));
            return true;
        }
        catch (Exception)
        {
            // Caller decides how to surface a write failure (e.g. locked or read-only file).
            return false;
        }
    }

    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    /// <summary>
    /// Parses a #RRGGBB or #AARRGGBB hex string into a <see cref="RgbaColor"/>, or returns
    /// <c>null</c> when the value is not a valid color. A leading '#' is optional.
    /// </summary>
    public static RgbaColor? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        ReadOnlySpan<char> h = hex.Trim().TrimStart('#');
        try
        {
            if (h.Length == 6)
            {
                return new RgbaColor(
                    255,
                    byte.Parse(h.Slice(0, 2), NumberStyles.HexNumber),
                    byte.Parse(h.Slice(2, 2), NumberStyles.HexNumber),
                    byte.Parse(h.Slice(4, 2), NumberStyles.HexNumber));
            }
            if (h.Length == 8)
            {
                return new RgbaColor(
                    byte.Parse(h.Slice(0, 2), NumberStyles.HexNumber),
                    byte.Parse(h.Slice(2, 2), NumberStyles.HexNumber),
                    byte.Parse(h.Slice(4, 2), NumberStyles.HexNumber),
                    byte.Parse(h.Slice(6, 2), NumberStyles.HexNumber));
            }
        }
        catch (FormatException)
        {
            // Invalid hex value — treat as unparseable
            System.Diagnostics.Debug.WriteLine("Invalid theme hex color value, treating as unparseable");
        }
        return null;
    }
}
