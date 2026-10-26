using ModernWigiDash.Hardware.Service;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The HWiNFO vendor-sensor refresh core (the ADR-0022 image): the one owner of
/// the reading state machine behind the <see cref="HwinfoWidget"/>. It owns the
/// list-refresh cadence (a 250 ms fast-retry until the first list lands, then
/// a 5 s settle), the sensor-key codec, the 500 ms read throttle, and the
/// empty-pick fallback to the first sensor, so "how does the HWiNFO sensor
/// refresh" is one module the widget drives with one call per render tick. It
/// reads through the <see cref="ISensorValues"/> seam (the vendor client's
/// facet), so the cadence/codec/throttle/fallback rule is drivable without a
/// widget, a WPF surface, or the <see cref="VendorService"/> static.
/// </summary>
internal sealed class VendorSensorRefresh(Func<long>? tickProvider = null)
{
    /// <summary>How long a fetched sensor list is trusted before a refetch (the list is stable).</summary>
    private const long SensorListRefreshMs = 5000;

    /// <summary>Minimum gap between vendor reads; matches the vendor's own ~1 Hz display cadence.</summary>
    private const long ReadingRefreshMs = 500;

    private readonly Func<long> _tick = tickProvider ?? (() => Environment.TickCount64);

    private VendorSensorItem[]? _sensorList;
    // A stale sentinel (before any tick), so the first call always fetches the
    // list instead of waiting out the fast-retry cadence from a zeroed clock.
    private long _sensorListAt = -SensorListRefreshMs;
    private long _readingAt;
    private string? _readingKey;
    private SensorReading? _lastReading;

    /// <summary>
    /// Runs the refresh state machine (cadence, codec, throttle, fallback) for
    /// the given sensor key and returns the current reading, or null when none
    /// is held (an empty list, an unknown key, or a failed read). A throttled
    /// call returns the last held reading unchanged.
    /// </summary>
    public SensorReading? Refresh(ISensorValues sensors, string sensorKey)
    {
        long now = _tick();

        // Retry fast until the first list lands (the client serves it from a
        // background cache and schedules the fetch, so a miss is cheap); once a
        // list is held, settle to the 5 s cadence.
        long listCadence = _sensorList is { Length: > 0 } ? SensorListRefreshMs : 250;
        if (now - _sensorListAt >= listCadence)
        {
            _sensorList = sensors.GetSensorList()?.ToArray();
            _sensorListAt = now;
        }

        if (_sensorList is not { Length: > 0 })
        {
            _lastReading = null;
            return null;
        }

        // An empty pick falls back to the first sensor, so a freshly placed
        // widget shows a live reading instead of an empty placeholder.
        string key = sensorKey;
        VendorSensorItem? sensor;
        if (!TryParseKey(key, out int readingType, out int sensorId1, out int sensorId2))
        {
            sensor = _sensorList[0];
            readingType = sensor.ReadingType;
            sensorId1 = sensor.SensorId1;
            sensorId2 = sensor.SensorId2;
        }
        else
        {
            sensor = Array.Find(_sensorList, s =>
                s.ReadingType == readingType && s.SensorId1 == sensorId1 && s.SensorId2 == sensorId2);
            if (sensor is null)
            {
                _lastReading = null;
                return null;
            }
        }

        if (now - _readingAt < ReadingRefreshMs && string.Equals(_readingKey, key, StringComparison.Ordinal))
            return _lastReading;

        _readingAt = now;
        _readingKey = key;

        var reading = sensors.GetSensorValue(readingType, sensorId1, sensorId2);
        if (reading is null || !reading.IsValid)
        {
            _lastReading = null;
            return null;
        }

        _lastReading = new SensorReading(sensor.Name, sensor.Unit ?? string.Empty, reading.Value);
        return _lastReading;
    }

    /// <summary>The stable key for a vendor sensor: reading type + both ids.</summary>
    public static string ComposeKey(int readingType, int sensorId1, int sensorId2)
        => $"{readingType}:{sensorId1}:{sensorId2}";

    /// <summary>Parses a key produced by <see cref="ComposeKey"/>; false for an empty/garbage value.</summary>
    public static bool TryParseKey(string? key, out int readingType, out int sensorId1, out int sensorId2)
    {
        readingType = 0;
        sensorId1 = 0;
        sensorId2 = 0;
        if (string.IsNullOrEmpty(key))
            return false;

        var parts = key.Split(':');
        return parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out readingType)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sensorId1)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out sensorId2);
    }
}

/// <summary>One held vendor sensor reading: the display name, its unit (empty when the vendor reports none), and the last value.</summary>
internal sealed record SensorReading(string Name, string Unit, double Value);
