using System.Text;

namespace RemoteSupport.Host;

/// <summary>
/// Append-only diagnostic log for the host, the mirror of the technician console's Trace.
/// Lives in ProgramData because the host may run as SYSTEM, whose user profile is not somewhere
/// anyone wants to look. It records the identity the host is running under and every desktop
/// switch — the two facts that decide whether remote control works at all (docs/HANDOVER.md 6d).
/// </summary>
internal static class HostTrace
{
    private static readonly object _lock = new();
    private static readonly string _dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteSupport");

    public static readonly string Path_ = Path.Combine(_dir, "host-trace.log");

    static HostTrace()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var fi = new FileInfo(Path_);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                var prev = Path_ + ".1";
                try { File.Delete(prev); } catch { }
                try { fi.MoveTo(prev); } catch { }
            }
        }
        catch { }
    }

    public static void Write(string line)
    {
        try
        {
            var s = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {line}{Environment.NewLine}";
            lock (_lock) File.AppendAllText(Path_, s, Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>Who am I and what can I reach — written once at startup.</summary>
    public static void WriteIdentity(string sessionId)
    {
        try
        {
            string user;
            bool isSystem = false;
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                user = id.Name;
                isSystem = id.IsSystem;
            }
            catch { user = "(unknown)"; }

            Write($"=== host started  pid={Environment.ProcessId}  session={sessionId} ===");
            Write($"running as {user}  isSystem={isSystem}");

            // Can we reach the desktop that UAC prompts and the lock screen live on? Only SYSTEM
            // can, and without it remote control dies silently whenever one appears.
            var probe = Native.OpenInputDesktop(0, false, Native.DESKTOP_ALL);
            if (probe != IntPtr.Zero)
            {
                Write($"input desktop at startup: {Native.DesktopName(probe)}");
                Native.CloseDesktop(probe);
            }
            else Write($"could not open the input desktop (win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
        }
        catch { }
    }
}
