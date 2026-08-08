using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Threading.Channels;
using CoreWCF;
using CoreWCF.Configuration;
using CoreWCF.Description;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using ModernWigiDash.Service.Services;
using ModernWigiDash.Service.Wcf;

namespace ModernWigiDash.Service;

/// <summary>
/// ModernWigiDash Service - .NET 10 Windows Service with CoreWCF.
///
/// Architecture:
/// - Windows Service running as LocalSystem for full USB device access
/// - CoreWCF named-pipe endpoint (net.pipe://localhost/ModernWigiDashDisplayService/WigiDash.svc) for IPC and display control
/// - Background workers for display hardware and system telemetry
/// - CLI commands: -install, -uninstall, -start, -stop, -reinstall
///
/// CoreWCF is Microsoft's officially supported WCF replacement for .NET 8/9/10.
/// It uses ASP.NET Core hosting (WebApplication) instead of the deprecated ServiceHost.
///
/// When installed as a Windows Service with LocalSystem account, this service
/// has full access to USB HID/WinUSB devices required for the WigiDash display.
///
/// Uses .NET 10 AddWindowsService() + CoreWCF for the modern Windows Service stack.
/// </summary>
public static partial class Program
{
    private const string ServiceName = "ModernWigiDashService";
    private const string ServiceDisplayName = "ModernWigiDash Display Service";

    /// <summary>
    /// Named pipe listen base address. CoreWCF derives the pipe name from this
    /// URI, so the trailing slash is required.
    /// </summary>
    private static string WcfPipeBase => "net.pipe://localhost/ModernWigiDashDisplayService/";

    /// <summary>
    /// Relative service path — combined with <see cref="WcfPipeBase"/> to form
    /// the full endpoint address.
    /// </summary>
    private const string WcfServicePath = "WigiDash.svc";

    /// <summary>
    /// Full WCF endpoint address the client connects to.
    /// </summary>
    private static string WcfEndpoint => WcfPipeBase + WcfServicePath;

    public static async Task Main(string[] args)
    {
        // Parse command-line flags
        bool testMode = args.Length > 0 && args[0].Equals("-test", StringComparison.OrdinalIgnoreCase);
        bool clientMode = args.Length > 0 && args[0].Equals("-client", StringComparison.OrdinalIgnoreCase);

        // -client mode: Connect to the running service via WCF (for debugging/testing)
        // Check this BEFORE CLI handling to avoid routing to HandleCommandLine
        if (clientMode)
        {
            await RunWcfClientMode();
            return;
        }

        // Handle CLI commands when running in interactive mode (not as a service)
        if (Environment.UserInteractive && !testMode)
        {
            await HandleCommandLine(args);
            return;
        }

        // -test mode or Windows Service: start the full service host with CoreWCF
        await RunServiceAsync(testMode ? args[1..] : args, testMode);
    }

    /// <summary>
    /// WCF client mode: connects to the running Windows Service via WCF.
    /// Used for debugging when the service is already running as LocalSystem.
    /// This avoids WinUsb_Initialize conflicts (Win32Error=6 ERROR_INVALID_HANDLE).
    /// </summary>

    private static async Task HandleCommandLine(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine($"{ServiceDisplayName}");
            Console.WriteLine($"Usage: {ServiceName}.exe [-install|-uninstall|-start|-stop|-reinstall]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  -install    Install the service (requires Administrator)");
            Console.WriteLine("  -uninstall  Uninstall the service (requires Administrator)");
            Console.WriteLine("  -start      Start the service");
            Console.WriteLine("  -stop       Stop the service");
            Console.WriteLine("  -reinstall  Reinstall the service (requires Administrator)");
            Console.WriteLine();
            Console.WriteLine($"CoreWCF Endpoint: {WcfEndpoint}");
            Console.WriteLine();
            Console.WriteLine("Debug Options:");
            Console.WriteLine("  -client     Connect to running service via WCF (for debugging)");
            Console.WriteLine("  -test       Run service in interactive mode (for testing)");
            return;
        }

