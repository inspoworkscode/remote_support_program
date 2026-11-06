using System.Diagnostics;

namespace RemoteSupport.Client;

/// <summary>Launches RemoteSupport.Host.exe (next to this exe, or where a downloaded copy unpacked it) to serve a session.</summary>
internal static class HostSpawner
{
    private static readonly Dictionary<string, Process> _bySession = new();
    private static readonly object _lock = new();

    /// <summary>Where the host is. Beside this exe, unless a downloaded copy unpacked it elsewhere (Launcher.PrepareRunNow).</summary>
    public static string Directory { get; set; } = AppContext.BaseDirectory;

    public static void Start(string wsBase, string sessionId, string hostToken, bool askConsent, string technicianName, string visitorCompany = "")
    {
        var exe = Path.Combine(Directory, "RemoteSupport.Host.exe");
        if (!File.Exists(exe)) exe = Path.Combine(Directory, "RemoteSupport.Host");
        var consent = askConsent ? "ask" : "auto";
        var tech = (technicianName ?? "").Replace("\"", "");
        var visitor = (visitorCompany ?? "").Replace("\"", "");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            Arguments = $"--server {wsBase} --session {sessionId} --token {hostToken} --consent {consent} --tech \"{tech}\"" + (visitor.Length > 0 ? $" --visitor \"{visitor}\"" : "")
        };
        var p = Process.Start(psi);
        if (p != null) lock (_lock) _bySession[sessionId] = p;
    }

    public static void Stop(string sessionId)
    {
        Process? p; lock (_lock) { _bySession.TryGetValue(sessionId, out p); _bySession.Remove(sessionId); }
        try { if (p is { HasExited: false }) p.Kill(true); } catch { }
    }

    public static bool AnyRunning() { lock (_lock) return _bySession.Values.Any(p => !p.HasExited); }
}
