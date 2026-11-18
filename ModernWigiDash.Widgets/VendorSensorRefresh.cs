using ModernWigiDash.Hardware.Service;

namespace ModernWigiDash.Widgets;

/// <summary>Categorized failure reason from the last <see cref="VendorSensorRefresh.Refresh"/> pass.</summary>
internal enum VendorRefreshFailure
{
    None,
    NoSensorList,
    MissingSelectedKey,
    InvalidReading,
}

/// <summary>
/// The HWiNFO vendor-sensor refresh core (the ADR-0022 image): the one owner of
/// the reading state machine behind the <see cref="HwinfoWidget"/>. It owns the
/// list-refresh cadence (a 250 ms fast-retry until the first list lands, then
/// a 5 s settle), the sensor-key codec, the 250 ms read throttle, and the
/// empty-pick bounded auto-selection, so "how does the HWiNFO sensor
/// refresh" is one module the widget drives with one call per render tick. It
/// reads through the <see cref="ISensorValues"/> seam (the vendor client's
/// facet), so the cadence/codec/throttle/fallback rule is drivable without a
/// widget, a WPF surface, or the <see cref="VendorService"/> static.
/// </summary>
internal sealed class VendorSensorRefresh(Func<long>? tickProvider = null)
{
    private const string AutoProbeKey = "__auto_probe__";
    private const int AutoProbeMaxCandidates = 16;

    /// <summary>How long a fetched sensor list is trusted before a refetch (the list is stable).</summary>
    private const long SensorListRefreshMs = 5000;

    /// <summary>Minimum gap between vendor reads; allows fast 250ms polling.</summary>
    private const long ReadingRefreshMs = 250;
    private readonly Func<long> _tick = tickProvider ?? (() => Environment.TickCount64);

    private VendorSensorItem[]? _sensorList;
    // A stale sentinel (before any tick), so the first call always fetches the
    // list instead of waiting out the fast-retry cadence from a zeroed clock.
    private long _sensorListAt = -SensorListRefreshMs;
    private long _offlineProbeAt = -ReadingRefreshMs;
    private long _readingAt;
    private string? _readingKey;
    private string? _resolvedAutoKey;
    private SensorReading? _lastReading;

    /// <summary>Reason the most recent refresh yielded no live reading (or <see cref="VendorRefreshFailure.None"/>).</summary>
    public VendorRefreshFailure FailureReason { get; private set; } = VendorRefreshFailure.NoSensorList;

    /// <summary>
    /// Runs the refresh state machine (cadence, codec, throttle, fallback) for
    /// the given sensor key and returns the current reading, or null when none
    /// is held (an empty list, an unknown key, or a failed read). A throttled
    /// call returns the last held reading unchanged.
    /// </summary>
    public SensorReading? Refresh(ISensorValues sensors, string sensorKey)
    {
        long now = _tick();

        if (sensors.Status != VendorSensorStatus.Ready)
        {
            _sensorList = null;
            _sensorListAt = -SensorListRefreshMs;
            _readingKey = null;
            _resolvedAutoKey = null;
            _lastReading = null;
            FailureReason = VendorRefreshFailure.NoSensorList;
            if (now - _offlineProbeAt >= ReadingRefreshMs)
            {
                _ = sensors.GetSensorList();
                _offlineProbeAt = now;
            }
            return null;
        }

        // Retry fast until the first list lands (the client serves it from a
        // background cache and schedules the fetch, so a miss is cheap); once a
        // list is held, settle to the 5 s cadence.
        long listCadence = _sensorList is { Length: > 0 } ? SensorListRefreshMs : ReadingRefreshMs;
        if (now - _sensorListAt >= listCadence)
        {
            _sensorList = sensors.GetSensorList()?.ToArray();
            _sensorListAt = now;
        }

        if (_sensorList is not { Length: > 0 })
        {
            _resolvedAutoKey = null;
            _lastReading = null;
            FailureReason = VendorRefreshFailure.NoSensorList;
            return null;
        }

        if (!TryParseKey(sensorKey, out int readingType, out int sensorId1, out int sensorId2))
            return RefreshAutomatic(sensors, now);

        _resolvedAutoKey = null;
        var sensor = Array.Find(_sensorList, s =>
            s.ReadingType == readingType && s.SensorId1 == sensorId1 && s.SensorId2 == sensorId2);
        if (sensor is null)
        {
            _lastReading = null;
            FailureReason = VendorRefreshFailure.MissingSelectedKey;
            return null;
        }

        return ReadSingleSensor(sensors, now, sensorKey, sensor, readingType, sensorId1, sensorId2, isAuto: false);
    }

