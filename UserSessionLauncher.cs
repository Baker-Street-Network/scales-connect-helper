using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BakerStreetWatchdog;

/// <summary>
/// Launches a process inside the currently active interactive Windows user session.
/// Required because the watchdog runs as SYSTEM (no desktop), but BakerScaleConnect
/// is a WinForms app that must run in the user's session.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UserSessionLauncher
{
    // ?? P/Invoke declarations ????????????????????????????????????????????????

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out nint phToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out nint lpEnvironment, nint hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(nint lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        nint hToken,
        string? lpApplicationName,
        string lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        nint tokenHandle,
        bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int  HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID  Luid;
        public uint  Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint              PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;   // single-entry inline array
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY             = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED    = 0x0002;
    private const string SE_TCB_NAME           = "SeTcbPrivilege";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize;
        public uint dwXCountChars, dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint NORMAL_PRIORITY_CLASS      = 0x00000020;
    private const uint CREATE_NEW_CONSOLE         = 0x00000010;

    // ?? Public API ????????????????????????????????????????????????????????????

    /// <summary>
    /// Enables <c>SeTcbPrivilege</c> in the current process token so that
    /// <see cref="WTSQueryUserToken"/> succeeds when running as a Windows Service
    /// under the SYSTEM account.
    /// </summary>
    private static void EnableTcbPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out nint token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed.");

        try
        {
            if (!LookupPrivilegeValue(null, SE_TCB_NAME, out LUID luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"LookupPrivilegeValue failed for '{SE_TCB_NAME}'.");

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege      = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
            };

            AdjustTokenPrivileges(token, false, ref tp, 0, nint.Zero, nint.Zero);

            // AdjustTokenPrivileges returns true even for partial success; check
            // GetLastError to distinguish "not all privileges assigned" (1300).
            int err = Marshal.GetLastWin32Error();
            if (err != 0)
                throw new Win32Exception(err, $"AdjustTokenPrivileges failed to enable '{SE_TCB_NAME}'. Ensure the service account has this privilege.");
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Launches <paramref name="exePath"/> inside the active interactive session.
    /// Returns the resulting <see cref="Process"/> (opened by PID), or throws on
    /// failure. The caller is responsible for disposing the returned process.
    /// </summary>
    /// <exception cref="InvalidOperationException">No active interactive session.</exception>
    /// <exception cref="Win32Exception">A Win32 call failed.</exception>
    public static Process Launch(string exePath, string arguments, string workingDirectory)
    {
        EnableTcbPrivilege();

        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
            throw new InvalidOperationException("No active interactive session found. Is a user logged in?");

        if (!WTSQueryUserToken(sessionId, out nint userToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed.");

        nint envBlock = nint.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out envBlock, userToken, false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed.");

            // Build the full command-line string.
            string commandLine = string.IsNullOrWhiteSpace(arguments)
                ? $"\"{exePath}\""
                : $"\"{exePath}\" {arguments}";

            string? cwd = string.IsNullOrWhiteSpace(workingDirectory)
                ? Path.GetDirectoryName(exePath)
                : workingDirectory;

            var si = new STARTUPINFO
            {
                cb          = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop   = "winsta0\\default",  // Interactive desktop
                dwFlags     = 0x00000001,           // STARTF_USESHOWWINDOW
                wShowWindow = 1                     // SW_NORMAL
            };

            uint flags = NORMAL_PRIORITY_CLASS | CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE;

            if (!CreateProcessAsUser(
                    userToken,
                    null,
                    commandLine,
                    nint.Zero,
                    nint.Zero,
                    false,
                    flags,
                    envBlock,
                    cwd,
                    ref si,
                    out var pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcessAsUser failed for '{exePath}'.");
            }

            // Close the Win32 handles — we only need the PID.
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);

            // Return a managed Process opened by PID so the caller can subscribe to Exited.
            return Process.GetProcessById(pi.dwProcessId);
        }
        finally
        {
            if (envBlock != nint.Zero) DestroyEnvironmentBlock(envBlock);
            CloseHandle(userToken);
        }
    }
}
