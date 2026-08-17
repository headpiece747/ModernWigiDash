namespace ModernWigiDash.App.Theming;

/// <summary>
/// The theme dialog's display rules (App): the friendly labels, section
/// groups, and per-property descriptions the dialog renders beside each
/// color swatch. This copy deliberately lives on the App side, beside the
/// dialog that renders it, instead of on the serialized Core model —
/// <c>ThemeSettings</c> keeps only values, the StringProperties rule, and
/// color parsing, so a display rename never touches the persisted model and
/// the two can never drift from each other.
/// </summary>
internal static class ThemePresentation
{
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
}
