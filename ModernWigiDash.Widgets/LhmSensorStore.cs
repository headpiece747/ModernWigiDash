namespace ModernWigiDash.Widgets;

/// <summary>
/// A single hardware sensor reading collected by the service via LibreHardwareMonitor.
/// </summary>
public sealed record LhmReading(
    string SensorId,
    string SensorName,
    string Label,
    string Unit,
    double Value,
    double Min,
    double Max,
    double Avg);

/// <summary>
/// A point-in-time snapshot of the live hardware sensor set.
/// </summary>
public sealed record LhmSnapshot(bool IsConnected, DateTime LastUpdate, IReadOnlyList<LhmReading> Readings)
{
    public static LhmSnapshot Disconnected() => new(false, DateTime.MinValue, Array.Empty<LhmReading>());
}

/// <summary>
/// In-process cache of the latest hardware sensor snapshot fetched from the
/// service over WCF. The App's polling loop calls <see cref="Update"/>; widgets
/// read the cached snapshot on the render thread without touching WCF.
/// </summary>
public static class LhmSensorStore
{
    private static readonly object Gate = new();
    private static LhmSnapshot _current = LhmSnapshot.Disconnected();

    public static LhmSnapshot ReadSnapshot()
    {
        lock (Gate)
        {
            return _current;
        }
    }

    public static void Update(LhmSnapshot snapshot)
    {
        lock (Gate)
        {
            _current = snapshot;
        }
    }
}