                string command = args[0].ToLowerInvariant();
        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        try
        {
            switch (command)
            {
                case "-install":
                    await InstallServiceAsync(assemblyPath);
                    break;

                case "-uninstall":
                    await UninstallServiceAsync();
                    break;

                case "-start":
                    await StartServiceAsync();
                    break;

                case "-stop":
                    await StopServiceAsync();
                    break;

                case "-reinstall":
                    await StopServiceAsync();
                    await UninstallServiceAsync();
                    await InstallServiceAsync(assemblyPath); // InstallServiceAsync already starts the service
                    break;

                default:
                    Console.WriteLine($"Unknown command: {args[0]}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine("Note: Service management commands require Administrator privileges.");
        }
    }

    /// <param name="isTestMode">Whether running in test/debug mode.</param>
    private static async Task RunServiceAsync(string[] serviceArgs, bool isTestMode = false)
    {
        var currentIdentity = WindowsIdentity.GetCurrent();
        bool isLocalSystem = currentIdentity.Name.Equals(
            "NT AUTHORITY\\SYSTEM",
            StringComparison.OrdinalIgnoreCase);

        Console.WriteLine("==========================================================");
        Console.WriteLine(" ModernWigiDash.Service - CoreWCF .NET 10 Windows Service");
        Console.WriteLine($" Running as: {currentIdentity.Name}");
        Console.WriteLine($" LocalSystem: {isLocalSystem}");
        Console.WriteLine($" CoreWCF Endpoint: {WcfEndpoint}");
        Console.WriteLine($" Mode: {(isTestMode ? "Test/Debug" : "Windows Service")}");
        Console.WriteLine("==========================================================");

        if (!isLocalSystem)
        {
            Console.WriteLine("WARNING: Not running as LocalSystem. Device access may be restricted.");
            Console.WriteLine("Install as Windows Service for full device access:");
            Console.WriteLine($"  {ServiceName}.exe -install");
        }

        var builder = CreateBuilder(serviceArgs, isTestMode);
        WebApplication app = builder.Build();

        if (isTestMode)
        {
            // Enable WSDL metadata endpoint only in test mode (?wsdl query parameter)
            var serviceMetadataBehavior = app.Services.GetRequiredService<ServiceMetadataBehavior>();
            serviceMetadataBehavior.HttpGetEnabled = true;
        }

        // Diagnostic: verify Kestrel is reachable outside CoreWCF pipeline
        app.Map("/trace", () => "ModernWigiDash Service is running.");

        // Register CoreWCF endpoint over named pipes.
        var pipeBinding = new NetNamedPipeBinding
        {
            MaxReceivedMessageSize = 32 * 1024 * 1024,
            MaxBufferSize = 32 * 1024 * 1024,
            ReaderQuotas = new System.Xml.XmlDictionaryReaderQuotas
            {
                MaxArrayLength = 32 * 1024 * 1024,
                MaxBytesPerRead = 32 * 1024 * 1024,
                MaxStringContentLength = 32 * 1024 * 1024
            },
            Security = new NetNamedPipeSecurity
            {
                Mode = NetNamedPipeSecurityMode.Transport
            }
        };

        app.UseServiceModel(serviceBuilder =>
        {
            var serviceOptions = serviceBuilder.AddService<ModernWigiDashDisplayService>(serviceOptions =>
            {
                // In test mode, expose exception details in SOAP faults for diagnostics.
                // Never enable in production — leaks internal details.
                if (isTestMode)
                {
                    serviceOptions.DebugBehavior.IncludeExceptionDetailInFaults = true;
                }
            });
            serviceOptions.AddServiceEndpoint<ModernWigiDashDisplayService, IModernWigiDashDisplayServiceContract>(
                pipeBinding, WcfServicePath);
        });

        Console.WriteLine($"[{TimeProvider.System.GetLocalNow():HH:mm:ss}] CoreWCF endpoint registered at {WcfEndpoint} -> {WcfServicePath}");

        // Run the service (this blocks until shutdown)
        await app.RunAsync();
    }

    /// <summary>
    /// Builds the service composition root. Internal + InternalsVisibleTo so a
    /// host-builder smoke test can resolve every singleton registration and
    /// catch mis-wired workers before a release.
    /// </summary>
    internal static WebApplicationBuilder CreateBuilder(string[] args, bool isTestMode = false)
    {
        // CoreWCF requires WebApplication hosting model (ASP.NET Core)
        var builder = WebApplication.CreateBuilder(args);

        // Configure as Windows Service
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = ServiceName;
        });

