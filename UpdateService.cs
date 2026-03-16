using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Sources;

namespace BakerStreetWatchdog;

/// <summary>
/// Periodically checks for a new version of the watchdog itself and applies it.
///
/// When an update is available Velopack will:
///   1. Download the delta/full package in the background.
///   2. Fire --veloapp-updated on the new binary (which updates the service binary path).
///   3. Restart the service automatically.
///
/// This service is optional — remove it and the watchdog will still work fine;
/// it just won't update itself. To enable it, set Watchdog:UpdateUrl in appsettings.json.
/// </summary>
public class UpdateService : BackgroundService
{
    private readonly ILogger<UpdateService> _logger;
    private readonly WatchdogSettings _settings;

    public UpdateService(ILogger<UpdateService> logger, IOptions<WatchdogSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.UpdateUrl))
        {
            _logger.LogInformation(
                "Auto-update disabled. Set Watchdog:UpdateUrl in appsettings.json to enable.");
            return;
        }

        _logger.LogInformation(
            "Auto-update enabled. Checking '{Url}' every {Hours}h.",
            _settings.UpdateUrl, _settings.UpdateCheckIntervalHours);

        // Stagger the first check so it doesn't compete with service startup.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await TryUpdateAsync().ConfigureAwait(false);

            await Task.Delay(
                TimeSpan.FromHours(_settings.UpdateCheckIntervalHours),
                stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TryUpdateAsync()
    {
        try
        {
            var source = new SimpleWebSource(_settings.UpdateUrl);
            var mgr = new UpdateManager(source);

            var newVersion = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (newVersion == null)
            {
                _logger.LogDebug("No update available.");
                return;
            }

            _logger.LogInformation(
                "New watchdog version {Version} found. Downloading...",
                newVersion.TargetFullRelease.Version);

            await mgr.DownloadUpdatesAsync(newVersion).ConfigureAwait(false);

            _logger.LogInformation(
                "Update downloaded. Applying and restarting service...");

            // ApplyUpdatesAndRestart exits the current process; the Velopack
            // update binary then fires --veloapp-updated on the new exe
            // (which re-registers the service binary path) and restarts the service.
            mgr.ApplyUpdatesAndRestart(newVersion);
        }
        catch (Exception ex)
        {
            // Never crash the watchdog over a failed self-update.
            _logger.LogWarning(ex, "Auto-update check failed. Will retry later.");
        }
    }
}
