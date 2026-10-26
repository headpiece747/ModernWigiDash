namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Official color tokens for the Cozy To-Do Companion widget.
/// Calibrated strictly to the ModernWigiDash theme tokens and Calendar widget accents.
/// </summary>
internal static class CozyTodoPalette
{
    /// <summary>Widget background and pet sanctuary frame interior (#121214).</summary>
    public static readonly SKColor BgDark = new(0x12, 0x12, 0x14);

    /// <summary>Outer card container and Pomodoro background (#1A1A1E).</summary>
    public static readonly SKColor BgPanel = new(0x1A, 0x1A, 0x1E);

    /// <summary>Task item row background and interactive card (#26262B).</summary>
    public static readonly SKColor BgCard = new(0x26, 0x26, 0x2B);

    /// <summary>Clean divider lines and checkbox outlines (#3F3F46).</summary>
    public static readonly SKColor Border = new(0x3F, 0x3F, 0x46);

    /// <summary>Level progress arc, XP fill, flame streak (#F59E0B).</summary>
    public static readonly SKColor AccentAmber = new(0xF5, 0x9E, 0x0B);

    /// <summary>Snack treats / coin counter, level badge highlight (#FBBF24).</summary>
    public static readonly SKColor HighlightAmber = new(0xFB, 0xBF, 0x24);

    /// <summary>Completed checkmark, Happy mood pill (#10B981).</summary>
    public static readonly SKColor AccentGreen = new(0x10, 0xB9, 0x81);

    /// <summary>Pomodoro focus ring, category tag pills (#4F8CFF).</summary>
    public static readonly SKColor CalendarAzure = new(0x4F, 0x8C, 0xFF);

    /// <summary>Urgent deadlines, low-timer alert states (#EF4444).</summary>
    public static readonly SKColor CalendarLiveRed = new(0xEF, 0x44, 0x44);

    /// <summary>High-contrast task titles and digital clock numbers (#FAFAFA).</summary>
    public static readonly SKColor TextPrimary = new(0xFA, 0xFA, 0xFA);

    /// <summary>Subtitles, timestamps, due dates, secondary hints (#A1A1AA).</summary>
    public static readonly SKColor TextSecondary = new(0xA1, 0xA1, 0xAA);
}
