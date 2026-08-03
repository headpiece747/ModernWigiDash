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
    public string BgDark { get; set; } = "#1B2930";
    public string BgPanel { get; set; } = "#243742";
    public string BgCard { get; set; } = "#2F4550";
    public string Border { get; set; } = "#586F7C";

    // Accents
    public string AccentRed { get; set; } = "#870000";
    public string M3Primary { get; set; } = "#FFCD85";
    public string M3PrimaryContainer { get; set; } = "#586F7C";
    public string M3OnPrimaryContainer { get; set; } = "#FFCD85";
    public string AccentGreen { get; set; } = "#FFCD85";

    // Text
    public string TextPrimary { get; set; } = "#C6E0FF";
    public string TextSecondary { get; set; } = "#98B4C8";

    // Interactive states & chrome extras
    public string ControlHover { get; set; } = "#3D5A68";
    public string DropdownHover { get; set; } = "#25334D";
    public string TitleBar { get; set; } = "#0F111A";
    public string StatusBarBackground { get; set; } = "#10121C";
    public string DangerBackground { get; set; } = "#7F1D1D";
    public string DangerBorder { get; set; } = "#EF4444";
    public string SuccessBackground { get; set; } = "#14532D";
    public string SuccessBorder { get; set; } = "#22C55E";

    public static ThemeSettings Theme { get; set; } = new();

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
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_theme.json");

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
        string h = hex.Trim().TrimStart('#');
        try
        {
            if (h.Length == 6)
            {
                return new RgbaColor(
                    255,
                    Convert.ToByte(h.Substring(0, 2), 16),
                    Convert.ToByte(h.Substring(2, 2), 16),
                    Convert.ToByte(h.Substring(4, 2), 16));
            }
            if (h.Length == 8)
            {
                return new RgbaColor(
                    Convert.ToByte(h.Substring(0, 2), 16),
                    Convert.ToByte(h.Substring(2, 2), 16),
                    Convert.ToByte(h.Substring(4, 2), 16),
                    Convert.ToByte(h.Substring(6, 2), 16));
            }
        }
        catch (Exception)
        {
            // Invalid hex value — treat as unparseable
        }
        return null;
    }
}
