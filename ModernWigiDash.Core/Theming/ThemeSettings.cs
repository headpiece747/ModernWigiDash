using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ModernWigiDash.Core.Theming;

/// <summary>
/// WPF-free color value produced by <see cref="ThemeSettings.ParseColor"/>.
/// Kept as a plain record struct so the Core library has no UI framework dependency.
/// </summary>
public readonly record struct RgbaColor(byte A, byte R, byte G, byte B);

/// <summary>
/// Serializable theme definition for the WPF chrome (surfaces, accents, and text)
/// outside the widget preview canvas. Colors are stored as #RRGGBB hex strings and
/// persisted to app_theme.json next to the executable. Property names map directly
/// to the resource keys (prop name + "Color") used by App.xaml.
/// </summary>
public class ThemeSettings
{
    // Surfaces
    public string BgDark { get; set; } = "#121214";
    public string BgPanel { get; set; } = "#1A1A1E";
    public string BgCard { get; set; } = "#26262B";
    // #3F3F46 (zinc-700) is deliberately shared by Border, M3PrimaryContainer,
    // and ControlHover — one neutral value serving borders, badge backgrounds,
    // and hover fills so the chrome reads as one family. The duplicates are
    // intentional, not drift: each property is themeable independently.
    public string Border { get; set; } = "#3F3F46";

    // Accents
    public string AccentRed { get; set; } = "#F59E0B";
    public string M3Primary { get; set; } = "#FBBF24";
    public string M3PrimaryContainer { get; set; } = "#3F3F46"; // shared zinc-700, see Border
    public string M3OnPrimaryContainer { get; set; } = "#FBBF24";
    public string AccentGreen { get; set; } = "#10B981";

    // Text
    public string TextPrimary { get; set; } = "#FAFAFA";
    public string TextSecondary { get; set; } = "#A1A1AA";

    // Interactive states & chrome extras
    public string ControlHover { get; set; } = "#3F3F46"; // shared zinc-700, see Border
    public string DropdownHover { get; set; } = "#2A2A30";
    public string TitleBar { get; set; } = "#0B0B0C";
    public string StatusBarBackground { get; set; } = "#0E0E10";
    public string DangerBackground { get; set; } = "#7F1D1D";
    public string DangerBorder { get; set; } = "#EF4444";
    public string SuccessBackground { get; set; } = "#064E3B";
    public string SuccessBorder { get; set; } = "#10B981";

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

    /// <summary>
    /// Human-friendly label for each theme property, used by the theme dialog so a user
    /// knows what they are changing without seeing the raw property name.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DisplayNames =
        new Dictionary<string, string>
        {
            ["BgDark"] = "App Background",
            ["BgPanel"] = "Panel / Sidebar Background",
            ["BgCard"] = "Card / Input Background",
            ["Border"] = "Borders & Dividers",
            ["AccentRed"] = "Primary Accent",
            ["M3Primary"] = "Highlight Accent",
            ["M3PrimaryContainer"] = "Badge / Tag Background",
            ["M3OnPrimaryContainer"] = "Badge / Tag Text",
            ["AccentGreen"] = "Status Text Highlight",
            ["TextPrimary"] = "Primary Text",
            ["TextSecondary"] = "Secondary Text / Hints",
            ["ControlHover"] = "Button Hover Background",
            ["DropdownHover"] = "Dropdown Hover / Selected",
            ["TitleBar"] = "Title Bar & Scrollbar",
            ["StatusBarBackground"] = "Status Bar Background",
            ["DangerBackground"] = "Destructive Button Background",
            ["DangerBorder"] = "Destructive Button Border",
            ["SuccessBackground"] = "Connected Badge Background",
            ["SuccessBorder"] = "Connected Badge Border"
        };

    /// <summary>
    /// Short explanation of where each color appears, shown under the label in the theme dialog.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions =
        new Dictionary<string, string>
        {
            ["BgDark"] = "Main window background behind the preview canvas.",
            ["BgPanel"] = "Header, sidebar, and inspector panel background.",
            ["BgCard"] = "Cards, catalog items, and input field background.",
            ["Border"] = "Borders and divider lines between panels.",
            ["AccentRed"] = "Buttons, pressed state, and selection highlights.",
            ["M3Primary"] = "Hover borders, section titles, and key highlights.",
            ["M3PrimaryContainer"] = "Background of the small grid-size badges.",
            ["M3OnPrimaryContainer"] = "Text sitting on the badge backgrounds.",
            ["AccentGreen"] = "Status text such as the Active Widgets counter.",
            ["TextPrimary"] = "Main heading and body text.",
            ["TextSecondary"] = "Secondary labels, hints, and captions.",
            ["ControlHover"] = "Button background when the mouse hovers over it.",
            ["DropdownHover"] = "Hovered / selected row in ComboBox dropdowns.",
            ["TitleBar"] = "OS title bar and scrollbar thumb.",
            ["StatusBarBackground"] = "Bottom status bar background.",
            ["DangerBackground"] = "Destructive actions such as Remove / Clear Canvas.",
            ["DangerBorder"] = "Border of the destructive buttons.",
            ["SuccessBackground"] = "USB badge background when the WigiDash is attached.",
            ["SuccessBorder"] = "USB badge border when the WigiDash is attached."
        };

    /// <summary>
    /// Section grouping for the theme dialog, in display order.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Groups =
        new Dictionary<string, string>
        {
            ["BgDark"] = "Surfaces",
            ["BgPanel"] = "Surfaces",
            ["BgCard"] = "Surfaces",
            ["Border"] = "Surfaces",
            ["AccentRed"] = "Accents & Highlighting",
            ["M3Primary"] = "Accents & Highlighting",
            ["M3PrimaryContainer"] = "Accents & Highlighting",
            ["M3OnPrimaryContainer"] = "Accents & Highlighting",
            ["AccentGreen"] = "Accents & Highlighting",
            ["TextPrimary"] = "Text",
            ["TextSecondary"] = "Text",
            ["ControlHover"] = "Interactive & Status",
            ["DropdownHover"] = "Interactive & Status",
            ["TitleBar"] = "Interactive & Status",
            ["StatusBarBackground"] = "Interactive & Status",
            ["DangerBackground"] = "Interactive & Status",
            ["DangerBorder"] = "Interactive & Status",
            ["SuccessBackground"] = "Interactive & Status",
            ["SuccessBorder"] = "Interactive & Status"
        };

    /// <summary>
    /// Returns the friendly display name for a property, falling back to the raw name.
    /// </summary>
    public static string FriendlyName(string propertyName) =>
        DisplayNames.TryGetValue(propertyName, out var name) ? name : propertyName;

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
        catch (Exception)
        {
            // Corrupt or unreadable theme file — fall back to defaults
            System.Diagnostics.Debug.WriteLine("Theme load failed, falling back to defaults");
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
