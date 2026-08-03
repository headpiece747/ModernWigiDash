using System.ServiceModel;

namespace ModernWigiDash.Service.Wcf;

/// <summary>
/// WCF Client using System.ServiceModel.ChannelFactory for CoreWCF IPC.
/// Provides direct typed WCF calls between the WPF application and the ModernWigiDash service.
/// The interface is dual-decorated with CoreWCF and System.ServiceModel attributes
/// so ChannelFactory can create a proxy for the CoreWCF-hosted service.
/// </summary>
public sealed class ModernWigiDashDisplayServiceClient : IDisposable
{
    private readonly ChannelFactory<ModernWigiDashDisplayServiceContract> _factory;
    private ModernWigiDashDisplayServiceContract _channel;
    private bool _isDisposed;

    public const int DefaultPort = 8733;
    public static string DefaultEndpointAddress => $"http://localhost:{DefaultPort}/ModernWigiDashDisplayService";
    private static readonly int[] FallbackPorts = [8733, 5000, 5001, 62073];

    public ModernWigiDashDisplayServiceClient(string? endpointAddress = null)
    {
        string address = endpointAddress ?? DefaultEndpointAddress;

        var binding = new BasicHttpBinding
        {
            MaxReceivedMessageSize = 32 * 1024 * 1024,
            MaxBufferSize = 32 * 1024 * 1024,
            ReaderQuotas = new System.Xml.XmlDictionaryReaderQuotas
            {
                MaxArrayLength = 32 * 1024 * 1024,
                MaxBytesPerRead = 32 * 1024 * 1024,
                MaxStringContentLength = 32 * 1024 * 1024
            },
            SendTimeout = TimeSpan.FromSeconds(30),
            ReceiveTimeout = TimeSpan.FromSeconds(30)
        };

        _factory = new ChannelFactory<ModernWigiDashDisplayServiceContract>(binding, new EndpointAddress(address));
        _channel = _factory.CreateChannel();
    }

    public static int? DetectServicePort()
    {
#pragma warning disable S6966 // Intentional sync wrapper — callers require synchronous port detection
        return DetectServicePortAsync().GetAwaiter().GetResult();
#pragma warning restore S6966
    }

    public static async Task<int?> DetectServicePortAsync()
    {
        var portsToCheck = new[] { DefaultPort }.Concat(FallbackPorts).Distinct().ToArray();

        // Shared HttpClient — reuse across all port probes to avoid handler/socket overhead
        using var http = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(1)
        };

        // Fast HTTP health check — avoids WCF channel overhead for detection
        foreach (int port in portsToCheck)
        {
            try
            {
                var resp = await http.GetAsync($"http://localhost:{port}/ModernWigiDashDisplayService").ConfigureAwait(false);
                if (resp.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotFound)
                {
                    return port;
                }
            }
            catch
            {
                // Try next port
            }
        }

        // Fallback: full WCF channel test (only if HTTP check failed)
        using var fallbackHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        foreach (int port in portsToCheck)
        {
            try
            {
                string url = $"http://localhost:{port}/ModernWigiDashDisplayService";
                var binding = new BasicHttpBinding
                {
                    OpenTimeout = TimeSpan.FromSeconds(1),
                    SendTimeout = TimeSpan.FromSeconds(1)
                };
                using var factory = new ChannelFactory<ModernWigiDashDisplayServiceContract>(binding, new EndpointAddress(url));
                ModernWigiDashDisplayServiceContract? client = null;
                try
                {
                    client = factory.CreateChannel();
                    string version = client.GetVersion();
                    if (!string.IsNullOrEmpty(version))
                    {
                        return port;
                    }
                }
                finally
                {
                    try { ((ICommunicationObject?)client)?.Abort(); }
                    catch (CommunicationObjectFaultedException) { /* Expected: channel already faulted */ }
                    catch (ObjectDisposedException) { /* Expected: channel already disposed */ }
                }
            }
            catch
            {
                // Try next port
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

    private static readonly string LogPath = System.IO.Path.Combine(AppContext.BaseDirectory, "display_device.log");

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
            return default!;
        }
        catch (CommunicationException ex)
        {
            LogClient($"[WCF-CLIENT] Communication error: {ex.GetType().Name}: {ex.Message}");
            RecreateChannel();
            return default!;
        }
    }

    private static void LogClient(string msg)
    {
        try
        {
            using var fs = new System.IO.FileStream(LogPath, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
            using var sw = new System.IO.StreamWriter(fs);
            sw.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }
        catch (System.IO.IOException)
        {
            // Log file may be locked or unavailable; silently ignore
        }
    }

    private void RecreateChannel()
    {
        try { ((ICommunicationObject?)_channel)?.Abort(); }
        catch (CommunicationObjectFaultedException) { /* Expected: channel already faulted */ }
        catch (ObjectDisposedException) { /* Expected: channel already disposed */ }
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