    private SensorReading? RefreshAutomatic(ISensorValues sensors, long now)
    {
        if (_resolvedAutoKey is not null &&
            TryParseKey(_resolvedAutoKey, out int autoRt, out int autoId1, out int autoId2))
        {
            var settled = Array.Find(_sensorList!, s =>
                s.ReadingType == autoRt && s.SensorId1 == autoId1 && s.SensorId2 == autoId2);
            if (settled is not null)
                return ReadSingleSensor(sensors, now, _resolvedAutoKey, settled, autoRt, autoId1, autoId2, isAuto: true);

            _resolvedAutoKey = null;
        }

        if (now - _readingAt < ReadingRefreshMs && string.Equals(_readingKey, AutoProbeKey, StringComparison.Ordinal))
        {
            FailureReason = _lastReading is not null ? VendorRefreshFailure.None : VendorRefreshFailure.InvalidReading;
            return _lastReading;
        }

        int count = Math.Min(_sensorList!.Length, AutoProbeMaxCandidates);
        for (int i = 0; i < count; i++)
        {
            var candidate = _sensorList[i];
            var reading = sensors.GetSensorValue(candidate.ReadingType, candidate.SensorId1, candidate.SensorId2);
            if (reading is { IsValid: true })
            {
                string key = ComposeKey(candidate.ReadingType, candidate.SensorId1, candidate.SensorId2);
                _resolvedAutoKey = key;
                _readingAt = now;
                _readingKey = key;
                _lastReading = new SensorReading(candidate.Name ?? string.Empty, candidate.Unit ?? string.Empty, reading.Value);
                FailureReason = VendorRefreshFailure.None;
                return _lastReading;
            }
        }

        _readingAt = now;
        _readingKey = AutoProbeKey;
        _lastReading = null;
        FailureReason = VendorRefreshFailure.InvalidReading;
        return null;
    }

    private SensorReading? ReadSingleSensor(
        ISensorValues sensors,
        long now,
        string key,
        VendorSensorItem sensor,
        int readingType,
        int sensorId1,
        int sensorId2,
        bool isAuto)
    {
        if (now - _readingAt < ReadingRefreshMs && string.Equals(_readingKey, key, StringComparison.Ordinal))
        {
            FailureReason = _lastReading is not null ? VendorRefreshFailure.None : VendorRefreshFailure.InvalidReading;
            return _lastReading;
        }

        _readingAt = now;
        _readingKey = key;

        var reading = sensors.GetSensorValue(readingType, sensorId1, sensorId2);
        if (reading is null || !reading.IsValid)
        {
            if (isAuto)
                _resolvedAutoKey = null;
            _lastReading = null;
            FailureReason = VendorRefreshFailure.InvalidReading;
            return null;
        }

        _lastReading = new SensorReading(sensor.Name ?? string.Empty, sensor.Unit ?? string.Empty, reading.Value);
        FailureReason = VendorRefreshFailure.None;
        return _lastReading;
    }

    /// <summary>The stable key for a vendor sensor: reading type + both ids.</summary>
    public static string ComposeKey(int readingType, int sensorId1, int sensorId2)
        => $"{readingType}:{sensorId1}:{sensorId2}";

    /// <summary>True while the last refreshed list held at least one sensor;
    /// drops to false if the service disconnects or returns no sensors so widgets
    /// can detect reconnect transitions.</summary>
    public bool HasSensorList => _sensorList is { Length: > 0 };

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
