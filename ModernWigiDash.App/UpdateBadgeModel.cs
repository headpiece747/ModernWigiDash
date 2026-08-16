namespace ModernWigiDash.App;

/// <summary>
/// The update button's state → presentation mapping: which Griddy icon, which
/// RGB brush, and whether the button is visible per <see cref="UpdateState"/>.
/// Mirrors the UsbBadgeModel pattern — the window keeps only the element
/// writes, so the table is assertable without WPF.
/// </summary>
internal sealed record UpdateBadgeModel(string IconName, byte Red, byte Green, byte Blue, bool IsVisible)
{
    public static UpdateBadgeModel From(UpdateState state) => state switch
    {
        UpdateState.Available => new("arrow-circle-down", 245, 158, 11, true), // amber
        UpdateState.Downloading => new("swap-horizontal", 250, 250, 250, true), // white
        UpdateState.Ready => new("refresh", 16, 185, 129, true), // green
        _ => new("", 250, 250, 250, false),
    };
}
