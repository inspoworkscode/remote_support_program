using System.Runtime.InteropServices;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// What this machine has and how busy it is (2026-10-13, the control dashboard, stage B): cores, memory, disk, and the
/// processor averaged over the last five minutes — sampled every 10 seconds, so one busy moment (a restart storm,
/// HANDOVER §126) is not read as a full server, and a full one is not missed between two quiet readings.
///
/// <para>Linux (every real server) reads <c>/proc/stat</c> and <c>/proc/meminfo</c>: memory in use is total minus
/// <c>MemAvailable</c>, which counts the page cache as free, as it is. Windows (this PC, harnesses) asks
/// <c>GetSystemTimes</c> and <c>GlobalMemoryStatusEx</c>. The disk is the one holding the data directory — on live,
/// <c>rsp-files</c>.</para>
///
/// <para><c>Testing:Readings:*</c> (MemoryUsedPercent, CpuPercent, DiskUsedPercent, MemoryTotalMb) replace a reading —
/// harnesses only, so a harness can see amber and red without filling a machine. Never on a real server.</para>
/// </summary>
public sealed class ServerReadings : BackgroundService
{
    public sealed record Snapshot(int Cores, long MemoryTotalMb, long MemoryAvailableMb, double CpuPercent,
        double DiskTotalGb, double DiskFreeGb, DateTime StartedUtc);

    private readonly IConfiguration _cfg;
    private readonly string _dataDir;
    private readonly Queue<double> _cpu = new();
    private (ulong idle, ulong total)? _last;
    private readonly DateTime _started = DateTime.UtcNow;
    private const int Samples = 30;   // 30 × 10 s = five minutes

    public ServerReadings(IConfiguration cfg, string dataDir) { _cfg = cfg; _dataDir = dataDir; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { Sample(); } catch { /* a reading that cannot be taken is simply not counted */ }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stop); } catch (OperationCanceledException) { return; }
        }
    }

    private void Sample()
    {
        var now = CpuTimes();
        if (now == null) return;
        if (_last is { } l && now.Value.total > l.total)
        {
            var busy = 1.0 - (double)(now.Value.idle - l.idle) / (now.Value.total - l.total);
            lock (_cpu) { _cpu.Enqueue(Math.Clamp(busy * 100, 0, 100)); while (_cpu.Count > Samples) _cpu.Dequeue(); }
        }
        _last = now;
    }

    public Snapshot Now()
    {
        var (total, avail) = Memory();
        double cpu; lock (_cpu) cpu = _cpu.Count == 0 ? 0 : _cpu.Average();
        double diskTotal = 0, diskFree = 0;
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_dataDir))!);
            // On Linux the root is "/" — find the mount that actually holds the data directory.
            if (OperatingSystem.IsLinux())
                d = DriveInfo.GetDrives().Where(x => x.IsReady && Path.GetFullPath(_dataDir).StartsWith(x.RootDirectory.FullName, StringComparison.Ordinal))
                    .OrderByDescending(x => x.RootDirectory.FullName.Length).FirstOrDefault() ?? d;
            diskTotal = d.TotalSize / 1e9; diskFree = d.AvailableFreeSpace / 1e9;
        }
        catch { }

        if (double.TryParse(_cfg["Testing:Readings:MemoryTotalMb"], out var mt)) total = (long)mt;
        if (double.TryParse(_cfg["Testing:Readings:MemoryUsedPercent"], out var mu)) avail = (long)(total * (1 - mu / 100));
        if (double.TryParse(_cfg["Testing:Readings:CpuPercent"], out var c)) cpu = c;
        if (double.TryParse(_cfg["Testing:Readings:DiskUsedPercent"], out var du) && diskTotal > 0) diskFree = diskTotal * (1 - du / 100);
        return new Snapshot(Environment.ProcessorCount, total, avail, Math.Round(cpu, 1), Math.Round(diskTotal, 1), Math.Round(diskFree, 1), _started);
    }

    // ---- the operating system's own figures --------------------------------------------------

    private static (ulong idle, ulong total)? CpuTimes()
    {
        if (OperatingSystem.IsLinux())
        {
            var first = File.ReadLines("/proc/stat").FirstOrDefault(x => x.StartsWith("cpu "));
            if (first == null) return null;
            var n = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(ulong.Parse).ToArray();
            // user nice system idle iowait irq softirq steal — idle and iowait are the machine not working
            ulong idle = n[3] + (n.Length > 4 ? n[4] : 0);
            ulong total = 0; for (int i = 0; i < Math.Min(n.Length, 8); i++) total += n[i];
            return (idle, total);
        }
        if (OperatingSystem.IsWindows() && GetSystemTimes(out var idleT, out var kernel, out var user))
            return ((ulong)idleT, (ulong)(kernel + user));   // kernel time includes idle
        return null;
    }

    private static (long totalMb, long availMb) Memory()
    {
        if (OperatingSystem.IsLinux())
        {
            long total = 0, avail = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:")) total = Kb(line);
                else if (line.StartsWith("MemAvailable:")) avail = Kb(line);
            }
            return (total / 1024, avail / 1024);
        }
        if (OperatingSystem.IsWindows())
        {
            var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref m)) return ((long)(m.TotalPhys / 1048576), (long)(m.AvailPhys / 1048576));
        }
        var gc = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576;
        return (gc, gc);
    }

    private static long Kb(string line) => long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
}
