using System.Runtime.InteropServices;

namespace RemoteSupport.Agent;

/// <summary>
/// Launches a process in the active interactive console session from a Windows service (session 0).
/// This is what lets unattended support show the real user's desktop, including the lock screen.
/// </summary>
internal static class SessionLauncher
{
    /// <summary>
    /// Launches the host as SYSTEM inside the active console session.
    ///
    /// SYSTEM is required, not merely elevated: only SYSTEM may open the "Winlogon" desktop, which
    /// is where UAC prompts, the lock screen and Ctrl+Alt+Del live. A host that cannot reach it
    /// keeps sending a stale picture and its input silently evaporates. SYSTEM also outranks every
    /// elevated window, so it fixes remote control against admin apps at the same time
    /// (docs/HANDOVER.md 6d).
    /// </summary>
    public static int LaunchAsSystemInActiveSession(string exePath, string arguments)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) throw new InvalidOperationException("No active console session.");

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY, out IntPtr ownToken))
            throw new InvalidOperationException($"OpenProcessToken failed ({Marshal.GetLastWin32Error()}).");

        IntPtr dupToken = IntPtr.Zero, envBlock = IntPtr.Zero;
        try
        {
            var sa = new SECURITY_ATTRIBUTES(); sa.nLength = Marshal.SizeOf(sa);
            if (!DuplicateTokenEx(ownToken, MAXIMUM_ALLOWED, ref sa, SecurityImpersonation, TokenPrimary, out dupToken))
                throw new InvalidOperationException($"DuplicateTokenEx failed ({Marshal.GetLastWin32Error()}).");

            // Move the copy of our SYSTEM token into the interactive session, or the process would
            // start back in session 0 where there is no desktop to show.
            IntPtr sidPtr = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                Marshal.WriteInt32(sidPtr, (int)sessionId);
                if (!SetTokenInformation(dupToken, TokenSessionId, sidPtr, sizeof(uint)))
                    throw new InvalidOperationException($"SetTokenInformation(TokenSessionId) failed ({Marshal.GetLastWin32Error()}).");
            }
            finally { Marshal.FreeHGlobal(sidPtr); }

            CreateEnvironmentBlock(out envBlock, dupToken, false);

            var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si);
            si.lpDesktop = @"winsta0\default";
            var pi = new PROCESS_INFORMATION();

            string cmd = $"\"{exePath}\" {arguments}";
            bool ok = CreateProcessAsUser(dupToken, null, cmd, ref sa, ref sa, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, envBlock,
                Path.GetDirectoryName(exePath), ref si, out pi);
            if (!ok) throw new InvalidOperationException($"CreateProcessAsUser (SYSTEM) failed ({Marshal.GetLastWin32Error()}).");

            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            return (int)pi.dwProcessId;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (ownToken != IntPtr.Zero) CloseHandle(ownToken);
        }
    }

    public static int LaunchInActiveSession(string exePath, string arguments)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) throw new InvalidOperationException("No active console session.");

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
            throw new InvalidOperationException($"WTSQueryUserToken failed ({Marshal.GetLastWin32Error()}). No user is logged on.");

        IntPtr dupToken = IntPtr.Zero, envBlock = IntPtr.Zero;
        try
        {
            var sa = new SECURITY_ATTRIBUTES(); sa.nLength = Marshal.SizeOf(sa);
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, ref sa, SecurityImpersonation, TokenPrimary, out dupToken))
                throw new InvalidOperationException($"DuplicateTokenEx failed ({Marshal.GetLastWin32Error()}).");

            CreateEnvironmentBlock(out envBlock, dupToken, false);

            var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si);
            si.lpDesktop = @"winsta0\default";
            var pi = new PROCESS_INFORMATION();

            string cmd = $"\"{exePath}\" {arguments}";
            bool ok = CreateProcessAsUser(dupToken, null, cmd, ref sa, ref sa, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, envBlock,
                Path.GetDirectoryName(exePath), ref si, out pi);
            if (!ok) throw new InvalidOperationException($"CreateProcessAsUser failed ({Marshal.GetLastWin32Error()}).");

            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            return (int)pi.dwProcessId;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    public static void Kill(int pid)
    {
        try { var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(true); } catch { }
    }

    // ---- interop ----
    private const int MAXIMUM_ALLOWED = 0x02000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400, CREATE_NO_WINDOW = 0x08000000;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;
    private const int TokenSessionId = 12;
    private const uint TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008;

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int cls, IntPtr info, int length);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existing, int access, ref SECURITY_ATTRIBUTES attrs, int level, int type, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? appName, string cmdLine, ref SECURITY_ATTRIBUTES procAttrs, ref SECURITY_ATTRIBUTES threadAttrs, bool inherit, uint flags, IntPtr env, string? curDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO { public int cb; public string? lpReserved, lpDesktop, lpTitle; public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
}
