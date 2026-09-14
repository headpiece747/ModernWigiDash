using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Security;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Hardware.Service;

/// <summary>
/// The vendor WCF service consumption seam (ADR-0022): one connection lifecycle
/// owning the BasicHttp channel to the vendor's WigiDashService at localhost:8733,
/// with one narrow read facet (<see cref="ISensorValues"/>) mirroring the vendor's
/// sensor provider. The client owns the channel open/close/reconnect; the facet is a
/// stateless read over the established channel. Tolerant parsing: a malformed or
/// partial response degrades to a named "no data" verdict, never a throw into the
/// render tick. The HWiNFO provider is a SHARED session on the service: the client
/// re-initializes it when a list read comes back empty (another consumer deinit it)
/// and never de-initializes it on exit. Testable without the service via an
/// in-memory fake implementing the facet behind the same seam.
/// </summary>
public sealed class WigiDashServiceClient : IDisposable
{
    private const string EndpointAddress = "http://localhost:8733/WigiDashService/WigiDashWcf/";
    private const string SensorInitTag = "[VENDOR-SERVICE]";

    /// <summary>
    /// The vendor's HWiNFO provider is a SHARED, process-wide session on the
    /// service: any consumer's <c>DeInit</c> tears it down for every other
    /// consumer (the vendor Manager deinits on exit) and <c>GetSensorList</c>
    /// then returns an EMPTY list, not an error. Retry the provider - reconnect
    /// when the channel is missing/dead, re-initialize when it was torn down -
    /// at most this often, so the render tick's probe and list refresh recover
    /// without an init storm.
    /// </summary>
    private const long ProviderRetryIntervalMs = 5000;

    private readonly System.Threading.Lock _gate = new();
    private readonly Func<IWigiDashWcf> _createChannel;
    private readonly TimeProvider _clock;
    private IWigiDashWcf? _channel;
    private ChannelFactory<IWigiDashWcf>? _factory;
    private bool _sensorReady;
    private DateTimeOffset _lastProviderAttemptUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>Lock-free readiness flag (the facet's IsReady is read on the UI thread).</summary>
    private volatile bool _connected;

    // --- Background-refreshed cache: the UI thread never performs WCF I/O ---
    private readonly System.Threading.Lock _cacheLock = new();
    private IReadOnlyList<VendorSensorItem>? _cachedSensors;
    private DateTimeOffset _cachedSensorsAtUtc = DateTimeOffset.MinValue;
    private readonly Dictionary<(int ReadingType, int SensorId1, int SensorId2), VendorSensorReading> _cachedReadings = [];
    private DateTimeOffset _cachedReadingsAtUtc = DateTimeOffset.MinValue;
    private readonly HashSet<(int ReadingType, int SensorId1, int SensorId2)> _requestedKeys = [];
    private int _refreshInFlight;
    private DateTimeOffset _lastRefreshAttemptUtc = DateTimeOffset.MinValue;

    /// <summary>Minimum gap between background refresh attempts (a wedged service backoff).</summary>
    private static readonly TimeSpan RefreshMinInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Deduplicated log for the untrusted-input read failures (the project's
    /// rule 5: an untrusted-input catch emits one line naming the source and
    /// the exception, so a hostile or broken producer is observable).
    /// </summary>
    private readonly DiagLog _readFailLog = new("VENDOR-SERVICE", cadence: 60, logFirst: true);

    /// <summary>How long a cached sensor list is served before a refresh is scheduled.</summary>
    private static readonly TimeSpan SensorListTtl = TimeSpan.FromSeconds(2);

    /// <summary>How long a cached sensor reading is served before a refresh is scheduled.</summary>
    private static readonly TimeSpan SensorValueTtl = TimeSpan.FromMilliseconds(500);

    /// <summary>The HWiNFO sensor read facet.</summary>
    public ISensorValues Sensors { get; }

    /// <summary>Creates the client over the production vendor-service channel.</summary>
    public WigiDashServiceClient()
    {
        _createChannel = CreateProductionChannel;
        _clock = TimeProvider.System;
        Sensors = new SensorValuesFacet(this);
    }

    /// <summary>Test constructor: injects the facet implementation directly.</summary>
    internal WigiDashServiceClient(ISensorValues sensors)
    {
        _createChannel = CreateProductionChannel;
        _clock = TimeProvider.System;
        Sensors = sensors;
    }

    /// <summary>
    /// Test constructor: injects the WCF channel directly, so the client's
    /// shared-provider recovery policy is drivable without the vendor service.
    /// </summary>
    internal WigiDashServiceClient(IWigiDashWcf channel)
    {
        _createChannel = () => channel;
        _clock = TimeProvider.System;
        _channel = channel;
        _sensorReady = true; // the injected channel stands in for a connected service
        _connected = true;
        Sensors = new SensorValuesFacet(this);
    }

    /// <summary>
    /// Test constructor: injects the channel factory and clock, so the whole
    /// reconnect path (open -> fail -> drop -> reopen) is drivable without the
    /// vendor service; a fake clock advances the retry window.
    /// </summary>
    internal WigiDashServiceClient(Func<IWigiDashWcf> createChannel, TimeProvider clock)
    {
        _createChannel = createChannel;
        _clock = clock;
        Sensors = new SensorValuesFacet(this);
    }

    /// <summary>
    /// Attempts to connect to the vendor service and initialize the HWiNFO
    /// provider. Returns true when the channel is open and the provider is
    /// initialized. Never throws (the ADR-0017 pattern). Throttled: a repeat
    /// call within <see cref="ProviderRetryIntervalMs"/> is a no-op, so a widget
    /// can use it as its self-heal probe.
    /// </summary>
    public bool TryConnect()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            EnsureConnectedLocked();
            return _channel != null && _sensorReady;
        }
    }

