using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ModernWigiDash.Sdk;

using System.Runtime.InteropServices;

namespace ModernWigiDash.Core.Theming;

/// <summary>
/// WPF-free color value produced by <see cref="ThemeSettings.ParseColor"/>.
/// Kept as a plain record struct so the Core library has no UI framework dependency.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct RgbaColor(byte A, byte R, byte G, byte B);

/// <summary>
/// Serializable theme definition for the WPF chrome (surfaces, accents, and text)
/// outside the widget preview canvas. Colors are stored as #RRGGBB hex strings and
/// persisted to app_theme.json next to the executable. Property names map directly
/// to the resource keys (prop name + "Color") used by App.xaml. The model is
/// display-free: the dialog's labels, groups, and descriptions live on the App
/// side (<see cref="ModernWigiDash.App.Theming.ThemePresentation"/>), beside the
/// UI that renders them.
/// </summary>
public class ThemeSettings
{
    // Surfaces
    public string BgDark { get; set; } = "#121214";
    public string BgPanel { get; set; } = "#1A1A1E";
    public string BgCard { get; set; } = "#26262B";
    public string Border { get; set; } = "#3F3F46";

    // Accents
    public string AccentRed { get; set; } = "#F59E0B";
    public string M3Primary { get; set; } = "#FBBF24";
    public string M3PrimaryContainer { get; set; } = "#3F3F46";
    public string M3OnPrimaryContainer { get; set; } = "#FBBF24";
    public string AccentGreen { get; set; } = "#10B981";

    // Text
    public string TextPrimary { get; set; } = "#FAFAFA";
    public string TextSecondary { get; set; } = "#A1A1AA";

    // Interactive states & chrome extras
    public string ControlHover { get; set; } = "#3F3F46";
    public string DropdownHover { get; set; } = "#2A2A30";
    public string TitleBar { get; set; } = "#0B0B0C";
    public string StatusBarBackground { get; set; } = "#0E0E10";
    public string DangerBackground { get; set; } = "#7F1D1D";
    public string DangerBorder { get; set; } = "#EF4444";
    public string SuccessBackground { get; set; } = "#064E3B";
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
    /// The active theme. Lazily loaded from app_theme.json on first access so
    /// consumers never observe the default unloaded state, regardless of when
    /// they touch it relative to App startup.
    /// </summary>
    public static ThemeSettings Theme
    {
        get => field ??= Load();
        set => field = value;
    }

    private static readonly string ThemePath =
        Path.Combine(AppContext.BaseDirectory, "app_theme.json");

    public static ThemeSettings Load()
    {
        try
        {
            if (File.Exists(ThemePath))
            {
                string json = File.ReadAllText(ThemePath);
                var loaded = JsonSerializer.Deserialize<ThemeSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable theme file — fall back to defaults
            string message = $"Theme load failed, falling back to defaults: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(message);
            FileLog.Write(message);
        }
        return new ThemeSettings();
    }

    public static bool Save()
    {
        try
        {
            File.WriteAllText(ThemePath, JsonSerializer.Serialize(Theme, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception)
        {
            // Caller decides how to surface a write failure (e.g. locked or read-only file).
            return false;
        }
    }

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
