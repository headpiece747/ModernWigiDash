using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// Defines the default 6-page starter profile. The layout spec is pure,
/// immutable data (<see cref="Layout"/>): page names, plugin ids,
/// coordinates. <see cref="Create"/> rehydrates it through the plugin loader
/// the same way the window used to place widgets by hand. Tests and tooling
/// can read <see cref="Layout"/> without instantiating widgets (no network,
/// timer, or audio-capture side effects).
/// </summary>
public sealed class StarterProfile
{
    /// <summary>One default widget placement on the 1016x592 canvas.</summary>
    /// <param name="PluginId">Widget plugin id from the catalog.</param>
    /// <param name="X">Left edge on the 1016x592 canvas.</param>
    /// <param name="Y">Top edge on the 1016x592 canvas.</param>
    /// <param name="Width">Width in pixels (&gt; 0).</param>
    /// <param name="Height">Height in pixels (&gt; 0).</param>
    public sealed record StarterPlacement(string PluginId, float X, float Y, float Width, float Height);

    /// <summary>One named starter page holding its widget placements.</summary>
    /// <param name="Name">Page name.</param>
    /// <param name="Placements">Widget placements on the page.</param>
    public sealed record StarterPage(string Name, IReadOnlyList<StarterPlacement> Placements);

    public static IReadOnlyList<StarterPage> Layout { get; } =
    [
        // ── Page 1: Main Dashboard ──
        new StarterPage("Main Dashboard",
        [
            new StarterPlacement("clock_modern", 0, 0, 406, 148),
            new StarterPlacement("weather_forecast", 0, 148, 406, 148),
            new StarterPlacement("audio_visualizer", 0, 296, 1016, 296),
            new StarterPlacement("frame_time", 406, 0, 406, 148),
            new StarterPlacement("ticker_stock", 406, 148, 203, 148),
            new StarterPlacement("text_label", 610, 148, 203, 148),
            new StarterPlacement("hotkey_button", 813, 0, 203, 148),
            new StarterPlacement("stopwatch_timer", 813, 148, 203, 148)
        ]),

        // ── Page 2: Now Playing ──
        new StarterPage("Now Playing",
        [
            new StarterPlacement("now_playing", 0, 0, 1016, 592)
        ]),

        // ── Page 3: Weather Forecast ──
        new StarterPage("Weather Forecast",
        [
            new StarterPlacement("weather_forecast", 0, 0, 1016, 592)
        ]),

        // ── Page 4: Twitch & Picture ──
        new StarterPage("Twitch & Picture",
        [
            new StarterPlacement("twitch_chat", 0, 0, 406, 592),
            new StarterPlacement("picture_viewer", 406, 0, 610, 592)
        ]),

        // ── Page 5: Hardware Monitor (2x2 sensor dashboard) ──
        new StarterPage("Hardware Monitor",
        [
            new StarterPlacement("hardware_monitor", 0, 0, 508, 296),
            new StarterPlacement("hardware_monitor", 508, 0, 508, 296),
            new StarterPlacement("hardware_monitor", 0, 296, 508, 296),
            new StarterPlacement("hardware_monitor", 508, 296, 508, 296)
        ]),

        // ── Page 6: FPS / Frame Time (full-screen hero) ──
        new StarterPage("FPS / Frame Time",
        [
            new StarterPlacement("frame_time", 0, 0, 1016, 592)
        ])
    ];

    private readonly WidgetPluginLoader _loader;
    private readonly IModernWigiDashContext _context;

    public StarterProfile(WidgetPluginLoader loader, IModernWigiDashContext context)
    {
        _loader = loader;
        _context = context;
    }

    /// <summary>
    /// Builds the default starter <see cref="ProfileLayout"/>: every
    /// <see cref="Layout"/> page (including the first — "Main Dashboard" is an
    /// explicit spec entry, not the <see cref="ProfileLayout"/> ctor's default
    /// page riding along) is materialized by name, every placement rehydrated
    /// into a placed widget, active page reset to the first. Unknown plugin
    /// ids are skipped (same as the old window placement path).
    /// </summary>
    public ProfileLayout Create()
    {
        var profile = new ProfileLayout();
        // The ctor creates one default page; replace it with the explicit
        // starter list so the layout spec is the single source of page truth.
        profile.Pages.Clear();
        foreach (var page in Layout)
        {
            profile.Pages.Add(new PageLayout { PageName = page.Name });
        }

        for (int pageIndex = 0; pageIndex < Layout.Count; pageIndex++)
        {
            profile.ActivePageIndex = pageIndex;
            foreach (var placement in Layout[pageIndex].Placements)
            {
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
