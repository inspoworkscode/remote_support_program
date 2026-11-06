using System.Runtime.InteropServices;

namespace RemoteSupport.Host;

/// <summary>
/// Who is actually sitting at this machine.
///
/// The host normally runs as SYSTEM so it can reach the Winlogon desktop (see DesktopFollower).
/// SYSTEM has no meaningful user profile, so anything that used to lean on
/// <c>Environment.UserName</c> or the current profile folder has to ask the session instead —
/// otherwise the technician sees "SYSTEM" in the Info tab and files land in
/// C:\Windows\System32\config\systemprofile\Downloads.
/// </summary>
internal static class UserContext
{
    private static readonly Lazy<string> _name = new(QueryName);
    private static readonly Lazy<string> _profile = new(QueryProfile);

    /// <summary>True when the host is running as SYSTEM rather than as the logged-on user.</summary>
    public static bool IsSystem
    {
        get { try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); return id.IsSystem; } catch { return false; } }
    }

    /// <summary>The interactive user, e.g. "DESKTOP-L2B85US\User". Falls back to the process user.</summary>
    public static string InteractiveUser => _name.Value;

    /// <summary>The interactive user's profile folder, for file transfer defaults.</summary>
    public static string ProfileDirectory => _profile.Value;

    /// <summary>The interactive user's SID, or "" — for reading THEIR registry (HKEY_USERS\sid)
    /// rather than SYSTEM's, e.g. their microphone privacy switch (VoiceCall).</summary>
    public static string InteractiveSid
    {
        get
        {
            try
            {
                var account = new System.Security.Principal.NTAccount(InteractiveUser);
                return ((System.Security.Principal.SecurityIdentifier)account.Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value;
            }
            catch { return ""; }
        }
    }

    private static string QueryName()
    {
        try
        {
            uint session = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
            var user = QuerySessionString(session, WTSUserName);
            var domain = QuerySessionString(session, WTSDomainName);
            if (!string.IsNullOrEmpty(user))
                return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
        }
        catch { }
        try { return $"{Environment.UserDomainName}\\{Environment.UserName}"; } catch { return Environment.UserName; }
    }

    private static string QueryProfile()
    {
        // The profile list keys the folder by the user's SID, which is the reliable way to get
        // another account's profile path without impersonating it.
        try
        {
            if (!IsSystem) return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var name = InteractiveUser;
            if (!string.IsNullOrEmpty(name))
            {
                var account = new System.Security.Principal.NTAccount(name);
                var sid = (System.Security.Principal.SecurityIdentifier)account.Translate(typeof(System.Security.Principal.SecurityIdentifier));
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid.Value);
                var path = key?.GetValue("ProfileImagePath") as string;
                if (!string.IsNullOrWhiteSpace(path)) return Environment.ExpandEnvironmentVariables(path);
            }
        }
        catch { }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // ---- interop ----
    private const int WTSUserName = 5, WTSDomainName = 7;

    private static string QuerySessionString(uint session, int infoClass)
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out buf, out _)) return "";
            return Marshal.PtrToStringUni(buf) ?? "";
        }
        catch { return ""; }
        finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
}
