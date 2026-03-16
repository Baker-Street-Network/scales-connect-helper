# Baker Street Watchdog

A .NET 8 Windows Service that monitors **Baker Street Connect** and automatically restarts it whenever it stops. Distributed and updated via **Velopack** — users run one installer and the watchdog installs itself as a Windows Service automatically. Future updates can be applied silently with zero user interaction.

---

## How It Works

### Watchdog (runtime)
- Polls every **10 seconds** to check if `BakerStreetConnect` is running.
- Also subscribes to `Process.Exited` for instant reaction on crash.
- Adopts already-running instances on start so it never launches a duplicate.
- Logs everything to the **Windows Event Log** (Event Viewer → Application → BakerStreetWatchdog).

### Velopack (install / update lifecycle)
Velopack calls your exe with special arguments at key lifecycle moments. The watchdog handles each one:

| Hook | What happens |
|---|---|
| `--veloapp-install` | Registers and starts the Windows Service |
| `--veloapp-updated` | Updates the service binary path to the new exe location |
| `--veloapp-uninstall` | Stops and removes the Windows Service before files are deleted |

**Result:** users double-click `BakerStreetWatchdog-Setup.exe` and the service is installed, started, and watching Baker Street Connect — no manual sc.exe or PowerShell required.

---

## Prerequisites

| Tool | Purpose |
|---|---|
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Build |
| `dotnet tool install -g vpk` | Velopack CLI for packaging |

---

## Configuration

Edit `BakerStreetWatchdog/appsettings.json` before building:

```json
{
  "Watchdog": {
    "ProcessName":             "BakerStreetConnect",
    "ExecutablePath":          "C:\\Program Files\\Baker Street\\BakerStreetConnect.exe",
    "ExecutableArguments":     "",
    "PollingIntervalSeconds":  10,
    "RestartDelaySeconds":     3,
    "WorkingDirectory":        "",
    "UpdateUrl":               "",
    "UpdateCheckIntervalHours": 6
  }
}
```

`UpdateUrl` — point this at the folder where you host Velopack releases to enable silent auto-update. Leave blank to disable.

---

## Building a Release

### Install vpk (once)
```powershell
dotnet tool install -g vpk
```

### Run the build script
```powershell
# Basic (no auto-update)
.\Build-Release.ps1 -Version 1.0.0

# With auto-update URL
.\Build-Release.ps1 -Version 1.0.0 -UpdateUrl "https://your-server.com/watchdog/releases"

# With code signing
.\Build-Release.ps1 -Version 1.0.0 -UpdateUrl "https://..." `
    -SigningCert ".\MyCert.pfx" -SigningPassword "secret"
```

Output in `releases\`:
```
BakerStreetWatchdog-1.0.0-Setup.exe   ← give this to users
BakerStreetWatchdog-1.0.0-full.nupkg
releases.win.json                      ← host for auto-updates
```

To release v1.1.0, run the script again. Velopack generates a delta package and updates the feed — running instances pick it up automatically within `UpdateCheckIntervalHours`.

---

## Installing on a User Machine

```
BakerStreetWatchdog-1.0.0-Setup.exe
```

Velopack's one-click installer:
1. Extracts to `%LocalAppData%\BakerStreetWatchdog\`
2. Fires `--veloapp-install` → hook registers + starts the Windows Service
3. Service immediately begins watching Baker Street Connect

**Uninstalling** via Settings → Apps fires `--veloapp-uninstall` → hook stops + removes the service cleanly before any files are deleted.

---

## Auto-Update Flow

1. Two minutes after service start, `UpdateService` calls `CheckForUpdatesAsync()`.
2. New version found → downloads delta package in the background.
3. `ApplyUpdatesAndRestart()` exits the current process.
4. Velopack updater extracts the new version, calls `--veloapp-updated` on the new exe.
5. `OnUpdate` hook calls `ServiceManager.UpdateBinaryPath()` — service now points at new exe.
6. Service restarts. Baker Street Connect is never interrupted.

---

## Project Structure

```
BakerStreetWatchdog/
├── BakerStreetWatchdog/
│   ├── BakerStreetWatchdog.csproj   Velopack NuGet, self-contained publish
│   ├── Program.cs                    VelopackApp hooks wired here (OnInstall/OnUpdate/OnUninstall)
│   ├── ServiceManager.cs             Windows Service install/uninstall via sc.exe
│   ├── WatchdogWorker.cs             Core process-monitoring loop
│   ├── UpdateService.cs              Self-update background service
│   ├── WatchdogSettings.cs           Typed config model
│   └── appsettings.json              Edit before building
├── Build-Release.ps1                 One-command build + vpk pack pipeline
└── README.md
```

---

## Notes on Services and GUI Apps

Windows Services run in Session 0, isolated from the interactive desktop. If Baker Street Connect is a GUI app, configure the service to Log On As the specific Windows user (services.msc → Log On tab). This is the most reliable way to ensure the launched GUI appears on the correct desktop.
