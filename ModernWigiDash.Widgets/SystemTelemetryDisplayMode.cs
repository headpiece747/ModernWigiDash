namespace ModernWigiDash.Widgets;

/// <summary>
/// The Hardware Monitor widget's display styles (public — the presentation
/// record exposes the resolved mode). <see cref="SystemTelemetryDisplayModeParser.Parse"/>
/// is the single string→mode mapping site (the inspector property stays a
/// string); unknown values fall back to <see cref="SystemTelemetryDisplayMode.Gauge"/>
/// — the property default.
/// </summary>
public enum SystemTelemetryDisplayMode
{
    Gauge,
    Bar,
    Value,
    Graph
}

/// <summary>The one parse site behind the widget's mode dispatch.</summary>
internal static class SystemTelemetryDisplayModeParser
{
    public static SystemTelemetryDisplayMode Parse(string? value) => value switch
    {
        "Bar" => SystemTelemetryDisplayMode.Bar,
        "Value" => SystemTelemetryDisplayMode.Value,
        "Graph" => SystemTelemetryDisplayMode.Graph,
        _ => SystemTelemetryDisplayMode.Gauge
    };
}
