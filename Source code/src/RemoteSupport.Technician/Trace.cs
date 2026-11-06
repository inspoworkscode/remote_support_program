using System.Text;

namespace RemoteSupport.Technician;

/// <summary>
/// Append-only diagnostic log for the console. Exists to separate the candidate causes of the
/// black-screen bug (see docs/HANDOVER.md 6b): messages never arriving, messages arriving but
/// being dropped before the UI thread, no subscription being sent, or tiles being rejected by
/// ScreenView. Cheap and always on — one line per interesting event, never per painted pixel.
/// </summary>
public static class Trace
{
    private static readonly object _lock = new();
    private static readonly string _dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheRemoteSupportProgram");

    /// <summary>Full path of the trace file, so the UI can tell the technician where to look.</summary>
    public static readonly string Path_ = Path.Combine(_dir, "technician-trace.log");

    static Trace()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            // Roll rather than grow forever: one previous run is kept for comparison.
            var fi = new FileInfo(Path_);
            if (fi.Exists && fi.Length > 4 * 1024 * 1024)
            {
                var prev = Path_ + ".1";
                try { File.Delete(prev); } catch { }
                try { fi.MoveTo(prev); } catch { }
            }
            Write($"=== console started  pid={Environment.ProcessId}  {Environment.MachineName} ===");
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
}
