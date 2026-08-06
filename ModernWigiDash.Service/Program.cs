using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Threading.Channels;
using CoreWCF;
using CoreWCF.Configuration;
using CoreWCF.Description;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Service.Services;
using ModernWigiDash.Service.Wcf;

namespace ModernWigiDash.Service;

/// <summary>
/// ModernWigiDash Service - .NET 10 Windows Service with CoreWCF.
///
/// Architecture:
/// - Windows Service running as LocalSystem for full USB device access
/// - CoreWCF HTTP endpoint at http://localhost:8733/ for IPC and display control
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
public static class Program
{
    private const string ServiceName = "ModernWigiDashService";
    private const string ServiceDisplayName = "ModernWigiDash Display Service";

    /// <summary>
    /// WCF endpoint port matching vendor's configuration (port 8733).
    /// </summary>
    private const int WcfPort = 8733;

    /// <summary>
    /// Full WCF endpoint base URL.
    /// </summary>
    private static string WcfEndpoint => $"http://localhost:{WcfPort}/";

    /// <summary>
    /// Full WCF service endpoint path.
    /// </summary>
    private const string WcfServicePath = "/ModernWigiDashDisplayService";

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
    private static async Task RunWcfClientMode()
    {
        Console.WriteLine("==========================================================");
        Console.WriteLine(" ModernWigiDash.Service - WCF Client Mode (Debug)");
        Console.WriteLine(" Connecting to running Windows Service via CoreWCF");
        Console.WriteLine("==========================================================");

        // Auto-detect the running service port (handles port drift between installs)
        Console.WriteLine("\nAuto-detecting WCF service port...");
#pragma warning disable S6966 // Console app entry point; sync is acceptable
        int? detectedPort = ModernWigiDashDisplayServiceClient.DetectServicePort();
#pragma warning restore S6966

        if (!detectedPort.HasValue)
        {
            Console.WriteLine($"ERROR: No WCF service detected on ports {WcfPort}, 5000, 5001, 62073.");
            Console.WriteLine("The Windows Service may not be running. Start it with:");
            Console.WriteLine($"  sc.exe start {ServiceName}");
            Console.WriteLine("Or install it with:");
            Console.WriteLine($"  {ServiceName}.exe -install");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            return;
        }

        Console.WriteLine($"Port {detectedPort} detected. Connecting via WCF...");

        try
        {
            string endpointUrl = $"http://localhost:{detectedPort.Value}{WcfServicePath}";
            using var client = new ModernWigiDashDisplayServiceClient(endpointUrl);

            // Test connection
            Console.WriteLine("\nTesting WCF connection...");
            string version = client.GetVersion();
            Console.WriteLine($"  Version: {version}");

            // Get display status
            Console.WriteLine("\nGetting display status...");
            var status = client.GetDisplayStatus();
            Console.WriteLine($"  IsConnected: {status.IsConnected}");
            Console.WriteLine($"  DevicePath: {status.DevicePath}");
            Console.WriteLine($"  State: {status.State}");
            Console.WriteLine($"  DiagnosticSummary: {status.DiagnosticSummary}");

            // Get diagnostics
            Console.WriteLine("\nGetting service diagnostics...");
            var diagnostics = client.GetDiagnostics();
            Console.WriteLine($"  ServiceName: {diagnostics.ServiceName}");
            Console.WriteLine($"  ServiceAccount: {diagnostics.ServiceAccount}");
            Console.WriteLine($"  Uptime: {diagnostics.Uptime}");
            Console.WriteLine($"  DisplayStatus: {diagnostics.DisplayStatus}");
            Console.WriteLine($"  WcfEndpoint: {diagnostics.WcfEndpoint}");

            Console.WriteLine("\nWCF connection successful!");
            Console.WriteLine("The Windows Service is running and controlling the display.");
            Console.WriteLine("\nInteractive WCF Client - Commands:");
            Console.WriteLine("  status    - Get display status");
            Console.WriteLine("  brightness <0-100> - Set brightness");
            Console.WriteLine("  diag      - Get diagnostics");
            Console.WriteLine("  version   - Get version");
            Console.WriteLine("  quit      - Exit");
            Console.WriteLine();

            // Interactive loop
            while (true)
            {
                Console.Write("> ");
                string? input = Console.ReadLine();
                if (string.IsNullOrEmpty(input))
                    continue;

                var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string command = parts[0].ToLowerInvariant();

                try
                {
                    bool handled = false;
                    bool shouldExit = false;

                    if (command == "status")
                    {
                        var s = client.GetDisplayStatus();
                        Console.WriteLine($"  Connected: {s.IsConnected}, State: {s.State}");
                        Console.WriteLine($"  Device: {s.DevicePath}");
                        Console.WriteLine($"  Summary: {s.DiagnosticSummary}");
                        handled = true;
                    }
                    else if (command == "brightness" && parts.Length > 1 && byte.TryParse(parts[1], out byte brightness))
                    {
                        bool ok = client.SetBrightness(brightness);
                        Console.WriteLine($"  Brightness set to {brightness}%: {(ok ? "OK" : "Failed")}");
                        handled = true;
                    }
                    else if (command == "diag")
                    {
                        var d = client.GetDiagnostics();
                        Console.WriteLine($"  Service: {d.ServiceName} ({d.ServiceAccount})");
                        Console.WriteLine($"  Uptime: {d.Uptime}");
                        Console.WriteLine($"  Display: {d.DisplayStatus}");
                        handled = true;
                    }
                    else if (command == "version")
                    {
                        Console.WriteLine($"  {client.GetVersion()}");
                        handled = true;
                    }
                    else if (command == "frametime")
                    {
                        var ft = client.GetFrameTimeSnapshot();
                        Console.WriteLine($"  Available: {ft.IsAvailable}");
                        Console.WriteLine($"  Error: {ft.ErrorMessage}");
                        Console.WriteLine($"  Process: {ft.ProcessName} (PID {ft.ProcessId})");
                        Console.WriteLine($"  FPS: {ft.Fps:F1}  FrameTime: {ft.FrameTimeMs:F2} ms");
                        Console.WriteLine($"  1% Low: {ft.Low1PercentFps:F1} FPS  0.1% Low: {ft.Low01PercentFps:F1} FPS");
                        Console.WriteLine($"  GPU Busy: {ft.GpuBusyPercent:F1}%  CPU Frame: {ft.CpuFrameTimeMs:F2} ms");
                        Console.WriteLine($"  Samples: {ft.RecentFrameTimesMs.Count}  LastUpdate: {ft.LastUpdate:HH:mm:ss}");
                        handled = true;
                    }
                    else if (command is "quit" or "exit")
                    {
                        Console.WriteLine("Goodbye.");
                        shouldExit = true;
                        handled = true;
                    }

                    if (!handled)
                    {
                        Console.WriteLine($"  Unknown command: {command}");
                    }

                    if (shouldExit)
                        return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nERROR: Failed to connect to WCF service: {ex.Message}");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }

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

    private static async Task InstallServiceAsync(string assemblyPath)
    {
        Console.WriteLine("Installing service...");

        // Use sc.exe to create the service, matching vendor architecture
        // Runs as LocalSystem for full device access (vendor uses ServiceAccount.LocalSystem)
        // For framework-dependent projects, the binPath must be "dotnet <dll-path>"
        // because Windows Services need an executable, not a DLL directly.
        string serviceBinPath = $"dotnet \"{assemblyPath}\"";

        // Only request elevation if not already running as admin.
        // When already elevated, Verb="runas" can cause nested UAC prompts that fail.
        bool isAdmin = WindowsIdentity.GetCurrent()
            .Claims
            .Any(c => c.Type == "http://schemas.microsoft.com/claims/privilege" && c.Value == "SeShutdownPrivilege");

        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"create \"{ServiceName}\" binPath=\"{serviceBinPath}\" start=auto obj= \"LocalSystem\" displayname=\"{ServiceDisplayName}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Verb = isAdmin ? "" : "runas"
        };

        using var process = Process.Start(psi);
        if (process != null)
        {
            await process.WaitForExitAsync();
            // Read output after WaitForExit to avoid deadlocks
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();

            if (process.ExitCode == 0)
            {
                Console.WriteLine("Service installed successfully.");
                if (!string.IsNullOrEmpty(output))
                    Console.WriteLine(output);

                // Wait for service to appear in service list
                bool installed = false;
                for (int i = 0; i < 50; i++)
                {
                    await Task.Delay(100);
                    try
                    {
                        using var sc = new System.ServiceProcess.ServiceController(ServiceName);
                        if (sc.ServiceName == ServiceName)
                        {
                            installed = true;
                            break;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        System.Diagnostics.Debug.WriteLine("Install poll: service not yet registered in Service Control Manager");
                        // Service not yet registered in Service Control Manager
                    }
                }

                if (installed)
                {
                    Console.WriteLine("Service is installed and ready.");
                    Console.WriteLine("Starting service...");
                    await StartServiceAsync();
                }
            }
            else
            {
                Console.WriteLine($"Failed to install service. Exit code: {process.ExitCode}");
                if (!string.IsNullOrEmpty(error))
                    Console.WriteLine(error);
            }
        }
    }

    private static async Task UninstallServiceAsync()
    {
        Console.WriteLine("Uninstalling service...");

        bool isAdmin = WindowsIdentity.GetCurrent()
            .Claims
            .Any(c => c.Type == "http://schemas.microsoft.com/claims/privilege" && c.Value == "SeShutdownPrivilege");

        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"delete \"{ServiceName}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Verb = isAdmin ? "" : "runas"
        };

        using var process = Process.Start(psi);
        if (process != null)
        {
            await process.WaitForExitAsync();
            string error = await process.StandardError.ReadToEndAsync();

            if (process.ExitCode == 0)
            {
                Console.WriteLine("Service uninstalled successfully.");
            }
            else
            {
                Console.WriteLine($"Failed to uninstall service. Exit code: {process.ExitCode}");
                if (!string.IsNullOrEmpty(error))
                    Console.WriteLine(error);
            }
        }
    }

    private static async Task StartServiceAsync()
    {
        Console.WriteLine("Starting service...");
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(ServiceName);
            sc.Start();
            sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            Console.WriteLine($"Service started. Status: {sc.Status}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start service: {ex.Message}");
        }
    }

    private static async Task StopServiceAsync()
    {
        Console.WriteLine("Stopping service...");
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(ServiceName);
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            Console.WriteLine($"Service stopped. Status: {sc.Status}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to stop service: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs the full service with CoreWCF HTTP endpoint and background workers.
    /// Uses WebApplication hosting model as required by CoreWCF.
    /// </summary>
    /// <param name="serviceArgs">Command-line arguments for the service.</param>
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

        // CoreWCF requires WebApplication hosting model (ASP.NET Core)
        var builder = WebApplication.CreateBuilder(serviceArgs);

        // Configure as Windows Service
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = ServiceName;
        });

        // Configure logging for service
        builder.Logging.AddConsole();
        builder.Logging.AddDebug();

        // Explicitly bind Kestrel to the WCF port so the client can always find the service.
        // Without this, Kestrel picks an unpredictable default port when running as a Windows Service.
        builder.WebHost.UseUrls($"http://localhost:{WcfPort}");

        // Configure Kestrel web host to allow up to 32MB request body size for 1.23MB frame payload SOAP messages
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
        });

