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
    private readonly System.Threading.Lock _connectLock = new();
    private readonly Func<IWigiDashWcf> _createChannel;
    private readonly TimeProvider _clock;
    private IWigiDashWcf? _channel;
    private ChannelFactory<IWigiDashWcf>? _factory;
    private volatile bool _sensorReady;
    private DateTimeOffset _lastProviderAttemptUtc = DateTimeOffset.MinValue;
    private volatile bool _disposed;

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
    private static readonly TimeSpan RefreshMinInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Deduplicated log for the untrusted-input read failures (the project's
    /// rule 5: an untrusted-input catch emits one line naming the source and
    /// the exception, so a hostile or broken producer is observable).
    /// </summary>
    private readonly DiagLog _readFailLog = new("VENDOR-SERVICE", cadence: 60, logFirst: true);

    /// <summary>How long a cached sensor list is served before a refresh is scheduled.</summary>
    private static readonly TimeSpan SensorListTtl = TimeSpan.FromSeconds(2);

    /// <summary>How long a cached sensor reading is served before a refresh is scheduled.</summary>
    private static readonly TimeSpan SensorValueTtl = TimeSpan.FromMilliseconds(250);

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
        if (_disposed) return false;
        EnsureConnected();
        lock (_gate)
        {
            return !_disposed && _channel != null && _sensorReady;
        }
    }

    /// <summary>
    /// (Re)opens the channel and initializes the provider when the client is not
    /// ready, throttled to one attempt per <see cref="ProviderRetryIntervalMs"/>.
    /// Never holds <see cref="_gate"/> across WCF I/O so status checks and AIDA64
    /// calls do not stall behind a wedged HWiNFO connect.
    /// </summary>
    internal void EnsureConnected()
    {
        lock (_connectLock)
        {
            IWigiDashWcf? channel = null;
            try
            {
                lock (_gate)
                {
                    if (_disposed || (_channel != null && _sensorReady))
                        return;

                    if (!RetryWindowElapsed())
                        return;
                    _lastProviderAttemptUtc = _clock.GetUtcNow();

                    EnsureChannelLocked();
                    channel = _channel;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _readFailLog.Write(() => $"vendor channel open failed: {ex.GetType().Name}: {ex.Message}");
                DropChannelUnconditionally();
                return;
            }

            if (channel is null) return;
            InitSensorProviderCore(channel, logNotReady: true);
        }
    }

    /// <summary>True when the channel is open and at least one provider is ready.</summary>
    public bool IsConnected
    {
        get { lock (_gate) return _channel != null && _sensorReady; }
    }

    private void EnsureChannelLocked()
    {
        if (_disposed || _channel != null) return;
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
        var factory = new ChannelFactory<IWigiDashWcf>(binding, endpoint);
        try
        {
            var channel = factory.CreateChannel();
            _factory = factory;
            return channel;
        }
        catch
        {
            try { factory.Abort(); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>
    /// True when at least <see cref="ProviderRetryIntervalMs"/> has elapsed since
    /// the last provider attempt (true before the first attempt).
    /// </summary>
    private bool RetryWindowElapsed()
        => _clock.GetUtcNow() - _lastProviderAttemptUtc >= TimeSpan.FromMilliseconds(ProviderRetryIntervalMs);

    /// <summary>
    /// Re-initializes the shared HWiNFO provider when it has been torn down
    /// underneath us. Serializes with <see cref="_connectLock"/> and runs WCF I/O
    /// outside <see cref="_gate"/>. Throttled.
    /// </summary>
    internal bool ReinitSensorProvider(IWigiDashWcf channel)
    {
        lock (_connectLock)
        {
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_channel, channel))
                    return false;
                if (!RetryWindowElapsed())
                    return false;
                _lastProviderAttemptUtc = _clock.GetUtcNow();
            }

            return InitSensorProviderCore(channel, logNotReady: false);
        }
    }

    private bool InitSensorProviderCore(IWigiDashWcf channel, bool logNotReady)
    {
        bool ready;
        try
        {
            ready = channel.InitSensorProvider(true, true, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readFailLog.Write(() => $"sensor provider init failed: {ex.GetType().Name}: {ex.Message}");
            DropFaultedChannel(channel);
            return false;
        }

        IWigiDashWcf? orphanChannel = null;
        ChannelFactory<IWigiDashWcf>? orphanFactory = null;
        lock (_gate)
        {
            if (_disposed)
            {
                if (ReferenceEquals(_channel, channel))
                    DetachChannelLocked(out orphanChannel, out orphanFactory);
                else
                    orphanChannel = channel;
            }
            else if (ReferenceEquals(_channel, channel))
            {
                _sensorReady = ready;
                if (logNotReady && !ready)
                {
                    FileLog.Write($"{SensorInitTag} vendor service connected but no providers initialized");
                }
                return ready;
            }
        }

        AbortSafely(orphanChannel, orphanFactory);
        return false;
    }

    internal IReadOnlyList<VendorSensorItem>? GetRawSensorList(IWigiDashWcf channel)
    {
        try
        {
            return channel.GetSensorList()?.ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readFailLog.Write(() => $"sensor list read failed: {ex.GetType().Name}: {ex.Message}");
            DropFaultedChannel(channel);
            return null;
        }
    }

    internal (double Value, bool IsValid)? GetRawSensorValue(IWigiDashWcf channel, int rt, int id1, int id2)
    {
        try
        {
            var value = channel.GetSensorValue(rt, id1, id2, out var isValid);
            return (value, isValid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readFailLog.Write(() => $"sensor value ({rt}:{id1}:{id2}) read failed: {ex.GetType().Name}: {ex.Message}");
            DropFaultedChannel(channel);
            return null;
        }
    }

    /// <summary>
    /// Ensures the vendor's AIDA64 map provider is initialized. Called by the
    /// AIDA64 panel master off the UI thread; never throws.
    /// </summary>
    internal bool TryInitAidaProvider()
    {
        IWigiDashWcf? channel = null;
        try
        {
            // The gate covers the channel handoff only. The call itself runs
            // outside it: this gate also serializes the HWiNFO cache refresh,
            // so a wedged AIDA64 call must not hold up the sensor list (the
            // 2026-11-11 freeze report).
            lock (_gate)
            {
                if (_disposed) return false;
                EnsureChannelLocked();
                channel = _channel;
            }

            if (channel is null) return false;
            bool ok = channel.InitAidaProvider();
            return !_disposed && ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readFailLog.Write(() => $"AIDA64 provider init failed: {ex.GetType().Name}: {ex.Message}");
            DropFaultedChannel(channel);
            return false;
        }
    }

    /// <summary>
    /// Writes the small master-handshake bytes into the vendor's AIDA64 shared
    /// map (the header, the widget record, the heartbeat counter, the frame
    /// ack). Never throws; a logical rejection returns false cleanly, whereas a
    /// transport fault drops the channel so the next poll can reconnect.
    /// </summary>
    internal bool TryWriteAidaMmap(int offset, byte[] buffer)
    {
        IWigiDashWcf? channel;
        lock (_gate)
        {
            if (_disposed || _channel is null) return false;
            channel = _channel;
        }

        // Outside the gate: see TryInitAidaProvider (the HWiNFO refresh shares
        // this gate, and a slow AIDA write must not delay the sensor list).
        try
        {
            bool ok = channel.WriteAidaMmap(offset, buffer);
            return !_disposed && ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readFailLog.Write(() => $"AIDA64 map write failed at {offset}: {ex.GetType().Name}: {ex.Message}");
            DropFaultedChannel(channel);
            return false;
        }
    }

    private void DropChannelUnconditionally()
    {
        IWigiDashWcf? channel;
        ChannelFactory<IWigiDashWcf>? factory;
        lock (_gate)
        {
            DetachChannelLocked(out channel, out factory);
        }
        AbortSafely(channel, factory);
    }

    private void DropFaultedChannel(IWigiDashWcf? faulted)
    {
        if (faulted is null) return;
        IWigiDashWcf? channel = null;
        ChannelFactory<IWigiDashWcf>? factory = null;
        lock (_gate)
        {
            if (ReferenceEquals(_channel, faulted))
                DetachChannelLocked(out channel, out factory);
        }
        AbortSafely(channel, factory);
    }

    private void DetachChannelLocked(out IWigiDashWcf? channel, out ChannelFactory<IWigiDashWcf>? factory)
    {
        channel = _channel;
        factory = _factory;
        _channel = null;
        _factory = null;
        _sensorReady = false;
    }

    private void AbortSafely(IWigiDashWcf? channel, ChannelFactory<IWigiDashWcf>? factory)
    {
        // Abort unconditionally: callers only close here on fault/error or teardown,
        // where Close() could spend seconds waiting on a half-dead socket.
        if (channel is ICommunicationObject cc)
        {
            try { cc.Abort(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _readFailLog.Write(() => $"channel abort failed: {ex.GetType().Name}"); }
        }

        if (factory is not null)
        {
            try { factory.Abort(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _readFailLog.Write(() => $"channel factory abort failed: {ex.GetType().Name}"); }
        }
    }

    /// <summary>Closes the channel. The vendor's shared provider is left initialized (see the class notes).</summary>
    public void Dispose()
    {
        // All vendor WCF I/O runs outside _gate, so taking _gate here is
        // sub-microsecond and atomically prevents EnsureChannelLocked() from
        // publishing a new channel/factory after teardown detaches the old ones.
        // Abort (never Close) runs outside the lock so UI teardown never waits.
        IWigiDashWcf? channel;
        ChannelFactory<IWigiDashWcf>? factory;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DetachChannelLocked(out channel, out factory);
        }

        // Deliberately NO DeInitSensorProvider here: the HWiNFO provider is
        // a shared session on the vendor service, so deinitializing it on
        // our exit would tear it down for the vendor Manager (which does
        // not re-init on demand). We leave the service's provider as it was
        // and re-initialize lazily if someone else deinits it.
        AbortSafely(channel, factory);
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
        (int ReadingType, int SensorId1, int SensorId2)[] requested = [];
        var readings = new Dictionary<(int, int, int), VendorSensorReading>();
        bool fetchFailed = false;
        try
        {
            if (_disposed) return;

            EnsureConnected();
            IWigiDashWcf? channel;
            lock (_gate)
            {
                if (_disposed) return;
                channel = _channel;
            }
            if (channel == null)
            {
                fetchFailed = true;
                return;
            }

            var now = _clock.GetUtcNow();
            bool listStale;
            lock (_cacheLock)
            {
                listStale = _cachedSensors == null || now - _cachedSensorsAtUtc >= SensorListTtl;
            }

            if (listStale)
            {
                var raw = GetRawSensorList(channel);
                if (raw == null)
                {
                    fetchFailed = true;
                    return;
                }

                if (raw.Count == 0)
                {
                    // Empty means the shared provider was torn down; re-init and re-read.
                    if (!ReinitSensorProvider(channel))
                    {
                        fetchFailed = true;
                        return;
                    }

                    raw = GetRawSensorList(channel);
                    if (raw == null)
                    {
                        fetchFailed = true;
                        return;
                    }
                }

                list = ProjectSensors(raw);
            }

            requested = RequestedKeys();
            foreach (var key in requested)
            {
                var value = GetRawSensorValue(channel, key.ReadingType, key.SensorId1, key.SensorId2);
                if (value == null)
                {
                    fetchFailed = true;
                    break;
                }

                readings[key] = new VendorSensorReading(value.Value.Value, value.Value.IsValid);
            }
        }
        catch (Exception ex)
        {
            fetchFailed = true;
            _readFailLog.Write(() => $"sensor refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            PublishSnapshot(list, requested, readings, fetchFailed);
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
        (int ReadingType, int SensorId1, int SensorId2)[] requested,
        IReadOnlyDictionary<(int, int, int), VendorSensorReading> readings,
        bool fetchFailed)
    {
        lock (_cacheLock)
        {
            if (_disposed) return;
            var now = _clock.GetUtcNow();

            if (fetchFailed)
            {
                // A failed refresh or lost connection: clear cached sensors and
                // readings regardless of whether list/partial reads succeeded
                // earlier in the same cycle, so widgets degrade to placeholder
                // and refresh once sensors return.
                _cachedSensors = null;
                _cachedReadings.Clear();
                _cachedSensorsAtUtc = now;
                _requestedKeys.Clear();
                return;
            }

            if (list != null)
            {
                _cachedSensors = list;
                _cachedSensorsAtUtc = now;
            }

            if (readings.Count > 0)
            {
                foreach (var kv in readings)
                {
                    _cachedReadings[kv.Key] = kv.Value;
                }
                _cachedReadingsAtUtc = now;
            }

            foreach (var key in requested)
            {
                _requestedKeys.Remove(key);
            }
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

            items.Add(new VendorSensorItem(s.Guid, s.Name ?? string.Empty, s.ReadingType, s.SensorId1, s.SensorId2, s.Type, s.Unit));
        }

        return items;
    }

    // --- Facets ---

    private sealed class SensorValuesFacet(WigiDashServiceClient owner) : ISensorValues
    {
        public bool IsReady => owner._sensorReady;

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