    /// <summary>
    /// (Re)opens the channel and initializes the provider when the client is not
    /// ready, throttled to one attempt per <see cref="ProviderRetryIntervalMs"/>.
    /// The caller holds <see cref="_gate"/>. A failed leg drops the channel so
    /// the next attempt reopens it, and never throws: the app recovers when the
    /// vendor service starts or restarts after the app (the former one-shot
    /// connect left the widget a placeholder until the next app launch).
    /// </summary>
    internal void EnsureConnectedLocked()
    {
        if (_disposed || (_channel != null && _sensorReady))
            return;

        if (!RetryWindowElapsed())
            return;
        _lastProviderAttemptUtc = _clock.GetUtcNow();

        try
        {
            EnsureChannel();
            // A THROWN transport fault must reach the catch below and drop the
            // channel: the former SafeCall wrapper swallowed it, so a faulted
            // channel was retried in place forever and TryConnect could never
            // reopen one (the doc promised the opposite).
            _sensorReady = _channel!.InitSensorProvider(true, true, true);
            if (!_sensorReady)
            {
                FileLog.Write($"{SensorInitTag} vendor service connected but no providers initialized");
            }

            _connected = _sensorReady;
        }
        catch
        {
            CloseChannelLocked();
        }
    }

    /// <summary>True when the channel is open and at least one provider is ready.</summary>
    public bool IsConnected
    {
        get { lock (_gate) return _channel != null && _sensorReady; }
    }

    internal void EnsureChannel()
    {
        if (_channel != null) return;
        _channel = _createChannel();
    }

    /// <summary>
    /// The production channel factory: a BasicHttp binding whose
    /// MaxReceivedMessageSize must exceed the largest vendor response (the
    /// vendor's own Manager binds at 20 MB; we match it so the unbounded-size
    /// sensor list clears the quota). The factory is kept for disposal.
    /// </summary>
    private IWigiDashWcf CreateProductionChannel()
    {
        var binding = new BasicHttpBinding
        {
            OpenTimeout = TimeSpan.FromSeconds(5),
            CloseTimeout = TimeSpan.FromSeconds(5),
            ReceiveTimeout = TimeSpan.FromSeconds(10),
            SendTimeout = TimeSpan.FromSeconds(10),
            MaxReceivedMessageSize = 20 * 1024 * 1024,
        };
        var endpoint = new EndpointAddress(EndpointAddress);
        _factory = new ChannelFactory<IWigiDashWcf>(binding, endpoint);
        return _factory.CreateChannel();
    }

