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

    private sealed class ChannelSlot
    {
        public IWigiDashWcf? Channel;
        public ChannelFactory<IWigiDashWcf>? Factory;
    }

    private readonly System.Threading.Lock _gate = new();
    private readonly System.Threading.Lock _connectLock = new();
    private readonly System.Threading.Lock _logGate = new();
    private readonly Func<(IWigiDashWcf Channel, ChannelFactory<IWigiDashWcf>? Factory)> _createChannel;
    private readonly TimeProvider _clock;
    private readonly ChannelSlot _sensorSlot = new();
    private readonly ChannelSlot _aidaSlot = new();
    private volatile bool _sensorReady;
    private volatile VendorSensorStatus _status = VendorSensorStatus.Disconnected;
    private DateTimeOffset _lastConnectAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReinitAttemptUtc = DateTimeOffset.MinValue;
    private readonly LogOnChange _stateLog = new();
    private bool _hadStateIssue;
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
    private TimeSpan _refreshMinInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Deduplicated log for transient per-key and AIDA64 WCF read failures (the
    /// project's rule 5: an untrusted-input catch emits one line naming the source
    /// and exception, so a hostile or broken producer is observable).
    /// </summary>
    private readonly DiagLog _readFailLog = new("VENDOR-SERVICE", cadence: 60, logFirst: true);

    /// <summary>How long a cached sensor list is served before a refresh is scheduled.</summary>
    private static readonly TimeSpan SensorListTtl = TimeSpan.FromSeconds(2);

    /// <summary>How long a cached sensor reading is served before a refresh is scheduled.</summary>
    private TimeSpan _sensorValueTtl = TimeSpan.FromMilliseconds(250);

    /// <summary>The HWiNFO sensor read facet.</summary>
    public ISensorValues Sensors { get; }

    /// <summary>Creates the client over the production vendor-service channel.</summary>
    public WigiDashServiceClient()
    {
        _createChannel = static () => CreateProductionChannel();
        _clock = TimeProvider.System;
        Sensors = new SensorValuesFacet(this);
    }

    /// <summary>Test constructor: injects the facet implementation directly.</summary>
    internal WigiDashServiceClient(ISensorValues sensors)
    {
        _createChannel = static () => CreateProductionChannel();
        _clock = TimeProvider.System;
        Sensors = sensors;
    }

    /// <summary>
    /// Test constructor: injects the WCF channel directly, so the client's
    /// shared-provider recovery policy is drivable without the vendor service.
    /// </summary>
    internal WigiDashServiceClient(IWigiDashWcf channel)
    {
        _createChannel = () => (channel, null);
        _clock = TimeProvider.System;
        _sensorSlot.Channel = channel;
        _aidaSlot.Channel = channel;
        _sensorReady = true; // the injected channel stands in for a connected service
        _status = VendorSensorStatus.Ready;
        Sensors = new SensorValuesFacet(this);
    }

    /// <summary>
    /// Test constructor: injects the channel factory and clock, so the whole
    /// reconnect path (open -> fail -> drop -> reopen) is drivable without the
    /// vendor service; a fake clock advances the retry window.
    /// </summary>
    internal WigiDashServiceClient(Func<IWigiDashWcf> createChannel, TimeProvider clock)
    {
        _createChannel = () => (createChannel(), null);
        _clock = clock;
        Sensors = new SensorValuesFacet(this);
    }

    /// <summary>
    /// Updates how frequently cached HWiNFO sensor readings are refreshed from the
    /// vendor service (clamped to [150 ms, 2000 ms]; background list wakeup cap stays at most 250 ms).
    /// </summary>
    public void SetPollingPeriod(TimeSpan period)
    {
        if (_disposed) return;
        double ms = Math.Clamp(period.TotalMilliseconds, 150.0, 2000.0);
        var clamped = TimeSpan.FromMilliseconds(ms);
        var refreshMin = TimeSpan.FromMilliseconds(Math.Min(ms, 250.0));
        lock (_cacheLock)
        {
            _sensorValueTtl = clamped;
            _refreshMinInterval = refreshMin;
        }
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
        _ = EnsureConnected();
        lock (_gate)
        {
            return !_disposed && _sensorSlot.Channel != null && _sensorReady;
        }
    }

    /// <summary>
    /// (Re)opens the HWiNFO channel and initializes the provider when the client is not
    /// ready, throttled to one attempt per <see cref="ProviderRetryIntervalMs"/>.
    /// Never holds <see cref="_gate"/> across WCF I/O so status checks and AIDA64
    /// calls do not stall behind a wedged HWiNFO connect.
    /// </summary>
    internal (IWigiDashWcf? Channel, bool InitializedThisCycle) EnsureConnected()
    {
        lock (_connectLock)
        {
            IWigiDashWcf? channel = null;
            IWigiDashWcf? orphanChannel = null;
            ChannelFactory<IWigiDashWcf>? orphanFactory = null;
            lock (_gate)
            {
                if (_disposed)
                    return (null, false);
                if (_sensorSlot.Channel != null && _sensorReady)
                    return (_sensorSlot.Channel, false);

                if (!ConnectRetryWindowElapsed())
                    return (_sensorSlot.Channel, false);
                _lastConnectAttemptUtc = _clock.GetUtcNow();

                try
                {
                    EnsureChannelLocked(_sensorSlot);
                    channel = _sensorSlot.Channel;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _status = VendorSensorStatus.Disconnected;
                    LogStateIssue($"vendor channel open failed: {ex.GetType().Name}: {ex.Message}");
                    DetachSlotLocked(_sensorSlot, out orphanChannel, out orphanFactory);
                }
            }

            if (orphanChannel != null || orphanFactory != null)
            {
                AbortSafely(orphanChannel, orphanFactory);
                return (null, true);
            }

            if (channel is null) return (null, true);
            InitSensorProviderCore(channel, logNotReady: true);
            lock (_gate)
            {
                return (_disposed ? null : _sensorSlot.Channel, true);
            }
        }
    }

    /// <summary>True when the HWiNFO channel is open and the sensor provider is initialized.</summary>
    public bool IsConnected
    {
        get { lock (_gate) return !_disposed && _sensorSlot.Channel != null && _sensorReady; }
    }

    private void EnsureChannelLocked(ChannelSlot slot)
    {
        if (_disposed || slot.Channel != null) return;
        var (channel, factory) = _createChannel();
        slot.Channel = channel;
        slot.Factory = factory;
    }

    /// <summary>
    /// The production channel factory: a BasicHttp binding whose
    /// MaxReceivedMessageSize must exceed the largest vendor response (the
    /// vendor's own Manager binds at 20 MB; we match it so the unbounded-size
    /// sensor list clears the quota). The factory is returned so each channel slot owns disposal.
    /// </summary>
    internal static (IWigiDashWcf Channel, ChannelFactory<IWigiDashWcf> Factory) CreateProductionChannel(
        string endpointAddress = EndpointAddress)
    {
        var binding = new BasicHttpBinding
        {
            OpenTimeout = TimeSpan.FromSeconds(5),
            CloseTimeout = TimeSpan.FromSeconds(5),
            ReceiveTimeout = TimeSpan.FromSeconds(10),
            SendTimeout = TimeSpan.FromSeconds(10),
            MaxBufferSize = 20 * 1024 * 1024,
            MaxReceivedMessageSize = 20 * 1024 * 1024,
            UseDefaultWebProxy = false,
            ReaderQuotas =
            {
                MaxBytesPerRead = 65536,
                MaxStringContentLength = 65536,
                MaxArrayLength = 1048576,
                MaxNameTableCharCount = 65536,
            },
        };
        var endpoint = new EndpointAddress(endpointAddress);
        var factory = new ChannelFactory<IWigiDashWcf>(binding, endpoint);
        try
        {
            var channel = factory.CreateChannel();
            return (channel, factory);
        }
        catch
        {
            try { factory.Abort(); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>
    /// True when at least <see cref="ProviderRetryIntervalMs"/> has elapsed since
    /// the last connection/init attempt (true before the first attempt).
    /// </summary>
    private bool ConnectRetryWindowElapsed()
        => _clock.GetUtcNow() - _lastConnectAttemptUtc >= TimeSpan.FromMilliseconds(ProviderRetryIntervalMs);

    /// <summary>
    /// True when at least <see cref="ProviderRetryIntervalMs"/> has elapsed since
    /// the last empty-catalog re-init attempt (true before the first attempt).
    /// </summary>
    private bool ReinitRetryWindowElapsed()
        => _clock.GetUtcNow() - _lastReinitAttemptUtc >= TimeSpan.FromMilliseconds(ProviderRetryIntervalMs);

    /// <summary>
    /// Re-initializes the shared HWiNFO provider when it has been torn down
    /// underneath us. Serializes with <see cref="_connectLock"/> and runs WCF I/O
    /// outside <see cref="_gate"/>. Throttled on its own window so initial
    /// connect never blocks immediate recovery when <c>GetSensorList</c> is empty.
    /// </summary>
    internal bool ReinitSensorProvider(IWigiDashWcf channel)
    {
        lock (_connectLock)
        {
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_sensorSlot.Channel, channel))
                    return false;
                if (!ReinitRetryWindowElapsed())
                    return false;
                _lastReinitAttemptUtc = _clock.GetUtcNow();
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
            LogStateIssue($"sensor provider init failed: {ex.GetType().Name}: {ex.Message}");
            DropFaultedChannel(_sensorSlot, channel);
            return false;
        }

        IWigiDashWcf? orphanChannel = null;
        ChannelFactory<IWigiDashWcf>? orphanFactory = null;
        lock (_gate)
        {
            if (_disposed)
            {
                if (ReferenceEquals(_sensorSlot.Channel, channel))
                    DetachSlotLocked(_sensorSlot, out orphanChannel, out orphanFactory);
                else
                    orphanChannel = channel;
            }
            else if (ReferenceEquals(_sensorSlot.Channel, channel))
            {
                _sensorReady = ready;
                if (!ready)
                {
                    _status = VendorSensorStatus.ProviderNotInitialized;
                    if (logNotReady)
                    {
                        LogStateIssue("vendor service connected but no providers initialized");
                    }
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
            LogStateIssue($"sensor list read failed: {ex.GetType().Name}: {ex.Message}");
            DropFaultedChannel(_sensorSlot, channel);
            return null;
        }
    }

    internal (VendorSensorReading? Reading, bool TransportFault) GetRawSensorValue(
        IWigiDashWcf channel, int rt, int id1, int id2)
    {
        try
        {
            var value = channel.GetSensorValue(rt, id1, id2, out var isValid);
            return (new VendorSensorReading(value, isValid), false);
        }
        catch (FaultException ex)
        {
            _readFailLog.Write(() => $"sensor value ({rt}:{id1}:{id2}) fault: {ex.GetType().Name}: {ex.Message}");
            return (null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readFailLog.Write(() => $"sensor value ({rt}:{id1}:{id2}) read failed: {ex.GetType().Name}: {ex.Message}");
            DropFaultedChannel(_sensorSlot, channel);
            return (null, true);
        }
    }

    /// <summary>
    /// Ensures the vendor's AIDA64 map provider is initialized over its own
    /// channel slot. Called by the AIDA64 panel master off the UI thread; never throws.
    /// </summary>
    internal bool TryInitAidaProvider()
    {
        IWigiDashWcf? channel = null;
        try
        {
            lock (_gate)
            {
                if (_disposed) return false;
                EnsureChannelLocked(_aidaSlot);
                channel = _aidaSlot.Channel;
            }

            if (channel is null) return false;
            bool ok = channel.InitAidaProvider();
            return !_disposed && ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readFailLog.Write(() => $"AIDA64 provider init failed: {ex.GetType().Name}: {ex.Message}");
            DropFaultedChannel(_aidaSlot, channel);
            return false;
        }
    }

    /// <summary>
    /// Writes the small master-handshake bytes into the vendor's AIDA64 shared
    /// map over its isolated channel slot. Never throws; a logical rejection returns
    /// false cleanly, whereas a transport fault drops only the AIDA64 channel.
    /// </summary>
    internal bool TryWriteAidaMmap(int offset, byte[] buffer)
    {
        IWigiDashWcf? channel;
        lock (_gate)
        {
            if (_disposed || _aidaSlot.Channel is null) return false;
            channel = _aidaSlot.Channel;
        }

        try
        {
            bool ok = channel.WriteAidaMmap(offset, buffer);
            return !_disposed && ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readFailLog.Write(() => $"AIDA64 map write failed at {offset}: {ex.GetType().Name}: {ex.Message}");
            DropFaultedChannel(_aidaSlot, channel);
            return false;
        }
    }

    private void DropFaultedChannel(ChannelSlot slot, IWigiDashWcf? faulted)
    {
        if (faulted is null) return;
        IWigiDashWcf? channel = null;
        ChannelFactory<IWigiDashWcf>? factory = null;
        lock (_gate)
        {
            if (ReferenceEquals(slot.Channel, faulted))
                DetachSlotLocked(slot, out channel, out factory);
        }
        AbortSafely(channel, factory);
    }

    private void DetachSlotLocked(
        ChannelSlot slot,
        out IWigiDashWcf? channel,
        out ChannelFactory<IWigiDashWcf>? factory)
    {
        channel = slot.Channel;
        factory = slot.Factory;
        slot.Channel = null;
        slot.Factory = null;
        if (ReferenceEquals(slot, _sensorSlot))
        {
            _sensorReady = false;
            _status = VendorSensorStatus.Disconnected;
        }
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

    /// <summary>Closes both channel slots. The vendor's shared provider is left initialized (see the class notes).</summary>
    public void Dispose()
    {
        // All vendor WCF I/O runs outside _gate, so taking _gate here is
        // sub-microsecond and atomically prevents EnsureChannelLocked() from
        // publishing a new channel/factory after teardown detaches the old ones.
        // Abort (never Close) runs outside the lock so UI teardown never waits.
        IWigiDashWcf? sensorChannel;
        ChannelFactory<IWigiDashWcf>? sensorFactory;
        IWigiDashWcf? aidaChannel;
        ChannelFactory<IWigiDashWcf>? aidaFactory;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DetachSlotLocked(_sensorSlot, out sensorChannel, out sensorFactory);
            DetachSlotLocked(_aidaSlot, out aidaChannel, out aidaFactory);
        }

        // Deliberately NO DeInitSensorProvider here: the HWiNFO provider is
        // a shared session on the vendor service, so deinitializing it on
        // our exit would tear it down for the vendor Manager (which does
        // not re-init on demand). We leave the service's provider as it was
        // and re-initialize lazily if someone else deinits it.
        AbortSafely(sensorChannel, sensorFactory);
        if (!ReferenceEquals(aidaChannel, sensorChannel) || !ReferenceEquals(aidaFactory, sensorFactory))
        {
            AbortSafely(aidaChannel, aidaFactory);
        }
    }

    private void LogStateIssue(string message)
    {
        bool emit;
        lock (_logGate)
        {
            _hadStateIssue = true;
            emit = _stateLog.Changed(message);
        }
        if (emit) FileLog.Write($"{SensorInitTag} {message}");
    }

    private void LogStateRecovered()
    {
        bool emit = false;
        lock (_logGate)
        {
            if (_hadStateIssue)
            {
                _hadStateIssue = false;
                emit = _stateLog.Changed("recovered");
            }
        }
        if (emit) FileLog.Write($"{SensorInitTag} vendor HWiNFO sensors recovered");
    }

    // --- Background cache (no WCF I/O on the caller's thread) ---

    /// <summary>
    /// Schedules a background refresh when a cache entry is stale. The caller
    /// holds <see cref="_cacheLock"/>. Single-flight and rate-limited: a refresh
    /// already running wins, and <see cref="_refreshMinInterval"/> backs a wedged
    /// service off (its calls hold a pool thread for the binding timeout).
    /// </summary>
    private void ScheduleRefreshLocked()
    {
        if (_disposed) return;
        var now = _clock.GetUtcNow();
        if (now - _lastRefreshAttemptUtc < _refreshMinInterval) return;
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
        HashSet<(int, int, int)>? faultedKeys = null;
        bool failAll = false;
        try
        {
            if (_disposed) return;

            var (channel, initializedThisCycle) = EnsureConnected();
            lock (_gate)
            {
                if (_disposed) return;
                if (channel == null)
                {
                    _status = VendorSensorStatus.Disconnected;
                    failAll = true;
                    return;
                }

                if (!_sensorReady)
                {
                    _status = VendorSensorStatus.ProviderNotInitialized;
                    failAll = true;
                    return;
                }
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
                    failAll = true;
                    return;
                }

                if (raw.Count == 0)
                {
                    // Empty means the shared provider was torn down (or HWiNFO isn't feeding sensors yet).
                    if (!initializedThisCycle && ReinitSensorProvider(channel))
                    {
                        raw = GetRawSensorList(channel);
                        if (raw == null)
                        {
                            failAll = true;
                            return;
                        }
                    }

                    if (raw.Count == 0)
                    {
                        lock (_gate)
                        {
                            if (_sensorSlot.Channel is null)
                                _status = VendorSensorStatus.Disconnected;
                            else if (!_sensorReady)
                                _status = VendorSensorStatus.ProviderNotInitialized;
                            else
                            {
                                _status = VendorSensorStatus.EmptySensorList;
                                LogStateIssue("vendor service returned 0 HWiNFO sensors");
                            }
                        }
                        failAll = true;
                        return;
                    }
                }

                lock (_gate)
                {
                    if (!_disposed && _sensorReady && _sensorSlot.Channel != null)
                        _status = VendorSensorStatus.Ready;
                }
                LogStateRecovered();
                list = ProjectSensors(raw);
            }

            requested = RequestedKeys();
            foreach (var key in requested)
            {
                var (reading, transportFault) = GetRawSensorValue(channel, key.ReadingType, key.SensorId1, key.SensorId2);
                if (transportFault)
                {
                    failAll = true;
                    break;
                }

                if (reading is null)
                {
                    (faultedKeys ??= []).Add(key);
                    continue;
                }

                readings[key] = reading;
            }
        }
        catch (Exception ex)
        {
            failAll = true;
            _readFailLog.Write(() => $"sensor refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            PublishSnapshot(list, requested, readings, faultedKeys, failAll);
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
        HashSet<(int, int, int)>? faultedKeys,
        bool failAll)
    {
        lock (_cacheLock)
        {
            if (_disposed) return;
            var now = _clock.GetUtcNow();

            if (failAll)
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

            if (faultedKeys is { Count: > 0 })
            {
                foreach (var fk in faultedKeys)
                {
                    _cachedReadings.Remove(fk);
                }
            }

            if (readings.Count > 0)
            {
                foreach (var kv in readings)
                {
                    _cachedReadings[kv.Key] = kv.Value;
                }
            }

            if (readings.Count > 0 || faultedKeys is { Count: > 0 })
            {
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

        public VendorSensorStatus Status => owner._disposed ? VendorSensorStatus.Disconnected : owner._status;

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
                if (missing || owner._clock.GetUtcNow() - owner._cachedReadingsAtUtc >= owner._sensorValueTtl)
                {
                    owner.ScheduleRefreshLocked();
                }

                return owner._cachedReadings.TryGetValue(key, out var reading) ? reading : null;
            }
        }
    }

}
