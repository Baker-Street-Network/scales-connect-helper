using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;

namespace BakerStreetWatchdog;

/// <summary>
/// Background worker that monitors BakerScaleConnect and restarts it whenever
/// the process is not found. Runs continuously as a Windows Service.
/// </summary>
[SupportedOSPlatform("windows")]
public class WatchdogWorker(ILogger<WatchdogWorker> logger, IOptions<WatchdogSettings> settings) : BackgroundService
{
    private readonly ILogger<WatchdogWorker> _logger = logger;
    private readonly WatchdogSettings _settings = settings.Value;

    // Tracks the process we launched so we can subscribe to its exit event
    // and also avoid double-launching.
    private Process? _managedProcess;
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    // The resolved exe path — discovered at runtime from the user's registry.
    private string? _resolvedExePath;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Baker Street Watchdog started. Monitoring '{ProcessName}' every {Interval}s.",
            ProcessName,
            _settings.PollingIntervalSeconds);

        // Try to adopt an already-running instance on service start so we don't
        // launch a duplicate if BakerScaleConnect is already up.
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

    // ── Process name ──────────────────────────────────────────────────────────

    /// <summary>
    /// The process name to monitor. Uses the configured value if set; otherwise
    /// falls back to the well-known name of BakerScaleConnect.
    /// </summary>
    private string ProcessName =>
        string.IsNullOrWhiteSpace(_settings.ProcessName)
            ? "BakerScaleConnect"
            : _settings.ProcessName;

    // ── Executable path resolution ────────────────────────────────────────────

    /// <summary>
    /// Returns the resolved executable path, or <c>null</c> if it cannot yet be
    /// determined. Resolution order:
    ///   1. Previously cached path (re-validated on each call).
    ///   2. Explicit <see cref="WatchdogSettings.ExecutablePath"/> from config.
    ///   3. BakerScaleConnect's own startup registry entry written by
    ///      <c>AddToStartup()</c> — works for any Velopack per-user install location.
    /// </summary>
    private string? ResolveExecutablePath()
    {
        // Return cached value if still valid.
        if (_resolvedExePath != null && File.Exists(_resolvedExePath))
            return _resolvedExePath;

        // 1. Explicit config override.
        if (!string.IsNullOrWhiteSpace(_settings.ExecutablePath) &&
            File.Exists(_settings.ExecutablePath))
        {
            _resolvedExePath = _settings.ExecutablePath;
            _logger.LogInformation("Using configured executable path: {Path}", _resolvedExePath);
            return _resolvedExePath;
        }

        // 2. Auto-discover from the HKCU Run registry entry written by BakerScaleConnect.
        string? discovered = ConnectPathResolver.TryResolve();
        if (discovered != null)
        {
            _resolvedExePath = discovered;
            _logger.LogInformation("Auto-discovered BakerScaleConnect at: {Path}", _resolvedExePath);
            return _resolvedExePath;
        }

        return null;
    }

    // ── Core poll loop ────────────────────────────────────────────────────────

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
            // BakerScaleConnect outside of our management (e.g. auto-start on login).
            var running = Process.GetProcessesByName(ProcessName);
            if (running.Length > 0)
            {
                // Adopt the first instance we find.
                _logger.LogInformation(
                    "Found existing '{ProcessName}' process (PID {Pid}). Adopting it.",
                    ProcessName, running[0].Id);

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

    // ── Process launch ────────────────────────────────────────────────────────

    /// <summary>
    /// Launches BakerScaleConnect in the active interactive user session.
    /// If the executable path cannot be resolved yet (user not logged in or app
    /// not yet installed) logs a warning and defers to the next poll cycle.
    /// </summary>
    private async Task LaunchProcessAsync()
    {
        string? exePath = ResolveExecutablePath();

        if (exePath is null)
        {
            _logger.LogWarning(
                "Cannot locate BakerScaleConnect executable yet. " +
                "Waiting for the app to be installed or for a user to log in. " +
                "Will retry on next poll.");
            return;
        }

        _logger.LogWarning(
            "'{ProcessName}' is not running. Waiting {Delay}s then launching: {Exe}",
            ProcessName,
            _settings.RestartDelaySeconds,
            exePath);

        if (_settings.RestartDelaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(_settings.RestartDelaySeconds)).ConfigureAwait(false);

        try
        {
            string workingDir = string.IsNullOrWhiteSpace(_settings.WorkingDirectory)
                ? Path.GetDirectoryName(exePath) ?? string.Empty
                : _settings.WorkingDirectory;

            // Launch inside the active user session — required because the watchdog
            // runs as SYSTEM and cannot reach the interactive desktop directly.
            var process = UserSessionLauncher.Launch(exePath, _settings.ExecutableArguments, workingDir);
            process.EnableRaisingEvents = true;
            process.Exited += OnManagedProcessExited;

            _managedProcess = process;
            _logger.LogInformation(
                "Successfully launched '{ProcessName}' in user session (PID {Pid}).",
                ProcessName, process.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch '{Exe}'. Will retry on next poll.", exePath);
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────────

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
            ProcessName, exitCode?.ToString() ?? "unknown");

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

                // Launch inside the lock so the poll loop cannot sneak in
                // and fire a second launch before this one completes.
                await LaunchProcessAsync().ConfigureAwait(false);
            }
            finally
            {
                _restartLock.Release();
            }
        });
    }

    // ── Startup helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// On service start, try to find and adopt an already-running instance
    /// so we don't accidentally launch a second copy.
    /// </summary>
    private void TryAdoptExistingProcess()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        if (processes.Length == 0) return;

        _logger.LogInformation(
            "'{ProcessName}' is already running (PID {Pid}). Adopting on startup.",
            ProcessName, processes[0].Id);

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

    // ── Disposal ──────────────────────────────────────────────────────────────

    public override void Dispose()
    {
        _managedProcess?.Dispose();
        _restartLock.Dispose();
        base.Dispose();
    }
}
