using ModernWigiDash.Sdk;
using System.ServiceModel;
using ModernWigiDash.Service.Contracts;

namespace ModernWigiDash.Service.Wcf;

/// <summary>
/// Thrown when a WCF operation cannot be completed because the service channel
/// is faulted or the service is unreachable. The client recreates its channel
/// before throwing, so a subsequent call may succeed.
/// </summary>
public sealed class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// WCF Client using System.ServiceModel.ChannelFactory for CoreWCF IPC.
/// Provides direct typed WCF calls between the WPF application and the ModernWigiDash service.
/// The interface is dual-decorated with CoreWCF and System.ServiceModel attributes
/// so ChannelFactory can create a proxy for the CoreWCF-hosted service.
/// </summary>
public sealed class ModernWigiDashDisplayServiceClient : IDisposable
{
    private readonly ChannelFactory<IModernWigiDashDisplayServiceContract> _factory;
    private IModernWigiDashDisplayServiceContract _channel;
    private bool _isDisposed;

    public static string DefaultEndpointAddress => "net.pipe://localhost/ModernWigiDashDisplayService/WigiDash.svc";
    private static readonly string[] KnownPipeEndpoints =
    [
        "net.pipe://localhost/ModernWigiDashDisplayService/WigiDash.svc"
    ];

    public ModernWigiDashDisplayServiceClient(string? endpointAddress = null)
    {
        string address = endpointAddress ?? DefaultEndpointAddress;

        var binding = new NetNamedPipeBinding(NetNamedPipeSecurityMode.Transport)
        {
            MaxReceivedMessageSize = 32 * 1024 * 1024,
            MaxBufferSize = 32 * 1024 * 1024,
            ReaderQuotas = new System.Xml.XmlDictionaryReaderQuotas
            {
                MaxArrayLength = 32 * 1024 * 1024,
                MaxBytesPerRead = 32 * 1024 * 1024,
                MaxStringContentLength = 32 * 1024 * 1024
            },
            // 5s send timeout (was 30s): every local IPC op completes in
            // milliseconds, and each in-flight request holds a SendTimeout
            // timer — 30s × ~90 ops/s kept ~1000 timers alive continuously.
            SendTimeout = TimeSpan.FromSeconds(5),
            ReceiveTimeout = TimeSpan.FromSeconds(30)
        };

        _factory = new ChannelFactory<IModernWigiDashDisplayServiceContract>(binding, new EndpointAddress(address));
        _channel = _factory.CreateChannel();
    }

    public static string? DetectServicePort()
    {
#pragma warning disable S6966 // Intentional sync wrapper — callers require synchronous port detection
        return DetectServicePortAsync().GetAwaiter().GetResult();
#pragma warning restore S6966
    }

