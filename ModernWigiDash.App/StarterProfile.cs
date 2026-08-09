using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// Builds the default 6-page starter profile. The placement table is pure,
/// testable data (page names, plugin ids, coordinates); <see cref="Create"/>
/// rehydrates it through the loader the same way the window used to place
/// widgets by hand.
/// </summary>
public sealed class StarterProfile
{
    /// <summary>One default widget placement on a named page.</summary>
    /// <param name="PageName">Target page (see <see cref="PageNames"/>).</param>
    /// <param name="PluginId">Widget plugin id from the catalog.</param>
    /// <param name="X">Left edge on the 1016x592 canvas.</param>
    /// <param name="Y">Top edge on the 1016x592 canvas.</param>
    /// <param name="Width">Width in pixels (&gt; 0).</param>
    /// <param name="Height">Height in pixels (&gt; 0).</param>
    public sealed record Placement(string PageName, string PluginId, float X, float Y, float Width, float Height);

    public static IReadOnlyList<string> PageNames { get; } =
        ["Main Dashboard", "Now Playing", "Weather Forecast", "Twitch & Picture", "Hardware Monitor", "FPS / Frame Time"];

    public static IReadOnlyList<Placement> Placements { get; } =
    [
        // ── Page 1: Main Dashboard ──
        new("Main Dashboard", "clock_modern", 0, 0, 406, 148),
        new("Main Dashboard", "weather_forecast", 0, 148, 406, 148),
        new("Main Dashboard", "audio_visualizer", 0, 296, 1016, 296),
        new("Main Dashboard", "frame_time", 406, 0, 406, 148),
        new("Main Dashboard", "ticker_stock", 406, 148, 203, 148),
        new("Main Dashboard", "text_label", 610, 148, 203, 148),
        new("Main Dashboard", "hotkey_button", 813, 0, 203, 148),
        new("Main Dashboard", "stopwatch_timer", 813, 148, 203, 148),

        // ── Page 2: Now Playing ──
        new("Now Playing", "now_playing", 0, 0, 1016, 592),

        // ── Page 3: Weather Forecast ──
        new("Weather Forecast", "weather_forecast", 0, 0, 1016, 592),

        // ── Page 4: Twitch & Picture ──
        new("Twitch & Picture", "twitch_chat", 0, 0, 406, 592),
        new("Twitch & Picture", "picture_viewer", 406, 0, 610, 592),

        // ── Page 5: Hardware Monitor (2x2 sensor dashboard) ──
        new("Hardware Monitor", "hardware_monitor", 0, 0, 508, 296),
        new("Hardware Monitor", "hardware_monitor", 508, 0, 508, 296),
        new("Hardware Monitor", "hardware_monitor", 0, 296, 508, 296),
        new("Hardware Monitor", "hardware_monitor", 508, 296, 508, 296),

        // ── Page 6: FPS / Frame Time (full-screen hero) ──
        new("FPS / Frame Time", "frame_time", 0, 0, 1016, 592)
    ];

    private readonly WidgetPluginLoader _loader;
    private readonly IModernWigiDashContext _context;

    public StarterProfile(WidgetPluginLoader loader, IModernWigiDashContext context)
    {
        _loader = loader;
        _context = context;
    }

    /// <summary>
    /// Builds the default starter <see cref="ProfileLayout"/>: six named pages
    /// with every <see cref="Placements"/> entry rehydrated into a placed
    /// widget, active page reset to the first. Unknown plugin ids are skipped
    /// (same as the old window placement path).
    /// </summary>
    public ProfileLayout Create()
    {
        var profile = new ProfileLayout();
        foreach (var pageName in PageNames.Skip(1))
        {
            profile.Pages.Add(new PageLayout { PageName = pageName });
        }

        for (int pageIndex = 0; pageIndex < PageNames.Count; pageIndex++)
        {
            profile.ActivePageIndex = pageIndex;
            foreach (var placement in Placements)
            {
                if (placement.PageName != PageNames[pageIndex]) continue;
                ProfileOps.PlaceWidget(
                    profile, _loader, _context,
                    placement.PluginId, placement.X, placement.Y,
                    placement.Width, placement.Height);
            }
        }

        profile.ActivePageIndex = 0;
        return profile;
    }
}