    /// <summary>
    /// True when at least <see cref="ProviderRetryIntervalMs"/> has elapsed since
    /// the last provider attempt (true before the first attempt).
    /// </summary>
    private bool RetryWindowElapsed()
        => _clock.GetUtcNow() - _lastProviderAttemptUtc >= TimeSpan.FromMilliseconds(ProviderRetryIntervalMs);

    internal bool SafeCall(Func<bool> action)
    {
        try { return action(); }
        catch { return false; }
    }

    /// <summary>
    /// Re-initializes the shared HWiNFO provider when it has been torn down
    /// underneath us. The caller holds <see cref="_gate"/>. Throttled.
    /// </summary>
    internal void ReinitSensorProviderLocked()
    {
        if (_channel == null || _disposed)
            return;
        if (!RetryWindowElapsed())
            return;
        _lastProviderAttemptUtc = _clock.GetUtcNow();
        _sensorReady = SafeCall(() => _channel.InitSensorProvider(true, true, true));
    }

    internal IReadOnlyList<VendorSensorItem>? GetRawSensorList()
    {
        try
        {
            return _channel!.GetSensorList()?.ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readFailLog.Write(() => $"sensor list read failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    internal (double Value, bool IsValid)? GetRawSensorValue(int rt, int id1, int id2)
    {
        try
        {
            var value = _channel!.GetSensorValue(rt, id1, id2, out var isValid);
            return (value, isValid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readFailLog.Write(() => $"sensor value ({rt}:{id1}:{id2}) read failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void CloseChannelLocked()
    {
        if (_channel is System.ServiceModel.ICommunicationObject cc)
        {
            try { cc.Close(TimeSpan.FromSeconds(5)); }
            catch { try { cc.Abort(); } catch { /* best-effort */ } }
        }
        _channel = null;
        try { _factory?.Close(); } catch { try { _factory?.Abort(); } catch { /* best-effort */ } }
        _factory = null;
        _sensorReady = false;
        _connected = false;
    }

    /// <summary>Closes the channel. The vendor's shared provider is left initialized (see the class notes).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Deliberately NO DeInitSensorProvider here: the HWiNFO provider is
            // a shared session on the vendor service, so deinitializing it on
            // our exit would tear it down for the vendor Manager (which does
            // not re-init on demand). We leave the service's provider as it was
            // and re-initialize lazily if someone else deinits it.
            CloseChannelLocked();
        }
    }

    // --- Background cache (no WCF I/O on the caller's thread) ---

    /// <summary>
    /// Schedules a background refresh when a cache entry is stale. The caller
    /// holds <see cref="_cacheLock"/>. Single-flight and rate-limited: a refresh
    /// already running wins, and <see cref="RefreshMinInterval"/> backs a wedged
    /// service off (its calls hold a pool thread for the binding timeout).
    /// </summary>
    private void ScheduleRefreshLocked()
    {
        if (_disposed) return;
        var now = _clock.GetUtcNow();
        if (now - _lastRefreshAttemptUtc < RefreshMinInterval) return;
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0) return;
        _lastRefreshAttemptUtc = now;
        _ = Task.Run(RefreshCache);
    }

    /// <summary>
    /// Performs the vendor I/O off the caller's thread and publishes one
    /// snapshot. Connection state mutates under <see cref="_gate"/> (the same
    /// lock the synchronous connect path uses); the snapshot swaps under a brief
    /// <see cref="_cacheLock"/>, so a reader never waits on I/O. Never throws.
    /// </summary>
    private void RefreshCache()
    {
        IReadOnlyList<VendorSensorItem>? list = null;
        var readings = new Dictionary<(int, int, int), VendorSensorReading>();
        try
        {
            lock (_gate)
            {
                if (_disposed) return;

                EnsureConnectedLocked();
                if (_channel == null) return;

                var raw = GetRawSensorList();
                if (raw == null)
                {
                    CloseChannelLocked();
                    return;
                }

                if (raw.Count == 0)
                {
                    // Empty means the shared provider was torn down; re-init and re-read.
                    ReinitSensorProviderLocked();
                    raw = GetRawSensorList();
                    if (raw == null)
                    {
                        CloseChannelLocked();
                        return;
                    }
                }

                list = ProjectSensors(raw);
                foreach (var key in RequestedKeys())
                {
                    var value = GetRawSensorValue(key.ReadingType, key.SensorId1, key.SensorId2);
                    if (value == null)
                    {
                        CloseChannelLocked();
                        break;
                    }

                    readings[key] = new VendorSensorReading(value.Value.Value, value.Value.IsValid);
                }
            }
        }
        catch
        {
            // Best-effort: a failed refresh publishes null and backs off via the TTL stamps.
        }
        finally
        {
            PublishSnapshot(list, readings);
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private (int ReadingType, int SensorId1, int SensorId2)[] RequestedKeys()
    {
        lock (_cacheLock)
        {
            return [.. _requestedKeys];
        }
    }

    private void PublishSnapshot(
        IReadOnlyList<VendorSensorItem>? list,
        IReadOnlyDictionary<(int, int, int), VendorSensorReading> readings)
    {
        lock (_cacheLock)
        {
            if (_disposed) return;
            var now = _clock.GetUtcNow();

            if (list != null)
            {
                _cachedSensors = list;
            }
            else
            {
                // A failed refresh: drop the readings so the widget degrades to
                // its placeholder instead of showing a stale value.
                _cachedReadings.Clear();
            }

            // Stamp the list attempt either way (the failure backoff).
            _cachedSensorsAtUtc = now;

            if (readings.Count > 0)
            {
                foreach (var kv in readings)
                {
                    _cachedReadings[kv.Key] = kv.Value;
                }
            }

            if (list != null && readings.Count > 0)
            {
                _cachedReadingsAtUtc = now;
            }

            _requestedKeys.Clear();
        }
    }

    private static IReadOnlyList<VendorSensorItem> ProjectSensors(IReadOnlyList<VendorSensorItem> raw)
    {
        var items = new List<VendorSensorItem>(raw.Count);
        foreach (var s in raw)
        {
            // A nil list element (<SensorItem i:nil="true"/>) deserializes to
            // null; projecting it threw an NRE that reached the render tick
            // uncaught and killed the process. Skip nulls.
            if (s is null)
            {
                continue;
            }

            items.Add(new VendorSensorItem(s.Guid, s.Name, s.ReadingType, s.SensorId1, s.SensorId2, s.Type, s.Unit));
        }

        return items;
    }

    // --- Facets ---

    private sealed class SensorValuesFacet(WigiDashServiceClient owner) : ISensorValues
    {
        public bool IsReady => owner._connected;

        public IReadOnlyList<VendorSensorItem>? GetSensorList()
        {
            // Served from the background-refreshed cache: the UI thread never
            // performs WCF I/O. The first read returns null and schedules the
            // first refresh (the widget shows its placeholder for one frame).
            lock (owner._cacheLock)
            {
                if (owner._clock.GetUtcNow() - owner._cachedSensorsAtUtc >= SensorListTtl)
                {
                    owner.ScheduleRefreshLocked();
                }

                return owner._cachedSensors;
            }
        }

        public VendorSensorReading? GetSensorValue(int readingType, int sensorId1, int sensorId2)
        {
            lock (owner._cacheLock)
            {
                var key = (readingType, sensorId1, sensorId2);
                owner._requestedKeys.Add(key);
                bool missing = !owner._cachedReadings.ContainsKey(key);
                if (missing || owner._clock.GetUtcNow() - owner._cachedReadingsAtUtc >= SensorValueTtl)
                {
                    owner.ScheduleRefreshLocked();
                }

                return owner._cachedReadings.TryGetValue(key, out var reading) ? reading : null;
            }
        }
    }

}
