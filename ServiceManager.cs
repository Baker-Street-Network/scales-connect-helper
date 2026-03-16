using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace BakerStreetWatchdog;

/// <summary>
/// Encapsulates all Windows Service Control Manager operations needed by the
/// Velopack install/uninstall hooks. Called during --veloapp-install and
/// --veloapp-uninstall, so it must complete quickly and not show any UI.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceManager
{
    public const string ServiceName = "BakerStreetWatchdog";
    public const string DisplayName = "Baker Street Watchdog";
    public const string Description = "Monitors Baker Street Connect and automatically restarts it if it stops running.";

    /// <summary>
    /// Installs and starts the Windows Service pointing at the given executable path.
    /// Safe to call if the service already exists (will update the binary path).
    /// Requires the process to be running elevated (Administrator).
    /// </summary>
    public static void InstallAndStart(string exePath)
    {
        // If the service already exists, just update the binary path and ensure it's running.
        if (ServiceExists())
        {
            UpdateBinaryPath(exePath);
            EnsureStarted();
            return;
        }

        // Create the service via sc.exe so we don't need P/Invoke for the full SCM API.
        RunSc($"create \"{ServiceName}\" binPath= \"{exePath}\" DisplayName= \"{DisplayName}\" start= auto");
        RunSc($"description \"{ServiceName}\" \"{Description}\"");

        // Configure service recovery: restart on every failure with escalating delays.
        RunSc($"failure \"{ServiceName}\" reset= 86400 actions= restart/5000/restart/10000/restart/30000");

        EnsureStarted();
    }

    /// <summary>
    /// Stops and removes the Windows Service. Safe to call if the service doesn't exist.
    /// </summary>
    public static void StopAndUninstall()
    {
        if (!ServiceExists())
            return;

        // Stop first — sc delete on a running service can leave it in a bad state.
        try
        {
            using var svc = new ServiceController(ServiceName);
            if (svc.Status != ServiceControllerStatus.Stopped &&
                svc.Status != ServiceControllerStatus.StopPending)
            {
                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
        catch
        {
            // Best-effort stop; proceed with delete regardless.
        }

        RunSc($"delete \"{ServiceName}\"");
    }

    /// <summary>
    /// Updates the service binary path after a Velopack update moves the exe.
    /// Called from the --veloapp-updated hook.
    /// </summary>
    public static void UpdateBinaryPath(string newExePath)
    {
        if (!ServiceExists()) return;
        RunSc($"config \"{ServiceName}\" binPath= \"{newExePath}\"");
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static bool ServiceExists()
    {
        return ServiceController.GetServices()
            .Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureStarted()
    {
        try
        {
            using var svc = new ServiceController(ServiceName);
            if (svc.Status == ServiceControllerStatus.Stopped ||
                svc.Status == ServiceControllerStatus.Paused)
            {
                svc.Start();
                svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex)
        {
            // Log to stderr; hooks must not show UI or block.
            Console.Error.WriteLine($"[Watchdog] Warning: could not start service: {ex.Message}");
        }
    }

    private static void RunSc(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start sc.exe");

        proc.WaitForExit(10_000);
    }
}
