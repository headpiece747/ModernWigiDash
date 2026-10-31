namespace ModernWigiDash.App;

/// <summary>
/// The settings hub's display facts and selection verdict (App): the three
/// group headers, the close-behavior radio entries (the one spelling of each
/// entry's label and description the dialog draws), and the checked verdict
/// for a raw persisted value. The verdict routes through
/// <see cref="CloseBehaviorPolicy"/>, so an absent or hand-edited value
/// always lands on the default radio instead of a mystery selection. Pure
/// and assertable without WPF; the dialog builds its rows from this model
/// the way <see cref="Dialogs.ThemeDialog"/> builds its rows from
/// <see cref="Dialogs.ThemeDraft"/>.
/// </summary>
internal sealed class SettingsModel
{
    /// <summary>One settings-group header: the display title and the hint
    /// line under it.</summary>
    public sealed record Group(string Title, string Description);

    /// <summary>One close-behavior radio entry: the policy value (the exact
    /// spelling committed to the profile) plus the display label and hint.
    /// The value is a <see cref="CloseBehaviorPolicy"/> constant, never a
    /// display string the dialog would have to re-map.</summary>
    public sealed record BehaviorOption(string Value, string Label, string Description);

    /// <summary>The hub's three groups in display order. The dialog builds
    /// its sections against this order (pinned by
    /// SettingsModelTests.Groups_AreAppearanceBehaviorProfile_InOrder).</summary>
    public static readonly IReadOnlyList<Group> Groups =
    [
        new("Appearance", "The chrome palette outside the widget canvas."),
        new("Behavior", "How the app reacts when the window is closed or minimized."),
        new("Profile", "Travel the display layout (and the theme) as a JSON file.")
    ];

    /// <summary>The close-behavior entries in display order: the pre-feature
    /// exit first, then the tray keep-alive.</summary>
    public static readonly IReadOnlyList<BehaviorOption> CloseBehaviors =
    [
new(
            CloseBehaviorPolicy.Quit,
            "Quit when the window closes",
            "Closing, minimizing, or Alt+F4 exits the app."),
        new(
            CloseBehaviorPolicy.HideToTray,
            "Keep running in the tray when the window closes",
            "Hides to the tray; only the tray's Quit exits.")
    ];

    /// <summary>The value the checked radio carries for a raw persisted
    /// value: routed through <see cref="CloseBehaviorPolicy.Resolve"/>, so
    /// null, whitespace, or a hand-edited unknown value lands on the
    /// default radio.</summary>
    public string CheckedCloseBehaviorFor(string? persisted)
        => CloseBehaviors.Single(o => string.Equals(o.Value, CloseBehaviorPolicy.Resolve(persisted), StringComparison.Ordinal)).Value;

    /// <summary>One telemetry polling rate option: the millisecond interval plus display label and description.</summary>
    public sealed record PollingRateOption(int Milliseconds, string Label, string Description);

    /// <summary>The supported telemetry polling rates.</summary>
    public static readonly IReadOnlyList<PollingRateOption> TelemetryPollingRates =
    [
        new(250, "Fast (250 ms)", "Updates 4 times per second. Highest responsiveness, slightly higher background CPU usage."),
        new(500, "Responsive (500 ms, Recommended)", "Updates 2 times per second (default). Balanced for smooth real-time monitoring."),
        new(1000, "Standard (1000 ms)", "Updates once per second."),
        new(2000, "Eco (2000 ms)", "Updates every 2 seconds. Lowest background CPU and system overhead.")
    ];

    /// <summary>
    /// Resolves a persisted millisecond value to a valid polling rate option, falling back to 500ms default.
    /// </summary>
    public static int ResolveTelemetryPollingRate(int persistedMs)
        => TelemetryPollingRates.Any(o => o.Milliseconds == persistedMs) ? persistedMs : 500;
}
