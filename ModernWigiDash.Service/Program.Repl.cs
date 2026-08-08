using ModernWigiDash.Service.Wcf;

namespace ModernWigiDash.Service;

/// <summary>
/// Program partial: the interactive WCF client mode used for debugging when
/// the service runs as LocalSystem (avoids WinUsb_Initialize conflicts).
/// Split from Program.cs so the composition root stays composition-only.
/// </summary>
public static partial class Program
{
    private static async Task RunWcfClientMode()
    {
        Console.WriteLine("==========================================================");
        Console.WriteLine(" ModernWigiDash.Service - WCF Client Mode (Debug)");
        Console.WriteLine(" Connecting to running Windows Service via CoreWCF");
        Console.WriteLine("==========================================================");

        // Auto-detect the running service via named pipe
        Console.WriteLine("\nAuto-detecting WCF named pipe service...");
#pragma warning disable S6966 // Console app entry point; sync is acceptable
        string? detectedEndpoint = ModernWigiDashDisplayServiceClient.DetectServicePort();
#pragma warning restore S6966

        if (detectedEndpoint == null)
        {
            Console.WriteLine($"ERROR: No WCF service detected on named pipe endpoint.");
            Console.WriteLine("The Windows Service may not be running. Start it with:");
            Console.WriteLine($"  sc.exe start {ServiceName}");
            Console.WriteLine("Or install it with:");
            Console.WriteLine($"  {ServiceName}.exe -install");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            return;
        }

        Console.WriteLine($"Pipe {detectedEndpoint} detected. Connecting via WCF...");

        try
        {
            using var client = new ModernWigiDashDisplayServiceClient(detectedEndpoint);

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

}