    public static async Task<string?> DetectServicePortAsync()
    {
        // Protocol check: only a real ModernWigiDashDisplayService endpoint
        // answers GetVersion with a non-empty version string, so an impostor
        // pipe cannot hijack frame streaming without speaking the contract.
        foreach (string pipeName in KnownPipeEndpoints)
        {
            try
            {
                var binding = new NetNamedPipeBinding(NetNamedPipeSecurityMode.Transport)
                {
                    OpenTimeout = TimeSpan.FromSeconds(2),
                    SendTimeout = TimeSpan.FromSeconds(2)
                };
                using var factory = new ChannelFactory<IModernWigiDashDisplayServiceContract>(binding, new EndpointAddress(pipeName));
                IModernWigiDashDisplayServiceContract? client = null;
                try
                {
                    client = factory.CreateChannel();
                    string version = client.GetVersion();
                    if (!string.IsNullOrEmpty(version))
                    {
                        return pipeName;
                    }
                }
                finally
                {
                    try { ((ICommunicationObject?)client)?.Abort(); }
                    catch (CommunicationObjectFaultedException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WCF-DETECT] Probe abort failed (faulted): {ex.Message}");
                        /* Expected: channel already faulted */
                    }
                    catch (ObjectDisposedException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WCF-DETECT] Probe abort failed (disposed): {ex.Message}");
                        /* Expected: channel already disposed */
                    }
                }
            }
            catch (Exception ex)
            {
                // Pipe not available — try next endpoint (diagnostic only)
                System.Diagnostics.Debug.WriteLine($"[WCF-DETECT] Pipe {pipeName} unavailable: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return null;
    }

    public bool InitializeDisplay()
    {
        ThrowIfDisposed();
        return ExecuteWithFaultRecovery(() => _channel.InitializeDisplay());
    }

    public bool DeInitializeDisplay()
    {
        ThrowIfDisposed();
        return ExecuteWithFaultRecovery(() => _channel.DeInitializeDisplay());
    }

    public DisplayStatus GetDisplayStatus()
    {
        ThrowIfDisposed();
        return ExecuteWithFaultRecovery(() => _channel.GetDisplayStatus());
    }

    public bool SetBrightness(byte brightnessPercent)
    {
        ThrowIfDisposed();
        return ExecuteWithFaultRecovery(() => _channel.SetBrightness(brightnessPercent));
    }

    public bool SendFrame(byte[] frameBuffer)
    {
        ThrowIfDisposed();
        return ExecuteWithFaultRecovery(() => _channel.SendFrame(new FramePayload { Data = frameBuffer }));
    }

    public ServiceDiagnostics GetDiagnostics()
    {
        ThrowIfDisposed();
        return ExecuteWithFaultRecovery(() => _channel.GetDiagnostics());
    }

    public string GetVersion()
    {
        ThrowIfDisposed();
        return ExecuteWithFaultRecovery(() => _channel.GetVersion());
    }

    public TouchEventInfo? PollTouch()
    {
        ThrowIfDisposed();
        return ExecuteWithFaultRecovery<TouchEventInfo?>(() => _channel.PollTouch());
    }

    public bool Shutdown()
    {
        ThrowIfDisposed();
        return ExecuteWithFaultRecovery(() => _channel.Shutdown());
    }

    public SensorSnapshotDto GetSensorSnapshot()
    {
        ThrowIfDisposed();
        return ExecuteWithFaultRecovery(() => _channel.GetSensorSnapshot());
    }

    public FrameTimeSnapshotDto GetFrameTimeSnapshot(int preferredProcessId = 0)
    {
        ThrowIfDisposed();
        return ExecuteWithFaultRecovery(() => _channel.GetFrameTimeSnapshot(preferredProcessId));
    }

    private T ExecuteWithFaultRecovery<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (CommunicationObjectFaultedException ex)
        {
            LogClient($"[WCF-CLIENT] Faulted: {ex.Message}");
            RecreateChannel();
            throw new ServiceUnavailableException("WCF channel faulted; the service may be restarting.", ex);
        }
        catch (CommunicationException ex)
        {
            LogClient($"[WCF-CLIENT] Communication error: {ex.GetType().Name}: {ex.Message}");
            RecreateChannel();
            throw new ServiceUnavailableException("WCF service unreachable.", ex);
        }
    }

    private static void LogClient(string msg) => FileLog.Write(msg);

    private void RecreateChannel()
    {
        try { ((ICommunicationObject?)_channel)?.Abort(); }
        catch (CommunicationObjectFaultedException)
        {
            System.Diagnostics.Debug.WriteLine("Abort failed: channel already faulted");
            /* Expected: channel already faulted */
        }
        catch (ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine("Abort failed: channel already disposed");
            /* Expected: channel already disposed */
        }
        _channel = _factory.CreateChannel();
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(ModernWigiDashDisplayServiceClient));
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            if (_channel is ICommunicationObject commObj)
            {
                if (commObj.State == CommunicationState.Faulted)
                {
                    commObj.Abort();
                }
                else
                {
                    commObj.Close();
                }
            }
        }
        catch
        {
            (_channel as ICommunicationObject)?.Abort();
        }

        try
        {
            if (_factory.State == CommunicationState.Faulted)
            {
                _factory.Abort();
            }
            else
            {
                _factory.Close();
            }
        }
        catch
        {
            _factory.Abort();
        }

        GC.SuppressFinalize(this);
    }
}
