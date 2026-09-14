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
    /// then returns an EMPTY list, not an error. Re-initialize at most this
    /// often when a list read comes back empty, so a widget recovers without an
    /// init storm on the render tick.
    /// </summary>
    private const long SensorProviderReinitIntervalMs = 5000;

    private readonly System.Threading.Lock _gate = new();
    private IWigiDashWcf? _channel;
    private ChannelFactory<IWigiDashWcf>? _factory;
    private bool _sensorReady;
    private long _lastSensorInitTicks;
    private bool _disposed;

    /// <summary>The HWiNFO sensor read facet.</summary>
    public ISensorValues Sensors { get; }

    /// <summary>Creates the client over the production vendor-service channel.</summary>
    public WigiDashServiceClient()
    {
        Sensors = new SensorValuesFacet(this);
    }

    /// <summary>Test constructor: injects the facet implementation directly.</summary>
    internal WigiDashServiceClient(ISensorValues sensors)
    {
        Sensors = sensors;
    }

    /// <summary>
    /// Test constructor: injects the WCF channel directly, so the client's
    /// shared-provider recovery policy is drivable without the vendor service.
    /// </summary>
    internal WigiDashServiceClient(IWigiDashWcf channel)
    {
        _channel = channel;
        Sensors = new SensorValuesFacet(this);
    }

    /// <summary>
    /// Attempts to connect to the vendor service and initialize both providers.
    /// Returns true when the channel is open and at least one provider initialized.
    /// Never throws: a connection failure degrades to false (the ADR-0017 pattern).
    /// </summary>
    public bool TryConnect()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            try
            {
                EnsureChannel();
                _lastSensorInitTicks = Environment.TickCount64;
                _sensorReady = SafeCall(() => _channel!.InitSensorProvider(true, true, true));
                var ok = _sensorReady;
                if (!ok)
                    FileLog.Write($"{SensorInitTag} vendor service connected but no providers initialized");
                return ok;
            }
            catch
            {
                CloseChannelLocked();
                return false;
            }
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
        // MaxReceivedMessageSize must exceed the largest vendor response. The
        // vendor's own Manager binds at 20 MB; we match it so the unbounded-size
        // sensor list clears the quota.
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
        _channel = _factory.CreateChannel();
    }

    internal bool TryEnsureChannel()
    {
        try
        {
            EnsureChannel();
            return true;
        }
        catch
        {
            CloseChannelLocked();
            return false;
        }
    }

    internal bool Call<T>(Func<T> action, out T result)
    {
        try
        {
            result = action();
            return true;
        }
        catch
        {
            result = default!;
            return false;
        }
    }

    internal bool SafeCall(Func<bool> action)
    {
        try { return action(); }
        catch { return false; }
    }

    internal void RefreshSensorReadiness()
    {
        _sensorReady = SafeCall(() => _channel!.GetSensorInitStatus() > 0);
    }

    /// <summary>
    /// Re-initializes the shared HWiNFO provider when it has been torn down
    /// underneath us. The caller holds <see cref="_gate"/>. Throttled.
    /// </summary>
    internal void ReinitSensorProviderLocked()
    {
        if (_channel == null || _disposed)
            return;
        var now = Environment.TickCount64;
        if (now - _lastSensorInitTicks < SensorProviderReinitIntervalMs)
            return;
        _lastSensorInitTicks = now;
        _sensorReady = SafeCall(() => _channel.InitSensorProvider(true, true, true));
    }

    internal IReadOnlyList<VendorSensorItem>? GetRawSensorList()
    {
        try { return _channel!.GetSensorList()?.ToList(); }
        catch { return null; }
    }

    internal (double Value, bool IsValid)? GetRawSensorValue(int rt, int id1, int id2)
    {
        try
        {
            var value = _channel!.GetSensorValue(rt, id1, id2, out var isValid);
            return (value, isValid);
        }
        catch { return null; }
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

    // --- Facets ---

    private sealed class SensorValuesFacet(WigiDashServiceClient owner) : ISensorValues
    {
        public bool IsReady
        {
            get { lock (owner._gate) return owner._channel != null && owner._sensorReady; }
        }

        public IReadOnlyList<VendorSensorItem>? GetSensorList()
        {
            lock (owner._gate)
            {
                if (owner._channel == null) return null;
                var raw = owner.GetRawSensorList();
                if (raw is { Count: 0 })
                {
                    // Empty means the shared provider was torn down (see
                    // SensorProviderReinitIntervalMs); re-init and re-read.
                    owner.ReinitSensorProviderLocked();
                    raw = owner.GetRawSensorList();
                }
                if (raw == null) return null;
                return raw.Select(s => new VendorSensorItem(s.Guid, s.Name, s.ReadingType, s.SensorId1, s.SensorId2, s.Type, s.Unit)).ToList();
            }
        }

        public VendorSensorReading? GetSensorValue(int readingType, int sensorId1, int sensorId2)
        {
            lock (owner._gate)
            {
                if (owner._channel == null || !owner._sensorReady) return null;
                var r = owner.GetRawSensorValue(readingType, sensorId1, sensorId2);
                return r == null ? null : new VendorSensorReading(r.Value.Value, r.Value.IsValid);
            }
        }
    }

}
