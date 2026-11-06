using System.Runtime.InteropServices;

namespace RemoteSupport.Host;

/// <summary>
/// The customer's prints on the TECHNICIAN's printer — remote printing, the first direction (roadmap
/// 1.8, 2026-10-06; Clint chose both directions).
///
/// <para>While a technician has "Print on my printer" on, the customer's machine has a printer called
/// <c>Sue Tech's printer (Coastal IT Remote Support)</c>. It is Windows' own "Microsoft Print to PDF"
/// driver with a FILE port, so what is printed to it becomes a PDF in the person's temp folder — no
/// driver of ours, nothing installed. The host picks each finished PDF up and the console prints it.
/// The printer goes when the technician switches it off, when the session ends, and — for a host that
/// died — at the next host's start (<see cref="SweepLeftovers"/>).</para>
///
/// <para>The port file is written by the spooler AS THE PERSON who printed (since PrintDemon,
/// CVE-2020-1048), so it lives under their temp, where they may write and the SYSTEM host may read.</para>
///
/// <para>One known limit, reported rather than hidden: the file port writes the same file for every
/// job, so two documents printed within a moment of each other can arrive as one. Every job seen in
/// the queue is counted; one that never produced a file is said to the technician, so they can ask
/// for it again.</para>
/// </summary>
public sealed class PrintRedirect : IDisposable
{
    /// <summary>A finished print: the technician it is for, the PDF (moved aside, the caller deletes
    /// it), and the document's name as the program that printed it gave it.</summary>
    public event Action<string, string, string>? JobReady;
    /// <summary>Jobs that were seen in a technician's queue and never became a file.</summary>
    public event Action<string, int>? JobsLost;

    private sealed class Redirect
    {
        public string Technician = "", Printer = "", Port = "";
        public readonly HashSet<uint> SeenJobs = new();
        public readonly Dictionary<uint, string> Names = new();
        public int FilesTaken;
        public long LastSize = -1;
        public string LastName = "";
    }

    private readonly Dictionary<string, Redirect> _on = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly System.Threading.Timer _poll;
    private readonly string _outRoot;

    /// <summary>The folder the port files go in: the person's temp, <c>RemoteSupport\print\out</c>.</summary>
    public PrintRedirect(string? outRoot = null)
    {
        _outRoot = outRoot ?? OutRoot();
        _poll = new System.Threading.Timer(_ => Poll(), null, 250, 250);
    }

    public static string OutRoot() =>
        Path.Combine(UserContext.ProfileDirectory, "AppData", "Local", "Temp", "RemoteSupport", "print", "out");

    /// <summary>
    /// The printer's name for a technician: theirs, and whose product it is, so the person choosing it
    /// in Word knows exactly where the paper will come out. Printer names may not hold a comma or a
    /// backslash, and are kept well under the spooler's limit.
    /// </summary>
    public static string PrinterName(string technician, string product)
    {
        static string Clean(string s) => new string(s.Where(c => c != ',' && c != '\\' && c != '!' && !char.IsControl(c)).ToArray()).Trim();
        var who = Clean(technician); var what = Clean(product);
        if (who.Length == 0) who = "The technician";
        var name = what.Length > 0 ? $"{who}'s printer ({what})" : $"{who}'s printer";
        return name.Length > 120 ? name[..120] : name;
    }

    /// <summary>Adds the technician's printer. Returns its name, or null with the reason.</summary>
    public string? Add(string technician, string product, out string error)
    {
        error = "";
        lock (_lock) if (_on.TryGetValue(technician, out var have)) return have.Printer;
        var printer = PrinterName(technician, product);
        try { Directory.CreateDirectory(_outRoot); } catch (Exception ex) { error = "could not make the print folder: " + ex.Message; return null; }
        var port = Path.Combine(_outRoot, $"{Slug(technician)}.pdf");
        try { if (File.Exists(port)) File.Delete(port); } catch { }

        // Anything of the same name from a host that died first; then the port, then the printer.
        Delete(printer, port, quiet: true);
        if (!Spooler.AddPort(port, out error)) return null;
        if (!Spooler.AddPrinter(printer, port, $"Remote support: prints on {technician}'s printer. Removed when the session ends.", out error))
        {
            Spooler.DeletePort(port, out _);
            return null;
        }
        lock (_lock) _on[technician] = new Redirect { Technician = technician, Printer = printer, Port = port };
        HostTrace.Write($"print: added \"{printer}\" -> {port}");
        return printer;
    }