        // Register CoreWCF infrastructure services
        builder.Services.AddServiceModelServices();

        if (isTestMode)
        {
            builder.Services.AddServiceModelMetadata();
        }

        // Configure Bounded Channel — only keep latest frame (display always shows most recent)
        var channelOptions = new BoundedChannelOptions(capacity: 2)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        };

        Channel<byte[]> frameChannel = Channel.CreateBounded<byte[]>(channelOptions);
        builder.Services.AddSingleton(frameChannel.Reader);
        builder.Services.AddSingleton(frameChannel.Writer);

        // Touch input channel (hardware -> WCF -> app)
        // Unbounded — touch events are tiny (~30 bytes) and must not be dropped to preserve gesture data.
        Channel<DisplayTouchInput> touchChannel = Channel.CreateUnbounded<DisplayTouchInput>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });
        builder.Services.AddSingleton(touchChannel.Reader);
        builder.Services.AddSingleton(touchChannel.Writer);

        // Hardware transport singleton
        builder.Services.AddSingleton<DisplayHidTransport>();

        // DisplayHardwareWorkerService as singleton so WCF service can inject it for frame stats
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

        WebApplication app = builder.Build();

        if (isTestMode)
        {
            // Enable WSDL metadata endpoint only in test mode (?wsdl query parameter)
            var serviceMetadataBehavior = app.Services.GetRequiredService<ServiceMetadataBehavior>();
            serviceMetadataBehavior.HttpGetEnabled = true;
        }

        // Diagnostic: verify Kestrel is reachable outside CoreWCF pipeline
        app.Map("/trace", () => "ModernWigiDash Service is running.");

        // Register CoreWCF endpoint.
        var httpBinding = new BasicHttpBinding
        {
            MaxReceivedMessageSize = 32 * 1024 * 1024,
            MaxBufferSize = 32 * 1024 * 1024,
            ReaderQuotas = new System.Xml.XmlDictionaryReaderQuotas
            {
                MaxArrayLength = 32 * 1024 * 1024,
                MaxBytesPerRead = 32 * 1024 * 1024,
                MaxStringContentLength = 32 * 1024 * 1024
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
            serviceOptions.AddServiceEndpoint<ModernWigiDashDisplayService, ModernWigiDashDisplayServiceContract>(
                httpBinding, WcfServicePath);
        });

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] CoreWCF endpoint registered at {WcfEndpoint} -> {WcfServicePath}");

        // Run the service (this blocks until shutdown)
        await app.RunAsync();
    }
}