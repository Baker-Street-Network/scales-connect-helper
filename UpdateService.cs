using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Sources;

namespace BakerStreetWatchdog;

/// <summary>
/// Periodically checks for a new version of the watchdog itself via GitHub Releases
/// and applies it automatically.
///
/// When an update is available Velopack will:
///   1. Download the delta/full package in the background.
///   2. Fire --veloapp-updated on the new binary (which updates the service binary path).
///   3. Restart the service automatically.
/// </summary>
public class UpdateService : BackgroundService
{
    private const string GitHubRepo = "https://github.com/Baker-Street-Network/scales-connect-helper";

    private readonly ILogger<UpdateService> _logger;
    private readonly WatchdogSettings _settings;

    public UpdateService(ILogger<UpdateService> logger, IOptions<WatchdogSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Auto-update enabled. Checking GitHub releases every {Hours}h.",
            _settings.UpdateCheckIntervalHours);

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
            var source = new GithubSource(GitHubRepo, null, false);
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
