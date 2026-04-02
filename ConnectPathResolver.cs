using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BakerStreetWatchdog;

/// <summary>
/// Resolves the installation path of BakerScaleConnect by reading the registry
/// startup entry that the app writes itself on every launch.
///
/// BakerScaleConnect registers itself under:
///   HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run  ?  BakerScaleConnect
///
/// Because the watchdog runs as SYSTEM, HKCU is unavailable. Instead we iterate
/// every loaded user hive under HKU\{SID}\... which SYSTEM can always read.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConnectPathResolver
{
    private const string AppName    = "BakerScaleConnect";
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Attempts to find the BakerScaleConnect executable path from any loaded user hive.
    /// Returns <c>null</c> if the app has not registered itself yet (user not logged in,
    /// or app never launched).
    /// </summary>
    public static string? TryResolve()
    {
        // Iterate all loaded user hives — SYSTEM has full read access to HKU.
        using var hku = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        foreach (string sidName in hku.GetSubKeyNames())
        {
            // Skip _Classes sub-keys and system pseudo-keys (.DEFAULT, S-1-5-18, etc.)
            if (sidName.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
            if (!sidName.StartsWith('S')) continue;

            string runPath = $@"{sidName}\{RunKeyPath}";
            RegistryKey? runKey;
            try
            {
                runKey = hku.OpenSubKey(runPath, writable: false);
            }
            catch (System.Security.SecurityException)
            {
                continue;
            }
            using (runKey)
            {
                if (runKey is null) continue;

                string? raw = runKey.GetValue(AppName) as string;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // The value is stored as: "C:\Users\...\BakerScaleConnect.exe"
                // Strip surrounding quotes if present.
                string path = raw.Trim('"');
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }
}
