using System.Runtime.InteropServices;

namespace RemoteSupport.Host;

/// <summary>
/// Runs a short-lived copy of this host AS THE LOGGED-ON USER, in the host's own session, and
/// waits for it.
///
/// The host runs as SYSTEM so that it can reach the Winlogon desktop (HANDOVER 4). For almost
/// everything that is the right identity. For one thing it is the wrong one: a file list on the
/// clipboard. Measured on DESKTOP-OPHQIDH on 2026-09-21 with tools/userpaste — the user's own
/// process could read both paths off the clipboard, and could open both files, and the shell
/// still offered no Paste while the clipboard's owner was the SYSTEM host. The identical paths
/// written by a process running as the user were offered and pasted. Explorer will not paste
/// from a list that a process above it owns, and that is the whole of "Ctrl+V does nothing".
///
/// So the host hands that one write to a process that IS the user. The token comes from the
/// session the host is already in, the same way the agent starts the customer's own tray app,
/// so the helper is the user in every respect that Explorer checks: account, logon session,
/// integrity, window station.
/// </summary>
internal static class UserProcess
{
    /// <summary>
    /// Starts <paramref name="exePath"/> with <paramref name="arguments"/> as the interactive user
    /// of this session and waits up to <paramref name="timeoutMs"/> for it to finish.
    /// Returns false, with a reason, if it could not be started or did not finish in time; the
    /// exit code is only meaningful when it returns true.
    /// </summary>
    public static bool RunAsInteractiveUser(string exePath, string arguments, int timeoutMs, out int exitCode, out string error)
    {
        exitCode = -1; error = "";
        uint sessionId = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
        {
            // 1008 (ERROR_NO_TOKEN) is the ordinary case: nobody is logged on in this session.
            error = $"no user token for session {sessionId} (win32 {Marshal.GetLastWin32Error()}) — nobody is logged on";
            return false;
        }

        IntPtr dupToken = IntPtr.Zero, envBlock = IntPtr.Zero;
        var pi = new PROCESS_INFORMATION();
        try
        {
            var sa = new SECURITY_ATTRIBUTES(); sa.nLength = Marshal.SizeOf(sa);
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, ref sa, SecurityImpersonation, TokenPrimary, out dupToken))
            { error = $"DuplicateTokenEx failed (win32 {Marshal.GetLastWin32Error()})"; return false; }

            CreateEnvironmentBlock(out envBlock, dupToken, false);

            var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si);
            si.lpDesktop = @"winsta0\default";

            string cmd = $"\"{exePath}\" {arguments}";
            if (!CreateProcessAsUser(dupToken, null, cmd, ref sa, ref sa, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, envBlock,
                    Path.GetDirectoryName(exePath), ref si, out pi))
            { error = $"CreateProcessAsUser failed (win32 {Marshal.GetLastWin32Error()})"; return false; }

            if (WaitForSingleObject(pi.hProcess, (uint)timeoutMs) != 0)
            {
                TerminateProcess(pi.hProcess, 9);
                error = $"pid {pi.dwProcessId} did not finish within {timeoutMs} ms and was stopped";
                return false;
            }
            GetExitCodeProcess(pi.hProcess, out uint code);
            exitCode = (int)code;
            return true;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    // ---- interop ----
    private const int MAXIMUM_ALLOWED = 0x02000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400, CREATE_NO_WINDOW = 0x08000000;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;

    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existing, int access, ref SECURITY_ATTRIBUTES attrs, int level, int type, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? appName, string cmdLine, ref SECURITY_ATTRIBUTES procAttrs, ref SECURITY_ATTRIBUTES threadAttrs, bool inherit, uint flags, IntPtr env, string? curDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO { public int cb; public string? lpReserved, lpDesktop, lpTitle; public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
}