        // Configure logging for service
        builder.Logging.AddConsole();
        builder.Logging.AddDebug();

        // Named pipe transport: kernel-level ACL security, no TCP exposure.
        // CoreWCF owns the pipe listener — do NOT use UseUrls() for net.pipe://
        // (Kestrel only accepts http/https there). The trailing slash on the
        // listen base address is required for CoreWCF's pipe-name derivation.
        builder.WebHost.UseNetNamedPipe(options =>
        {
            options.Listen(new Uri(WcfPipeBase));
        });

        // Configure Kestrel: the app talks to the service exclusively over the
        // named pipe, so the HTTP listener is only the ASP.NET host CoreWCF
        // requires. Bind a private ephemeral loopback port instead of the
        // default :5000 to avoid conflicts and keep the surface minimal.
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
            // Ephemeral loopback port: ListenLocalhost rejects port 0, so bind
            // the loopback address explicitly.
            serverOptions.Listen(System.Net.IPAddress.Loopback, 0);
        });

        // Register CoreWCF infrastructure services
        builder.Services.AddServiceModelServices();

        if (isTestMode)
        {
            builder.Services.AddServiceModelMetadata();
        }

        // Frame delivery: one policy module (bounded DropOldest channel →
        // drain-to-latest → paced send) owned by the service. The WCF service
        // pushes encoded bytes in; the send seam writes them to USB. This is
        // the same module the App uses for its WCF and direct-USB hops, so a
        // backlog behaves identically in every mode. No pacing here: the pipe
        // round-trip already bounds delivery, and pacing would add
        // display-visible latency to page switches.
        builder.Services.AddSingleton(sp =>
        {
            var transport = sp.GetRequiredService<IDisplayTransport>();
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            return new FrameDelivery(
                send: bytes => transport.IsConnected && transport.SendFrame(bytes),
                isReady: () => transport.IsConnected,
                minInterval: TimeSpan.Zero,
                timeProvider: timeProvider,
                log: msg => FileLog.Write(msg));
        });

        // Touch input channel (hardware -> WCF -> app)
        // Unbounded — touch events are tiny (~30 bytes) and must not be dropped to preserve gesture data.
        Channel<TouchEventInfo> touchChannel = Channel.CreateUnbounded<TouchEventInfo>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });
        builder.Services.AddSingleton(touchChannel.Reader);
        builder.Services.AddSingleton(touchChannel.Writer);

        // Hardware transport singleton
        builder.Services.AddSingleton<IDisplayTransport, DisplayHidTransport>();

        // Injectable clock: DI classes receive TimeProvider (tests can substitute a fake).
        builder.Services.AddSingleton(TimeProvider.System);

        // DisplayHardwareWorkerService as singleton so the touch loop and standby
        // lifecycle run once per host.
        builder.Services.AddSingleton<DisplayHardwareWorkerService>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DisplayHardwareWorkerService>());

        // LhmSensorReader as singleton background worker polled by the WCF GetSensorSnapshot operation.
        builder.Services.AddSingleton<LhmSensorReader>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<LhmSensorReader>());

        // FrameTimeReader as singleton background worker polled by the WCF GetFrameTimeSnapshot
        // operation. Captures DXGI/D3D9/DxgKrnl ETW present events in-process (no external tool).
        builder.Services.AddSingleton<FrameTimeReader>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<FrameTimeReader>());

        // CoreWCF Service registration.
        // AddService<T>() registers the type in CoreWCF's pipeline.
        // CoreWCF resolves it from DI per-request (default InstanceContextMode.PerCall),
        // so the service type MUST also be in DI for CoreWCF to create instances.
        // Scoped = one instance per WCF request (matches PerCall semantics).
        builder.Services.AddScoped<ModernWigiDashDisplayService>();

        return builder;
    }
}
