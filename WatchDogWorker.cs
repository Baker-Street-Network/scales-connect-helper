using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace BakerStreetWatchdog;

/// <summary>
/// Background worker that monitors Baker Street Connect and restarts it whenever
/// the process is not found. Runs continuously as a Windows Service.
/// </summary>
public class WatchdogWorker(ILogger<WatchdogWorker> logger, IOptions<WatchdogSettings> settings) : BackgroundService
{
    private readonly ILogger<WatchdogWorker> _logger = logger;
    private readonly WatchdogSettings _settings = settings.Value;

    // Tracks the process we launched so we can subscribe to its exit event
    // and also avoid double-launching.
    private Process? _managedProcess;
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Baker Street Watchdog started. Monitoring process '{ProcessName}' every {Interval}s.",
            _settings.ProcessName,
            _settings.PollingIntervalSeconds);

        ValidateSettings();

        // Try to adopt an already-running instance on service start so we don't
        // launch a duplicate if Baker Street Connect is already up.
        TryAdoptExistingProcess();

        var pollingInterval = TimeSpan.FromSeconds(_settings.PollingIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(pollingInterval, stoppingToken).ConfigureAwait(false);

            if (stoppingToken.IsCancellationRequested)
                break;

            await EnsureProcessRunningAsync(stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Baker Street Watchdog is stopping.");
    }

    /// <summary>
    /// Checks whether the target process is running. If not, launches it.
    /// Protected by a lock so the poll and the Exited event handler never race.
    /// </summary>
    private async Task EnsureProcessRunningAsync(CancellationToken ct)
    {
        await _restartLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // If we have a reference to a managed process, check whether it is
            // still alive before querying the full process list.
            if (_managedProcess != null)
            {
                try
                {
                    // Refresh to update HasExited
                    _managedProcess.Refresh();
                    if (!_managedProcess.HasExited)
                        return; // Still running — nothing to do.
                }
                catch (InvalidOperationException)
                {
                    // Process object is no longer valid (handle was closed etc.)
                }

                _managedProcess.Dispose();
                _managedProcess = null;
            }

            // Fallback: scan the full process list in case something else started
            // Baker Street Connect outside of our management (e.g. a user launched it).
            var running = Process.GetProcessesByName(_settings.ProcessName);
            if (running.Length > 0)
            {
                // Adopt the first instance we find.
                _logger.LogInformation(
                    "Found existing '{ProcessName}' process (PID {Pid}). Adopting it.",
                    _settings.ProcessName, running[0].Id);

                AdoptProcess(running[0]);

                // Dispose the rest to avoid handle leaks.
                for (int i = 1; i < running.Length; i++)
                    running[i].Dispose();

                return;
            }

            // Process is not running — launch it.
            await LaunchProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <summary>
    /// Launches Baker Street Connect and wires up an exit event for fast reaction.
    /// </summary>
    private async Task LaunchProcessAsync()
    {
        _logger.LogWarning(
            "'{ProcessName}' is not running. Waiting {Delay}s then launching: {Exe}",
            _settings.ProcessName,
            _settings.RestartDelaySeconds,
            _settings.ExecutablePath);

        if (_settings.RestartDelaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(_settings.RestartDelaySeconds)).ConfigureAwait(false);

        try
        {
            var workingDir = string.IsNullOrWhiteSpace(_settings.WorkingDirectory)
                ? Path.GetDirectoryName(_settings.ExecutablePath) ?? string.Empty
                : _settings.WorkingDirectory;

            var startInfo = new ProcessStartInfo
            {
                FileName = _settings.ExecutablePath,
                Arguments = _settings.ExecutableArguments,
                WorkingDirectory = workingDir,
                UseShellExecute = true,   // Required for GUI apps launched from a service
                WindowStyle = ProcessWindowStyle.Normal
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += OnManagedProcessExited;

            if (process.Start())
            {
                _managedProcess = process;
                _logger.LogInformation(
                    "Successfully launched '{ProcessName}' (PID {Pid}).",
                    _settings.ProcessName, process.Id);
            }
            else
            {
                _logger.LogError("Process.Start() returned false for '{Exe}'. Will retry on next poll.",
                    _settings.ExecutablePath);
                process.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch '{Exe}'. Will retry on next poll.",
                _settings.ExecutablePath);
        }
    }

    /// <summary>
    /// Called when the process we launched raises its Exited event.
    /// Triggers an immediate restart rather than waiting for the next poll cycle.
    /// </summary>
    private void OnManagedProcessExited(object? sender, EventArgs e)
    {
        int? exitCode = null;
        try { exitCode = _managedProcess?.ExitCode; } catch { /* ignore */ }

        _logger.LogWarning(
            "'{ProcessName}' exited unexpectedly (exit code: {ExitCode}). Scheduling immediate restart.",
            _settings.ProcessName, exitCode?.ToString() ?? "unknown");

        // Fire-and-forget restart on a thread-pool thread so the event handler
        // returns immediately and doesn't block the CLR's event dispatch.
        _ = Task.Run(async () =>
        {
            await _restartLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_managedProcess != null)
                {
                    _managedProcess.Dispose();
                    _managedProcess = null;
                }
            }
            finally
            {
                _restartLock.Release();
            }

            await LaunchProcessAsync().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// On service start, try to find and adopt an already-running instance
    /// so we don't accidentally launch a second copy.
    /// </summary>
    private void TryAdoptExistingProcess()
    {
        var processes = Process.GetProcessesByName(_settings.ProcessName);
        if (processes.Length == 0) return;

        _logger.LogInformation(
            "'{ProcessName}' is already running (PID {Pid}). Adopting on startup.",
            _settings.ProcessName, processes[0].Id);

        AdoptProcess(processes[0]);

        for (int i = 1; i < processes.Length; i++)
            processes[i].Dispose();
    }

    private void AdoptProcess(Process process)
    {
        process.EnableRaisingEvents = true;
        process.Exited += OnManagedProcessExited;
        _managedProcess = process;
    }

    private void ValidateSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.ProcessName))
            throw new InvalidOperationException("Watchdog:ProcessName must be set in appsettings.json.");

        if (string.IsNullOrWhiteSpace(_settings.ExecutablePath))
            throw new InvalidOperationException("Watchdog:ExecutablePath must be set in appsettings.json.");

        if (!File.Exists(_settings.ExecutablePath))
            _logger.LogWarning(
                "Executable not found at '{Path}'. Watchdog will keep trying but restarts will fail until the path is valid.",
                _settings.ExecutablePath);
    }

    public override void Dispose()
    {
        _managedProcess?.Dispose();
        _restartLock.Dispose();
        base.Dispose();
    }
}
