namespace BakerStreetWatchdog;

public class WatchdogSettings
{
    public const string SectionName = "Watchdog";

    /// <summary>
    /// The process name to look for (without .exe extension).
    /// Example: "BakerStreetConnect"
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the executable to launch if the process is not found.
    /// Example: "C:\Program Files\Baker Street\BakerStreetConnect.exe"
    /// </summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Optional command-line arguments to pass to the executable.
    /// </summary>
    public string ExecutableArguments { get; set; } = string.Empty;

    /// <summary>
    /// Optional working directory for the launched process.
    /// Defaults to the directory of the executable if not set.
    /// </summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// How often (in seconds) to check whether the process is running.
    /// Default: 10 seconds.
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// How long (in seconds) to wait after detecting a crash before restarting.
    /// Gives the OS time to clean up handles before re-launching.
    /// Default: 3 seconds.
    /// </summary>
    public int RestartDelaySeconds { get; set; } = 3;

    // ── Self-update settings ──────────────────────────────────────────────────

    /// <summary>
    /// URL to the folder/feed where Velopack releases are hosted.
    /// Example: "https://my-server.com/releases/watchdog"
    /// Leave empty to disable self-updating.
    /// </summary>
    public string UpdateUrl { get; set; } = string.Empty;

    /// <summary>
    /// How often (in hours) to check for a new watchdog release.
    /// Default: 6 hours.
    /// </summary>
    public int UpdateCheckIntervalHours { get; set; } = 6;
}
