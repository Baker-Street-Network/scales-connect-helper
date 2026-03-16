using BakerStreetWatchdog;
using Velopack;

// ─────────────────────────────────────────────────────────────────────────────
// Velopack MUST be initialised before anything else in Main.
//
// During install/uninstall Velopack calls this exe with special args:
//   --veloapp-install   → register + start the Windows Service
//   --veloapp-updated   → update the service binary path after an update
//   --veloapp-uninstall → stop + remove the Windows Service
//
// Each hook has a strict time limit (30s) and must not show any UI.
// VelopackApp.Build().Run() intercepts these args and calls our callbacks,
// then exits — normal startup code below never runs in those cases.
// ─────────────────────────────────────────────────────────────────────────────
VelopackApp.Build()
    .OnInstall(version =>
    {
        // Called once after the files are extracted but before the installer closes.
        // Register and start the service pointing at our own executable.
        var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
        ServiceManager.InstallAndStart(exePath);
    })
    .OnUpdate(version =>
    {
        // Called on the NEW version's exe after an update is applied.
        // Velopack moves the exe to a new 'current' directory, so we must
        // update the service's binary path to point at the new location.
        var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
        ServiceManager.UpdateBinaryPath(exePath);
    })
    .OnUninstall(version =>
    {
        // Called before the files are removed. Stop and delete the service first
        // so Windows doesn't complain about locked files.
        ServiceManager.StopAndUninstall();
    })
    .Run();

// ─────────────────────────────────────────────────────────────────────────────
// Normal application startup — only reached when running as the actual service
// or interactively (not during install/update/uninstall hooks).
// ─────────────────────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

// Run as a Windows Service; gracefully falls back to a console app when run
// interactively, which is handy for debugging.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = ServiceManager.ServiceName;
});

// Bind configuration from appsettings.json → Watchdog section.
builder.Services.Configure<WatchdogSettings>(
    builder.Configuration.GetSection(WatchdogSettings.SectionName));

// Write events to the Windows Event Log (Event Viewer → Application).
builder.Logging.AddEventLog(settings =>
{
    settings.SourceName = "BakerStreetWatchdog";
});

// Core watchdog: monitors Baker Street Connect.
builder.Services.AddHostedService<WatchdogWorker>();

// Optional self-updater: checks for new watchdog releases. Does nothing if
// Watchdog:UpdateUrl is empty.
builder.Services.AddHostedService<UpdateService>();

var host = builder.Build();
host.Run();
