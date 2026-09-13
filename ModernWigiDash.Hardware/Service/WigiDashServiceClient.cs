using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Security;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Hardware.Service;

/// <summary>
/// The vendor WCF service consumption seam (ADR-0022): one connection lifecycle
/// owning the BasicHttp channel to the vendor's WigiDashService at localhost:8733,
/// with two narrow read facets (<see cref="ISensorValues"/>, <see cref="IAidaPanel"/>)
/// mirroring the vendor's own split. The client owns the channel open/close/reconnect;
/// the facets are stateless reads over the established channel. Tolerant parsing: a
/// malformed or partial response degrades to a named "no data" verdict, never a throw
/// into the render tick. Testable without the service via an in-memory fake implementing
/// both facets behind the same seam.
/// </summary>
public sealed class WigiDashServiceClient : IDisposable
{
    private const string EndpointAddress = "http://localhost:8733/WigiDashService/WigiDashWcf/";
    private const string SensorInitTag = "[VENDOR-SERVICE]";

    private readonly System.Threading.Lock _gate = new();
    private IWigiDashWcf? _channel;
    private ChannelFactory<IWigiDashWcf>? _factory;
    private bool _sensorReady;
    private bool _aidaReady;
    private bool _disposed;

    /// <summary>The HWiNFO sensor read facet.</summary>
    public ISensorValues Sensors { get; }

    /// <summary>The AIDA64 panel image-frame read facet.</summary>
    public IAidaPanel Aida { get; }

    public WigiDashServiceClient()
    {
        Sensors = new SensorValuesFacet(this);
        Aida = new AidaPanelFacet(this);
    }

    /// <summary>Test constructor: injects facet implementations directly.</summary>
    internal WigiDashServiceClient(ISensorValues sensors, IAidaPanel aida)
    {
        Sensors = sensors;
        Aida = aida;
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
                _sensorReady = SafeCall(() => _channel!.InitSensorProvider(true, true, true));
                _aidaReady = SafeCall(() => _channel!.InitAidaProvider());
                var ok = _sensorReady || _aidaReady;
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
        get { lock (_gate) return _channel != null && (_sensorReady || _aidaReady); }
    }

    internal void EnsureChannel()
    {
        if (_channel != null) return;
        var binding = new BasicHttpBinding
        {
            OpenTimeout = TimeSpan.FromSeconds(5),
            CloseTimeout = TimeSpan.FromSeconds(5),
            ReceiveTimeout = TimeSpan.FromSeconds(10),
            SendTimeout = TimeSpan.FromSeconds(10),
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

    internal void RefreshAidaReadiness()
    {
        _aidaReady = SafeCall(() => _channel!.InitAidaProvider());
    }

    internal VendorSensorItem[]? GetRawSensorList()
    {
        try { return _channel!.GetSensorList(); }
        catch { return null; }
    }

    internal (double Value, bool IsValid)? GetRawSensorValue(int rt, int id1, int id2)
    {
        try { return _channel!.GetSensorValue(rt, id1, id2); }
        catch { return null; }
    }

    internal byte[]? GetRawAidaMmap(int offset, int length)
    {
        try
        {
            var (ok, buffer) = _channel.ReadAidaMmap(offset, length);
            return ok ? buffer : null;
        }
        catch { return null; }
    }

    private void CloseChannelLocked()
    {
        if (_channel != null)
        {
            var cc = (System.ServiceModel.ICommunicationObject)_channel;
            try { cc.Close(TimeSpan.FromSeconds(5)); }
            catch { try { cc.Abort(); } catch { /* best-effort */ } }
        }
        _channel = null;
        try { _factory?.Close(); } catch { try { _factory?.Abort(); } catch { /* best-effort */ } }
        _factory = null;
        _sensorReady = false;
        _aidaReady = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _channel?.DeInitSensorProvider(); } catch { /* best-effort */ }
            try { _channel?.DeInitAidaProvider(); } catch { /* best-effort */ }
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
                if (owner._channel == null || !owner._sensorReady) return null;
                var raw = owner.GetRawSensorList();
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

    private sealed class AidaPanelFacet(WigiDashServiceClient owner) : IAidaPanel
    {
        public bool IsReady
        {
            get { lock (owner._gate) return owner._channel != null && owner._aidaReady; }
        }

        public byte[]? ReadAidaMmap(int offset, int length)
        {
            lock (owner._gate)
            {
                if (owner._channel == null || !owner._aidaReady) return null;
                return owner.GetRawAidaMmap(offset, length);
            }
        }
    }
}