    /// <summary>Removes one technician's printer.</summary>
    public void Remove(string technician)
    {
        Redirect? r;
        lock (_lock) { if (!_on.Remove(technician, out r)) return; }
        Delete(r.Printer, r.Port, quiet: false);
    }

    /// <summary>Every printer this host added. At session end.</summary>
    public void RemoveAll()
    {
        List<string> who; lock (_lock) who = _on.Keys.ToList();
        foreach (var t in who) Remove(t);
    }

    public bool IsOn(string technician) { lock (_lock) return _on.ContainsKey(technician); }

    /// <summary>The printer's name on this machine for a technician, or "".</summary>
    public string PrinterOf(string technician) { lock (_lock) return _on.TryGetValue(technician, out var r) ? r.Printer : ""; }

    /// <summary>Every technician not in <paramref name="stillHere"/> loses their printer — they have left the session.</summary>
    public void KeepOnly(IEnumerable<string> stillHere)
    {
        var keep = new HashSet<string>(stillHere, StringComparer.OrdinalIgnoreCase);
        List<string> gone; lock (_lock) gone = _on.Keys.Where(t => !keep.Contains(t)).ToList();
        foreach (var t in gone) Remove(t);
    }

    /// <summary>A finished job moved aside for sending — so the pull that sends it is not also reported as a copy.</summary>
    public static bool IsJobFile(string? path, string? outRoot = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(outRoot ?? OutRoot()).TrimEnd('\\') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("job-", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Printers an earlier host added and never removed — it was killed, or the machine lost power
    /// mid-session. Recognised by their port being a file under the print folder, never by name alone.
    /// </summary>
    public static int SweepLeftovers(string? outRoot = null)
    {
        var root = outRoot ?? OutRoot();
        int n = 0;
        foreach (var (name, port) in Spooler.Printers())
            if (port.StartsWith(root, StringComparison.OrdinalIgnoreCase) && port.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                Delete(name, port, quiet: false); n++;
            }
        return n;
    }

    private static void Delete(string printer, string port, bool quiet)
    {
        if (!Spooler.DeletePrinter(printer, out var e1) && !quiet) HostTrace.Write($"print: could not remove \"{printer}\": {e1}");
        if (!Spooler.DeletePort(port, out var e2) && !quiet) HostTrace.Write($"print: could not remove port {port}: {e2}");
        try { if (File.Exists(port)) File.Delete(port); } catch { }
        if (!quiet) HostTrace.Write($"print: removed \"{printer}\"");
    }

    private static string Slug(string s)
    {
        var h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[..10];
        return "tech-" + h;
    }

    /// <summary>
    /// Four times a second: who is printing (for the document's name and the count), and whether a
    /// finished PDF is waiting. Finished means the spooler has closed it — it opens exclusively — and
    /// it has stopped growing.
    /// </summary>
    private void Poll()
    {
        List<Redirect> all; lock (_lock) all = _on.Values.ToList();
        foreach (var r in all)
        {
            try
            {
                foreach (var (id, doc) in Spooler.Jobs(r.Printer))
                {
                    if (r.SeenJobs.Add(id)) HostTrace.Write($"print: job {id} \"{doc}\" on \"{r.Printer}\"");
                    r.Names[id] = doc; r.LastName = doc;
                }
                if (!File.Exists(r.Port)) { r.LastSize = -1; continue; }
                long size = new FileInfo(r.Port).Length;
                if (size == 0 || size != r.LastSize) { r.LastSize = size; continue; }
                string taken;
                try
                {
                    using (File.Open(r.Port, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                    taken = Path.Combine(Path.GetDirectoryName(r.Port)!, $"job-{DateTime.Now:HHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}.pdf");
                    File.Move(r.Port, taken);
                }
                catch { continue; }   // still being written
                r.LastSize = -1;
                r.FilesTaken++;
                var name = r.LastName.Length > 0 ? r.LastName : "a document";
                HostTrace.Write($"print: \"{name}\" for {r.Technician}, {size:N0} bytes");
                try { JobReady?.Invoke(r.Technician, taken, name); } catch (Exception ex) { HostTrace.Write("print: handler failed: " + ex.Message); }
                // More jobs seen than files taken, once the queue is empty: some were overwritten.
                if (Spooler.Jobs(r.Printer).Count == 0 && r.SeenJobs.Count > r.FilesTaken)
                {
                    int lost = r.SeenJobs.Count - r.FilesTaken;
                    r.FilesTaken = r.SeenJobs.Count;
                    try { JobsLost?.Invoke(r.Technician, lost); } catch { }
                }
            }
            catch (Exception ex) { HostTrace.Write("print: poll failed: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        _poll.Dispose();
        RemoveAll();
    }
}

/// <summary>The print spooler, through winspool. Adding and removing ports and printers needs administrator rights — the host is SYSTEM.</summary>
public static class Spooler
{
    public const string PdfDriver = "Microsoft Print To PDF";

    public static bool AddPort(string port, out string error)
    {
        error = "";
        var pd = new PRINTER_DEFAULTS { DesiredAccess = SERVER_ACCESS_ADMINISTER };
        if (!OpenPrinter(",XcvMonitor Local Port", out var xcv, ref pd)) { error = Win32("open the local port monitor"); return false; }
        try
        {
            var data = System.Text.Encoding.Unicode.GetBytes(port + "\0");
            if (!XcvData(xcv, "AddPort", data, (uint)data.Length, IntPtr.Zero, 0, out _, out var status)) { error = Win32("add the port"); return false; }
            // 183, ERROR_ALREADY_EXISTS: a port of that name is there already, which is what is wanted.
            if (status != 0 && status != 183) { error = $"the spooler would not add the port (error {status})"; return false; }
            return true;
        }
        finally { ClosePrinter(xcv); }
    }

    public static bool DeletePort(string port, out string error)
    {
        error = "";
        var pd = new PRINTER_DEFAULTS { DesiredAccess = SERVER_ACCESS_ADMINISTER };
        if (!OpenPrinter(",XcvMonitor Local Port", out var xcv, ref pd)) { error = Win32("open the local port monitor"); return false; }
        try
        {
            var data = System.Text.Encoding.Unicode.GetBytes(port + "\0");
            if (!XcvData(xcv, "DeletePort", data, (uint)data.Length, IntPtr.Zero, 0, out _, out var status)) { error = Win32("remove the port"); return false; }
            if (status != 0) { error = $"error {status}"; return false; }
            return true;
        }
        finally { ClosePrinter(xcv); }
    }

    public static bool AddPrinter(string name, string port, string comment, out string error)
    {
        error = "";
        var info = new PRINTER_INFO_2
        {
            pPrinterName = name, pPortName = port, pDriverName = PdfDriver, pComment = comment,
            pPrintProcessor = "winprint", pDatatype = "RAW", Attributes = 0x40 /* LOCAL */
        };
        var h = AddPrinterW(null, 2, ref info);
        if (h == IntPtr.Zero)
        {
            int e = Marshal.GetLastWin32Error();
            error = e == 1797 ? $"this computer does not have the \"{PdfDriver}\" printer driver"   // ERROR_UNKNOWN_PRINTER_DRIVER
                  : e == 5 ? "only an administrator can add a printer (is the support service running?)"
                  : $"Windows would not add the printer (error {e})";
            return false;
        }
        ClosePrinter(h);
        return true;
    }

    public static bool DeletePrinter(string name, out string error)
    {
        error = "";
        var pd = new PRINTER_DEFAULTS { DesiredAccess = PRINTER_ALL_ACCESS };
        if (!OpenPrinter(name, out var h, ref pd)) { error = Win32("open the printer"); return false; }
        try
        {
            // Jobs still queued would keep it alive as "deleting"; there is no paper to save them for.
            SetPrinter(h, 0, IntPtr.Zero, 3 /* PRINTER_CONTROL_PURGE */);
            if (!DeletePrinterW(h)) { error = Win32("remove the printer"); return false; }
            return true;
        }
        finally { ClosePrinter(h); }
    }

    /// <summary>Every local printer with its port.</summary>
    public static List<(string Name, string Port)> Printers()
    {
        var list = new List<(string, string)>();
        EnumPrinters(2 /* LOCAL */, null, 2, IntPtr.Zero, 0, out uint need, out _);
        if (need == 0) return list;
        var buf = Marshal.AllocHGlobal((int)need);
        try
        {
            if (!EnumPrinters(2, null, 2, buf, need, out _, out uint n)) return list;
            int size = Marshal.SizeOf<PRINTER_INFO_2>();
            for (int i = 0; i < n; i++)
            {
                var p = Marshal.PtrToStructure<PRINTER_INFO_2>(buf + i * size);
                list.Add((p.pPrinterName ?? "", p.pPortName ?? ""));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    /// <summary>The jobs in a printer's queue: id and document name.</summary>
    public static List<(uint Id, string Document)> Jobs(string printer)
    {
        var list = new List<(uint, string)>();
        var pd = new PRINTER_DEFAULTS { DesiredAccess = PRINTER_ACCESS_USE };
        if (!OpenPrinter(printer, out var h, ref pd)) return list;
        try
        {
            EnumJobs(h, 0, 64, 1, IntPtr.Zero, 0, out uint need, out _);
            if (need == 0) return list;
            var buf = Marshal.AllocHGlobal((int)need);
            try
            {
                if (!EnumJobs(h, 0, 64, 1, buf, need, out _, out uint n)) return list;
                int size = Marshal.SizeOf<JOB_INFO_1>();
                for (int i = 0; i < n; i++)
                {
                    var j = Marshal.PtrToStructure<JOB_INFO_1>(buf + i * size);
                    list.Add((j.JobId, j.pDocument ?? ""));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { ClosePrinter(h); }
        return list;
    }

    private static string Win32(string what) => $"could not {what} (win32 {Marshal.GetLastWin32Error()})";

    private const uint SERVER_ACCESS_ADMINISTER = 0x00000001;
    private const uint PRINTER_ACCESS_USE = 0x00000008;
    private const uint PRINTER_ALL_ACCESS = 0x000F000C;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_DEFAULTS { public string? pDatatype; public IntPtr pDevMode; public uint DesiredAccess; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_2
    {
        public string? pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation;
        public IntPtr pDevMode;
        public string? pSepFile, pPrintProcessor, pDatatype, pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOB_INFO_1
    {
        public uint JobId;
        public string? pPrinterName, pMachineName, pUserName, pDocument, pDatatype, pStatus;
        public uint Status, Priority, Position, TotalPages, PagesPrinted;
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenPrinterW")]
    private static extern bool OpenPrinter(string name, out IntPtr h, ref PRINTER_DEFAULTS pd);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool ClosePrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool XcvData(IntPtr xcv, string dataName, byte[] input, uint cbInput, IntPtr output, uint cbOutput, out uint needed, out uint status);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddPrinterW(string? server, uint level, ref PRINTER_INFO_2 info);
    [DllImport("winspool.drv", SetLastError = true, EntryPoint = "DeletePrinter")] private static extern bool DeletePrinterW(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool SetPrinter(IntPtr h, uint level, IntPtr info, uint command);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumPrintersW")]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buf, uint cb, out uint needed, out uint returned);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumJobsW")]
    private static extern bool EnumJobs(IntPtr h, uint first, uint count, uint level, IntPtr buf, uint cb, out uint needed, out uint returned);
}
