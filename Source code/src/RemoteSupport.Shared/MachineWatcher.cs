using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace RemoteSupport.Shared;

/// <summary>
/// Live figures for the console's Processes and Performance windows (2026-10-05), measured on the
/// customer's machine once a second while somebody is looking and not at all otherwise.
///
/// <para>Run by the AGENT where there is one — as SYSTEM it sees every process and who owns it — and
/// by the tray app otherwise, which sees the same list and the same numbers but only its own
/// person's account names. Nothing here changes anything on the machine; it only reads.</para>
///
/// <para>Where each number comes from, so nobody has to rediscover it:</para>
/// <list type="bullet">
/// <item>Processor: <c>GetSystemTimes</c>, busy over elapsed between two readings.</item>
/// <item>Memory: <c>GlobalMemoryStatusEx</c>.</item>
/// <item>Processes: ONE call to <c>NtQuerySystemInformation(SystemProcessInformation)</c>, which hands
/// back every process's times and memory without opening any of them — so a process the caller
/// could not open (another account's, or a protected one) still shows its processor and memory.
/// <c>Process.GetProcesses</c> cannot do that: its processor time opens a handle per process.</item>
/// <item>Disk: Windows' own performance counters through PDH, by their ENGLISH names (so a machine in
/// another language still finds them). Active time is the busiest disk's, as Task Manager draws one
/// graph per disk and the busiest is the one that matters; the bytes are all disks together.</item>
/// <item>Network: the adapters' own byte counts (<see cref="NetworkInterface"/>), loopback and tunnels
/// left out.</item>
/// </list>
/// </summary>
public sealed class MachineWatcher : IDisposable
{
    private readonly Action<MachineSample> _send;
    private readonly object _lock = new();
    private Timer? _timer;
    private int _tick;
    private bool _busy;

    // the previous reading of everything measured as a difference
    private long _prevIdle, _prevKernel, _prevUser;
    private long _prevStamp;
    private long _prevProcStamp;
    private readonly Dictionary<(int Pid, long Created), long> _prevProcTimes = new();
    private long _prevNetIn, _prevNetOut, _prevNetStamp;
    private readonly Dictionary<(int Pid, long Created), string> _owners = new();
    private Pdh? _pdh;

    /// <summary>How often a reading is sent. The process list rides on every other one.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    public MachineWatcher(Action<MachineSample> send) { _send = send; }

    public bool Running { get { lock (_lock) return _timer != null; } }

    /// <summary>Start or stop. Starting takes the first readings at once, so the first figures the
    /// console sees, a second later, are real rates rather than zeros.</summary>
    public void Set(bool on)
    {
        lock (_lock)
        {
            if (on == (_timer != null)) return;
            if (!on)
            {
                _timer!.Dispose(); _timer = null;
                _pdh?.Dispose(); _pdh = null;
                _prevProcTimes.Clear(); _owners.Clear();
                return;
            }
            _tick = 0;
            Prime();
            _timer = new Timer(_ => Tick(), null, Interval, Interval);
        }
    }

    public void Dispose() => Set(false);

    /// <summary>Takes the first readings that every rate is measured from. <see cref="Set"/> does it
    /// on starting; public so a harness can take one, do something, and <see cref="Read"/> without
    /// a timer reading in between.</summary>
    public void Prime()
    {
        lock (_lock)
        {
            if (_pdh == null) try { _pdh = Pdh.Open(); } catch { _pdh = null; }
            Baseline();
        }
    }

