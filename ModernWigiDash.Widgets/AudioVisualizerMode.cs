namespace ModernWigiDash.Widgets;

/// <summary>
/// The visualizer's display styles. <see cref="AudioVisualizerModeParser.Parse"/>
/// is the single string→mode mapping site (the inspector property stays a
/// string); unknown values fall back to <see cref="AudioVisualizerMode.NeonBars"/>
/// — the property default.
/// </summary>
internal enum AudioVisualizerMode
{
    NeonBars,
    Oscilloscope,
    RadialPulse
}

/// <summary>The one parse site behind the widget's style dispatch.</summary>
internal static class AudioVisualizerModeParser
{
    public static AudioVisualizerMode Parse(string? value) => value switch
    {
        "Oscilloscope Wave" => AudioVisualizerMode.Oscilloscope,
        "Radial Pulse" => AudioVisualizerMode.RadialPulse,
        _ => AudioVisualizerMode.NeonBars
    };
}
