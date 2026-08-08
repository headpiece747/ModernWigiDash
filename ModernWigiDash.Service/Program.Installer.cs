using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;

namespace ModernWigiDash.Service;

/// <summary>
/// Program partial: sc.exe service install/uninstall/start/stop helpers.
/// Split from Program.cs so the composition root stays composition-only.
/// </summary>
public static partial class Program
{
    /// <summary>
    /// Absolute path to sc.exe. PATH/current-directory lookup would let a
    /// planted binary run elevated with the service install (S4036).
    /// </summary>
    private static string ScExePath => Path.Combine(Environment.SystemDirectory, "sc.exe");

    private static string ResolveDotnetPath()
    {
        string candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        return File.Exists(candidate) ? candidate : "dotnet";
    }

    private static async Task InstallServiceAsync(string assemblyPath)
    {
        Console.WriteLine("Installing service...");

        // Run as LocalSystem for full device access (vendor uses ServiceAccount.LocalSystem).
        // Prefer the SDK apphost executable: it gives the service its own process
        // name (ModernWigiDash.Service) in Task Manager and makes
        // Process.GetProcessesByName("ModernWigiDash.Service") work. Fall back to
        // "dotnet <dll>" only when the apphost is absent (e.g. dll-only deploy).
        string? exePath = Path.ChangeExtension(assemblyPath, ".exe");
        string serviceBinPath = File.Exists(exePath)
            ? $"\"{exePath}\""
            : $"\"{ResolveDotnetPath()}\" \"{assemblyPath}\"";

        // Only request elevation if not already running as admin.
        // When already elevated, Verb="runas" can cause nested UAC prompts that fail.
        bool isAdmin = WindowsIdentity.GetCurrent()
            .Claims
            .Any(c => c.Type == "http://schemas.microsoft.com/claims/privilege" && c.Value == "SeShutdownPrivilege");

        var psi = new ProcessStartInfo
        {
            FileName = ScExePath,
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
}