    private void Baseline()
    {
        GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);
        _prevStamp = _prevProcStamp = _prevNetStamp = Environment.TickCount64;
        (_prevNetIn, _prevNetOut) = NetBytes();
        _pdh?.Collect();
        foreach (var p in ReadProcesses()) _prevProcTimes[(p.Pid, p.Created)] = p.Times;
    }

    private void Tick()
    {
        MachineSample? sample = null;
        lock (_lock)
        {
            if (_timer == null || _busy) return;
            _busy = true;
            try { sample = Read(++_tick % 2 == 1); }
            catch { sample = null; }
            finally { _busy = false; }
        }
        if (sample != null) try { _send(sample); } catch { }
    }

    /// <summary>One reading. Public so a harness can take one without a timer.</summary>
    public MachineSample Read(bool withProcesses)
    {
        var now = Environment.TickCount64;
        var s = new MachineSample { Ts = DateTimeOffset.UtcNow, LogicalProcessors = Environment.ProcessorCount, UptimeSeconds = now / 1000 };

        GetSystemTimes(out var idle, out var kernel, out var user);
        long dIdle = idle - _prevIdle, dTotal = (kernel - _prevKernel) + (user - _prevUser);   // kernel time INCLUDES idle
        s.CpuPercent = dTotal > 0 ? Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100) : 0;
        (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);
        _prevStamp = now;

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            s.MemoryTotalMb = (long)(mem.ullTotalPhys / (1024 * 1024));
            s.MemoryUsedMb = (long)((mem.ullTotalPhys - mem.ullAvailPhys) / (1024 * 1024));
        }

        if (_pdh != null && _pdh.Collect())
        {
            s.DiskActivePercent = _pdh.BusiestDiskActive();
            s.DiskReadBytesPerSec = (long)_pdh.Value(_pdh.Read);
            s.DiskWriteBytesPerSec = (long)_pdh.Value(_pdh.Write);
        }

        var (netIn, netOut) = NetBytes();
        double netSecs = Math.Max(0.001, (now - _prevNetStamp) / 1000.0);
        s.NetReceiveBytesPerSec = Math.Max(0, (long)((netIn - _prevNetIn) / netSecs));
        s.NetSendBytesPerSec = Math.Max(0, (long)((netOut - _prevNetOut) / netSecs));
        (_prevNetIn, _prevNetOut, _prevNetStamp) = (netIn, netOut, now);
        // Private or Public: asked of Windows every few seconds, not every second — it changes when
        // somebody changes it, and asking is a COM round trip.
        if (_networks == null || now - _networksAt >= 5000) { _networks = ReadNetworks(); _networksAt = now; }
        s.Networks = _networks;

        var procs = ReadProcesses();
        s.ProcessCount = procs.Count(p => p.Pid != 0);
        s.ThreadCount = procs.Sum(p => p.Threads);
        if (withProcesses)
        {
            // Processor time is in 100 ns units; a share of the WHOLE machine, as Task Manager shows.
            double elapsed100ns = Math.Max(1, (now - _prevProcStamp) * 10_000.0) * Environment.ProcessorCount;
            var seen = new HashSet<(int, long)>();
            var list = new List<ProcessSample>(procs.Count);
            foreach (var p in procs)
            {
                if (p.Pid == 0) continue;   // the idle "process" is the processor NOT working; it has its own graph
                var key = (p.Pid, p.Created);
                seen.Add(key);
                double cpu = _prevProcTimes.TryGetValue(key, out var before) ? Math.Clamp(100.0 * (p.Times - before) / elapsed100ns, 0, 100) : 0;
                _prevProcTimes[key] = p.Times;
                if (!_owners.TryGetValue(key, out var owner)) _owners[key] = owner = OwnerOf(p.Pid);
                list.Add(new ProcessSample { Pid = p.Pid, Name = p.Name, CpuPercent = Math.Round(cpu, 1), MemoryKb = p.PrivateBytes / 1024, User = owner });
            }
            // processes that have gone
            foreach (var gone in _prevProcTimes.Keys.Where(k => !seen.Contains(k)).ToList()) { _prevProcTimes.Remove(gone); _owners.Remove(gone); }
            _prevProcStamp = now;
            s.Processes = list;
        }
        return s;
    }

    // ------------------------------------------------------------------ processes

    private readonly record struct Proc(int Pid, string Name, long Created, long Times, long PrivateBytes, int Threads);

    private static List<Proc> ReadProcesses()
    {
        var result = new List<Proc>(300);
        int size = 512 * 1024;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buf, size, out int needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH) { size = Math.Max(size * 2, needed + 64 * 1024); continue; }
                if (status != 0) return result;
                long offset = 0;
                while (true)
                {
                    var at = buf + (nint)offset;
                    var spi = Marshal.PtrToStructure<SYSTEM_PROCESS_INFORMATION>(at);
                    int pid = (int)spi.UniqueProcessId;
                    string name = spi.ImageName.Buffer != IntPtr.Zero && spi.ImageName.Length > 0
                        ? Marshal.PtrToStringUni(spi.ImageName.Buffer, spi.ImageName.Length / 2)
                        : pid == 0 ? "System Idle Process" : "System";
                    result.Add(new Proc(pid, name, spi.CreateTime, spi.UserTime + spi.KernelTime,
                        spi.WorkingSetPrivateSize > 0 ? spi.WorkingSetPrivateSize : (long)spi.PrivatePageCount, (int)spi.NumberOfThreads));
                    if (spi.NextEntryOffset == 0) break;
                    offset += spi.NextEntryOffset;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return result;
    }

    private readonly Dictionary<string, string> _sidNames = new();

    /// <summary>The account a process runs as, by name only ("Sue", "SYSTEM"); empty when this caller
    /// may not ask — which for the tray app is every other account's processes.</summary>
    private string OwnerOf(int pid)
    {
        if (pid == 4) return "SYSTEM";
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out var tok)) return "";
            try
            {
                GetTokenInformation(tok, TokenUser, IntPtr.Zero, 0, out int len);
                if (len <= 0) return "";
                var info = Marshal.AllocHGlobal(len);
                try
                {
                    if (!GetTokenInformation(tok, TokenUser, info, len, out _)) return "";
                    var sid = Marshal.ReadIntPtr(info);   // TOKEN_USER.User.Sid
                    if (!ConvertSidToStringSid(sid, out var sidStrPtr)) return "";
                    string sidStr;
                    try { sidStr = Marshal.PtrToStringUni(sidStrPtr) ?? ""; } finally { LocalFree(sidStrPtr); }
                    if (_sidNames.TryGetValue(sidStr, out var known)) return known;
                    var name = new System.Text.StringBuilder(256); var domain = new System.Text.StringBuilder(256);
                    int nLen = name.Capacity, dLen = domain.Capacity;
                    string found = LookupAccountSid(null, sid, name, ref nLen, domain, ref dLen, out _) ? name.ToString() : "";
                    _sidNames[sidStr] = found;
                    return found;
                }
                finally { Marshal.FreeHGlobal(info); }
            }
            finally { CloseHandle(tok); }
        }
        finally { CloseHandle(h); }
    }

    // ------------------------------------------------------------------ network

    private static (long In, long Out) NetBytes()
    {
        long i = 0, o = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                try { var st = n.GetIPStatistics(); i += st.BytesReceived; o += st.BytesSent; } catch { }
            }
        }
        catch { }
        return (i, o);
    }

    private List<NetworkProfile>? _networks;
    private long _networksAt;
    /// <summary>Why the last attempt to read the networks failed, for a harness to print; null when it did not.</summary>
    public static string? NetworkError { get; private set; }

    /// <summary>
    /// The connected networks and their Private / Public / Domain label, from Windows' Network List
    /// Manager — the same answer the Settings app and <c>Get-NetConnectionProfile</c> give. Late-bound
    /// COM (no interop assembly to ship); every object released, since this runs every few seconds
    /// for as long as somebody is looking. Empty, never an exception, if Windows will not say.
    /// </summary>
    private static List<NetworkProfile> ReadNetworks()
    {
        var list = new List<NetworkProfile>();
        object? nlm = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));   // NetworkListManager
            if (type == null) return list;
            nlm = Activator.CreateInstance(type);
            if (nlm == null) return list;
            var adapters = new Dictionary<Guid, NetworkInterface>();
            try { foreach (var n in NetworkInterface.GetAllNetworkInterfaces()) if (Guid.TryParse(n.Id, out var g)) adapters[g] = n; } catch { }

            var nets = ((INetworkListManager)nlm).GetNetworks(1);   // NLM_ENUM_NETWORK_CONNECTED
            try
            {
                while (nets.Next(1, out var net, out int got) == 0 && got == 1 && net != null)
                {
                    try
                    {
                        int category = net.GetCategory();
                        var names = new List<string>(); var addresses = new List<string>();
                        var conns = net.GetNetworkConnections();
                        try
                        {
                            while (conns.Next(1, out var conn, out int one) == 0 && one == 1 && conn != null)
                            {
                                try
                                {
                                    if (!adapters.TryGetValue(conn.GetAdapterId(), out var a)) continue;
                                    if (!names.Contains(a.Name)) names.Add(a.Name);
                                    foreach (var ip in AddressesOf(a)) if (!addresses.Contains(ip)) addresses.Add(ip);
                                }
                                finally { Release(conn); }
                            }
                        }
                        finally { Release(conns); }
                        list.Add(new NetworkProfile
                        {
                            Name = net.GetName(),
                            // NLM_NETWORK_CATEGORY: 0 public, 1 private, 2 domain authenticated
                            Category = category switch { 1 => "Private", 2 => "Domain", _ => "Public" },
                            Adapter = string.Join(", ", names),
                            Address = string.Join(", ", addresses)
                        });
                    }
                    finally { Release(net); }
                }
            }
            finally { Release(nets); }
            NetworkError = null;
        }
        catch (Exception ex) { NetworkError = ex.GetType().Name + ": " + ex.Message; }
        finally { Release(nlm); }
        return list;

        static void Release(object? o) { try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { } }
    }

    /// <summary>The adapter's IPv4 addresses; failing those, its global IPv6 ones (never a link-local
    /// fe80:: address — it means nothing to anybody reading it).</summary>
    private static IEnumerable<string> AddressesOf(NetworkInterface a)
    {
        List<System.Net.IPAddress> all;
        try { all = a.GetIPProperties().UnicastAddresses.Select(u => u.Address).ToList(); } catch { yield break; }
        var v4 = all.Where(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList();
        var pick = v4.Count > 0 ? v4 : all.Where(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !x.IsIPv6LinkLocal).ToList();
        foreach (var x in pick) yield return x.ToString();
    }

    // ------------------------------------------------------------------ disk, through PDH

    private sealed class Pdh : IDisposable
    {
        private IntPtr _query;
        public IntPtr Idle, Read, Write;

        public static Pdh Open()
        {
            var p = new Pdh();
            if (PdhOpenQuery(null, IntPtr.Zero, out p._query) != 0) throw new InvalidOperationException("PdhOpenQuery");
            PdhAddEnglishCounter(p._query, @"\PhysicalDisk(*)\% Idle Time", IntPtr.Zero, out p.Idle);
            PdhAddEnglishCounter(p._query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out p.Read);
            PdhAddEnglishCounter(p._query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out p.Write);
            PdhCollectQueryData(p._query);
            return p;
        }

        public bool Collect() => _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0;

        public double Value(IntPtr counter)
        {
            if (counter == IntPtr.Zero) return 0;
            return PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, out var v) == 0 && v.CStatus <= 1 ? Math.Max(0, v.doubleValue) : 0;
        }

        /// <summary>100 minus the LEAST idle physical disk's idle time; -1 when there is no answer.</summary>
        public double BusiestDiskActive()
        {
            if (Idle == IntPtr.Zero) return -1;
            int bytes = 0, count = 0;
            int st = PdhGetFormattedCounterArray(Idle, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bytes, out count, IntPtr.Zero);
            if (st != PDH_MORE_DATA || bytes <= 0) return -1;
            var buf = Marshal.AllocHGlobal(bytes);
            try
            {
                if (PdhGetFormattedCounterArray(Idle, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bytes, out count, buf) != 0) return -1;
                // PDH_FMT_COUNTERVALUE_ITEM_W: a name pointer, then {DWORD CStatus; double value},
                // the double aligned to 8 — 16 bytes a value on 64-bit.
                int valueAt = (IntPtr.Size + 4 + 7) & ~7;
                int itemSize = valueAt + 8;
                double best = -1;
                for (int k = 0; k < count; k++)
                {
                    var item = buf + k * itemSize;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    if (name == "_Total") continue;
                    if ((uint)Marshal.ReadInt32(item, IntPtr.Size) > 1) continue;   // not a valid reading
                    double idle = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, valueAt));
                    best = Math.Max(best, Math.Clamp(100 - idle, 0, 100));
                }
                return best;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public void Dispose() { if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; } }
    }

    // ------------------------------------------------------------------ Network List Manager (netlistmgr.h)
    // Dual interfaces: only the methods up to the last one called are declared, in vtable order —
    // the order is the contract, the names are not. Late binding (dynamic) was tried first and Windows
    // refused it with E_INVALIDARG, measured by watchcheck.

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        IEnumNetworks GetNetworks(int flags);
    }

    [ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworks
    {
        [return: MarshalAs(UnmanagedType.Interface)] object NewEnum();
        [PreserveSig] int Next(int celt, out INetwork? item, out int fetched);
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        IEnumNetworkConnections GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
        short IsConnectedToInternet();
        short IsConnected();
        int GetConnectivity();
        int GetCategory();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)] object NewEnum();
        [PreserveSig] int Next(int celt, out INetworkConnection? item, out int fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        short IsConnectedToInternet();
        short IsConnected();
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    // ------------------------------------------------------------------ native

    private const int SystemProcessInformation = 5;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;
    private const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    /// <summary>The documented head of the structure, as far as Private page count; laid out by the
    /// marshaller, so its padding is right on 32- and 64-bit alike.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public UIntPtr UniqueProcessKey;
        public UIntPtr PeakVirtualSize;
        public UIntPtr VirtualSize;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivatePageCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int length, out int returnLength);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr str);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupAccountSid(string? system, IntPtr sid, System.Text.StringBuilder name, ref int nameLen, System.Text.StringBuilder domain, ref int domainLen, out int use);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref int bufferSize, out int itemCount, IntPtr items);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
}
