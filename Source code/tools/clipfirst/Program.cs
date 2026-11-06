using System.Runtime.InteropServices;
using System.Text;
using RemoteSupport.Host;

// ---------------------------------------------------------------------------------------------
// clipfirst — BUGS #10: the first copy after a fresh logon never reached the technician
// (2026-10-06).
//
// The fault only shows on a clipboard whose sequence number is still 0 — nothing copied since the
// window station was made, which is a machine just logged on to. BUGS #10 said it could not be
// proved without restarting a test machine. It can: a NEW window station has exactly that
// clipboard. So this makes one per case, starts a copy of itself inside it (STARTUPINFO names the
// desktop), and runs the real ClipboardAccess there, nowhere near this laptop's own clipboard.
//
// The cases, each in a fresh window station:
//
//   first      nothing copied yet; the watcher starts; then the person copies.     MUST fire.
//              This is the bug. The old watcher took that copy as its starting point.
//   already    something copied BEFORE the watcher starts, then copied again.      Must NOT fire
//              for what was there (§26: the password copied at nine this morning), MUST fire
//              for the second.
//   ownwrite   nothing copied; the host itself writes (a technician's paste);
//              then the person copies.                                            Must NOT fire
//              for the host's own write, MUST fire for the person's.
//
//   dotnet run -c Debug --project tools/clipfirst/clipfirst.csproj              (simulated counter)
//   dotnet run -c Debug --project tools/clipfirst/clipfirst.csproj -- --station (ELEVATED: real window stations)
//
// Making a window station needs elevation (win32 5 otherwise), so the default run hands the REAL
// watcher a counter this program drives, through ClipboardAccess's harness parameters. The
// --station run is the proof against Windows itself.
// ---------------------------------------------------------------------------------------------

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--child") return Child(args[1], args[2]);
        if (args.Length == 0 || args[0] != "--station") return Simulated();

        Console.WriteLine("clipfirst — BUGS #10, the first copy on a clipboard nobody has used yet");
        Console.WriteLine();
        int pass = 0, fail = 0;
        void Check(string what, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
        }

        foreach (var scenario in new[] { "first", "already", "ownwrite" })
        {
            Console.WriteLine($"== {scenario} ==");
            var lines = RunInFreshStation(scenario, out var problem);
            if (lines == null) { Check($"{scenario}: ran in a fresh window station", false, problem); continue; }
            foreach (var l in lines.Where(l => l.StartsWith("note ")))
                Console.WriteLine("        " + l[5..]);
            string Get(string key) => lines.FirstOrDefault(l => l.StartsWith(key + "="))?[(key.Length + 1)..] ?? "?";

            Check($"{scenario}: the clipboard started unused (sequence 0)", Get("start") == "0", "sequence " + Get("start"));
            switch (scenario)
            {
                case "first":
                    Check("the person's FIRST copy is noticed", Get("afterCopy") == "1", $"fired {Get("afterCopy")} time(s)");
                    Check("...and read back as what they copied", Get("read") == "first copy", Get("read"));
                    break;
                case "already":
                    Check("what was on the clipboard before the session is NOT sent", Get("atStart") == "0", $"fired {Get("atStart")} time(s)");
                    Check("the next copy IS noticed", Get("afterCopy") == "1", $"fired {Get("afterCopy")} time(s)");
                    break;
                case "ownwrite":
                    Check("the host's own write is not reported as theirs", Get("afterOwn") == "0", $"fired {Get("afterOwn")} time(s)");
                    Check("the person's copy after it IS noticed", Get("afterCopy") == "1", $"fired {Get("afterCopy")} time(s)");
                    break;
            }
            Console.WriteLine();
        }
        Console.WriteLine($"{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    // ---- simulated: the real watcher, a counter driven from here ---------------------------

    private static int Simulated()
    {
        Console.WriteLine("clipfirst — BUGS #10, the first copy on a clipboard nobody has used yet (simulated counter)");
        Console.WriteLine();
        int pass = 0, fail = 0;
        void Check(string what, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
        }

        // One fake clipboard per case: a counter and a format count, as Windows would report them.
        (ClipboardAccess clip, Func<int> fired, Action<uint, int> set) Make(uint seq, int formats)
        {
            uint s = seq; int f = formats; int n = 0;
            var c = new ClipboardAccess(() => Volatile.Read(ref s), () => Volatile.Read(ref f));
            c.Changed += () => Interlocked.Increment(ref n);
            return (c, () => Volatile.Read(ref n), (ns, nf) => { Volatile.Write(ref s, ns); Volatile.Write(ref f, nf); });
        }
        // A write of the host's own, the way SetText and the --put-files helper make one.
        void HostWrites(ClipboardAccess c, Action write)
        {
            var m = typeof(ClipboardAccess).GetMethod("Writing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            m.Invoke(c, new object[] { (Func<bool>)(() => { write(); return true; }) });
        }

        Console.WriteLine("== nothing copied since logon, then the first copy ==");
        {
            var (c, fired, set) = Make(0, 0);
            Thread.Sleep(3000);
            Check("nothing is reported while nobody copies", fired() == 0, $"{fired()}");
            set(1, 1);
            Thread.Sleep(1200);
            Check("the FIRST copy is reported", fired() == 1, $"fired {fired()} time(s)");
            set(2, 1);
            Thread.Sleep(1200);
            Check("and the next one too", fired() == 2, $"fired {fired()} time(s)");
            c.Dispose();
        }

        Console.WriteLine("== the start-up zero: one read of 0 while the clipboard holds something ==");
        {
            var (c, fired, set) = Make(0, 1);
            Thread.Sleep(300);
            set(7, 1);
            Thread.Sleep(2500);
            Check("what was already there is NOT reported", fired() == 0, $"fired {fired()} time(s)");
            set(8, 1);
            Thread.Sleep(1200);
            Check("the next copy is", fired() == 1, $"fired {fired()} time(s)");
            c.Dispose();
        }

        Console.WriteLine("== a long zero, but something IS on the clipboard ==");
        {
            var (c, fired, set) = Make(0, 2);
            Thread.Sleep(3000);
            set(4, 2);
            Thread.Sleep(1200);
            Check("not believed: a zero with formats on the clipboard is not an unused clipboard", fired() == 0, $"fired {fired()} time(s)");
            c.Dispose();
        }

        Console.WriteLine("== a zero too short to believe ==");
        {
            var (c, fired, set) = Make(0, 0);
            Thread.Sleep(600);
            set(3, 1);
            Thread.Sleep(1200);
            Check("a zero shorter than two seconds is taken as the start-up blink", fired() == 0, $"fired {fired()} time(s)");
            c.Dispose();
        }

        Console.WriteLine("== something copied before the session ==");
        {
            var (c, fired, set) = Make(5, 1);
            Thread.Sleep(2500);
            Check("what was there is NOT reported (§26)", fired() == 0, $"fired {fired()} time(s)");
            set(6, 1);
            Thread.Sleep(1200);
            Check("the next copy is", fired() == 1, $"fired {fired()} time(s)");
            c.Dispose();
        }

        Console.WriteLine("== the host itself writes first (a technician's paste, or --put-files) ==");
        {
            var (c, fired, set) = Make(0, 0);
            Thread.Sleep(3000);
            HostWrites(c, () => set(1, 1));
            Thread.Sleep(1500);
            Check("the host's own write is not reported as the person's", fired() == 0, $"fired {fired()} time(s)");
            set(2, 1);
            Thread.Sleep(1200);
            Check("the person's copy after it is", fired() == 1, $"fired {fired()} time(s)");
            c.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine($"{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    // ---- inside the fresh window station ---------------------------------------------------

    private static int Child(string scenario, string outFile)
    {
        var o = new List<string>();
        try
        {
            o.Add("start=" + GetClipboardSequenceNumber());
            // The person at the machine is a second ClipboardAccess: its writes are its own, so to
            // the watcher they are somebody else's — exactly what a person's Ctrl+C is.
            using var person = new ClipboardAccess();
            Thread.Sleep(300);

            if (scenario == "already")
            {
                person.SetText("copied at nine this morning", out _);
                o.Add("note sequence after the earlier copy: " + GetClipboardSequenceNumber());
            }

            using var host = new ClipboardAccess();
            int fired = 0;
            host.Changed += () => Interlocked.Increment(ref fired);

            // Longer than any start-up blink, so the watcher has settled on what it believes.
            Thread.Sleep(3000);
            o.Add("atStart=" + fired);

            if (scenario == "ownwrite")
            {
                Interlocked.Exchange(ref fired, 0);
                host.SetText("pasted by the technician", out var err);
                if (err.Length > 0) o.Add("note host write failed: " + err);
                Thread.Sleep(1500);
                o.Add("afterOwn=" + fired);
            }

            Interlocked.Exchange(ref fired, 0);
            person.SetText(scenario == "first" ? "first copy" : "a later copy", out var perr);
            if (perr.Length > 0) o.Add("note person write failed: " + perr);
            Thread.Sleep(1500);
            o.Add("afterCopy=" + fired);
            o.Add("read=" + host.GetText());
            o.Add("note sequence at the end: " + GetClipboardSequenceNumber());
        }
        catch (Exception ex) { o.Add("note child failed: " + ex.GetType().Name + ": " + ex.Message); }
        File.WriteAllLines(outFile, o);
        return 0;
    }

    // ---- making the window station ----------------------------------------------------------

    private static List<string>? RunInFreshStation(string scenario, out string problem)
    {
        problem = "";
        var name = $"RsClipFirst{Environment.ProcessId}{scenario}";
        var old = GetProcessWindowStation();
        var ws = CreateWindowStationW(name, 0, WINSTA_ALL_ACCESS, IntPtr.Zero);
        if (ws == IntPtr.Zero) { problem = "CreateWindowStation failed, win32 " + Marshal.GetLastWin32Error(); return null; }
        try
        {
            // A desktop is made in the PROCESS's window station, so switch to it for one call.
            if (!SetProcessWindowStation(ws)) { problem = "SetProcessWindowStation failed, win32 " + Marshal.GetLastWin32Error(); return null; }
            var desk = CreateDesktopW("Default", IntPtr.Zero, IntPtr.Zero, 0, DESKTOP_ALL_ACCESS, IntPtr.Zero);
            int deskErr = Marshal.GetLastWin32Error();
            SetProcessWindowStation(old);
            if (desk == IntPtr.Zero) { problem = "CreateDesktop failed, win32 " + deskErr; return null; }
            try
            {
                var outFile = Path.Combine(Path.GetTempPath(), $"clipfirst-{scenario}-{Guid.NewGuid():N}.txt");
                var exe = Environment.ProcessPath!;
                var cmd = new StringBuilder($"\"{exe}\" --child {scenario} \"{outFile}\"");
                var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>(), lpDesktop = name + @"\Default" };
                if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_NO_WINDOW, IntPtr.Zero, null, ref si, out var pi))
                { problem = "CreateProcess failed, win32 " + Marshal.GetLastWin32Error(); return null; }
                WaitForSingleObject(pi.hProcess, 60000);
                CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                if (!File.Exists(outFile)) { problem = "the child wrote nothing"; return null; }
                var lines = File.ReadAllLines(outFile).ToList();
                File.Delete(outFile);
                return lines;
            }
            finally { CloseDesktop(desk); }
        }
        finally { CloseWindowStation(ws); }
    }

    private const uint WINSTA_ALL_ACCESS = 0x37F;
    private const uint DESKTOP_ALL_ACCESS = 0x1FF;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowStationW(string name, uint flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(IntPtr ws);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseWindowStation(IntPtr ws);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, uint flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr d);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? dir, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
}
