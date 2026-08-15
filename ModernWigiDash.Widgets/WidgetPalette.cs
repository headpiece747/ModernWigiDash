using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The widgets' shared fallback palette: the color rendered when a widget's
/// [WidgetProperty] color hex is invalid. Every color property defaults to
/// #F59E0B, so the fallback must BE that value — these constants are the
/// single spelling of the default, replacing hand-copied literals (of which
/// the accent copies drifted to #FFCD85 and the clock/button copies to a
/// dark red, so an invalid hex no longer matched the declared default).
/// </summary>
internal static class WidgetPalette
{
    /// <summary>The shared amber accent — the default of every AccentColorHex /
    /// ButtonColorHex property, and the fallback when a color hex is invalid.</summary>
    public static readonly SKColor Accent = new(0xF5, 0x9E, 0x0B);

    /// <summary>The chat-widget background fallback — opaque, matching the
    /// #0F1117 default (the old literal carried a 235 alpha).</summary>
    public static readonly SKColor ChatBackground = new(0x0F, 0x11, 0x17);
}
