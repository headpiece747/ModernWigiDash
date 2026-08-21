namespace ModernWigiDash.Widgets;

/// <summary>
/// The hotkey executor's decision rules — action-count cap, repeat/delay
/// clamps, the URL scheme allowlist, the mouse-button flag map, and the wheel
/// direction rule — extracted from the SendInput executor so they are
/// testable without P/Invoke.
/// </summary>
internal static class HotkeyActionPolicy
{
    public const int MaxActions = 64;
    public const int MaxTextLength = 4096;

    private const int MaxRepeat = 20;
    private const int MaxDelayMs = 5000;

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheel = 0x0800;

    public static int ClampRepeat(int repeat) => Math.Clamp(repeat, 1, MaxRepeat);

    public static int ClampDelayMs(int delayMs) => Math.Clamp(delayMs, 0, MaxDelayMs);

    /// <summary>Only http, https, and mailto URLs may be shell-opened.</summary>
    public static bool IsAllowedUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
           && uri.Scheme is "http" or "https" or "mailto";

    /// <summary>The SendInput down/up flag pair for a named mouse button
    /// (left is the default for unknown names).</summary>
    public static (uint Down, uint Up) MouseButtonFlags(string button)
        => button.Trim().ToLowerInvariant() switch
        {
            "right" or "rbutton" => (MouseRightDown, MouseRightUp),
            "middle" or "mbutton" => (MouseMiddleDown, MouseMiddleUp),
            _ => (MouseLeftDown, MouseLeftUp)
        };

    /// <summary>The wheel amount: an explicit number, else "down" = -120,
    /// anything else (e.g. "up") = +120.</summary>
    public static int WheelAmount(string direction)
    {
        if (int.TryParse(direction, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return value;
        return direction.Trim().Equals("down", StringComparison.OrdinalIgnoreCase) ? -120 : 120;
    }

    public static uint WheelFlag => MouseWheel;
}
