# Baker Street Watchdog — Build & Package Script
# Publishes the app and creates a Velopack installer + update feed in one command.
#
# Prerequisites:
#   dotnet tool install -g vpk
#
# Usage:
#   .\Build-Release.ps1 -Version 1.0.0
#   .\Build-Release.ps1 -Version 1.1.0 -ReleaseDir .\releases -SigningCert MyCert.pfx -SigningPassword "secret"

param(
    [Parameter(Mandatory=$true)]
    [string]$Version,

    # Where Velopack writes the installer and update feed files.
    [string]$ReleaseDir = ".\releases",

    # Optional: path to a .pfx code-signing certificate.
    [string]$SigningCert = "",

    # Optional: password for the signing certificate.
    [string]$SigningPassword = "",

    # Optional: URL where you will host the releases folder.
    # Set this to enable the installer to self-update in future.
    [string]$UpdateUrl = ""
)

$ErrorActionPreference = "Stop"

$ProjectDir  = "$PSScriptRoot\BakerStreetWatchdog"
$PublishDir  = "$PSScriptRoot\publish"
$PackId      = "BakerStreetWatchdog"
$MainExe     = "BakerStreetWatchdog.exe"

# ── 1. Clean previous publish output ─────────────────────────────────────────
Write-Host "`n[1/4] Cleaning publish directory..." -ForegroundColor Cyan
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

# ── 2. Publish self-contained single-file exe ─────────────────────────────────
Write-Host "`n[2/4] Publishing ($Version)..." -ForegroundColor Cyan
dotnet publish "$ProjectDir\BakerStreetWatchdog.csproj" `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -o "$PublishDir"

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed."
    exit 1
}

Write-Host "Published to: $PublishDir" -ForegroundColor Green

# ── 3. Package with Velopack ──────────────────────────────────────────────────
Write-Host "`n[3/4] Packaging with Velopack..." -ForegroundColor Cyan

$vpkArgs = @(
    "pack",
    "--packId",      $PackId,
    "--packVersion", $Version,
    "--packDir",     $PublishDir,
    "--mainExe",     $MainExe,
    "--outputDir",   $ReleaseDir,
    # Don't create desktop/Start Menu shortcuts — this is a headless service.
    "--noPortable",
    "--noInst"       # We'll add --noInst only if you DON'T want a Setup.exe
)

# Remove --noInst so we DO get a Setup.exe (the point of Velopack!)
$vpkArgs = $vpkArgs | Where-Object { $_ -ne "--noInst" }

if ($UpdateUrl -ne "") {
    $vpkArgs += "--updateUrl"
    $vpkArgs += $UpdateUrl
}

if ($SigningCert -ne "") {
    $vpkArgs += "--signParams"
    $vpkArgs += "/f `"$SigningCert`" /p `"$SigningPassword`" /fd sha256 /tr http://timestamp.digicert.com /td sha256"
}

Write-Host "Running: vpk $($vpkArgs -join ' ')" -ForegroundColor Gray
vpk @vpkArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "vpk pack failed."
    exit 1
}

# ── 4. Summary ────────────────────────────────────────────────────────────────
Write-Host "`n[4/4] Done!" -ForegroundColor Green
Write-Host ""
Write-Host "Release artifacts written to: $ReleaseDir" -ForegroundColor Cyan
Write-Host ""
Write-Host "  BakerStreetWatchdog-$Version-Setup.exe  ← distribute this to users"
Write-Host "  releases\                               ← host this folder for auto-updates"
Write-Host ""

if ($UpdateUrl -eq "") {
    Write-Host "Tip: run with -UpdateUrl https://your-server.com/releases to enable auto-update." -ForegroundColor Yellow
}
