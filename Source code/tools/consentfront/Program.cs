using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RemoteSupport.Shared;

namespace RemoteSupport.Tools.ConsentFront;

/// <summary>
/// Is the customer's Allow prompt IN FRONT when it appears? (2026-10-16.) Clint: "the allow is behind other
/// open screens". This shows the host's REAL prompt through <c>ConsentPrompt.Ask</c>, from a process that is
/// NOT the foreground one (the host never is), and reads it back from where the customer stands: is the window
/// at its middle and four corners the prompt, does it have the keyboard, and what is above it. The stand-ins
/// for the customer's other programs are a second copy of this tool (<c>--blocker</c>).
/// Leave the mouse and keyboard alone while it runs. Exit code 0 = every check passed.
/// </summary>
internal static class Program
{
    private static int _pass, _fail;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--blocker") return Blocker(args.Length > 1 && args[1] == "topmost");
        AttachConsole(-1);

        Say($"consentfront - foreground before we start: {Describe(GetForegroundWindow())}");
        Case("1. this desktop as it is (another program in front)", blocker: null);
        Case("2. another program's ordinary window just clicked, over the middle", blocker: "normal");
        Case("3. another program's always-on-top window over the middle", blocker: "topmost");
        Case("4. an always-on-top window that opens WHILE the question waits", blocker: "topmost", blockerAfter: true);
        Say($"\n{_pass} of {_pass + _fail} passed");
        return _fail == 0 ? 0 : 1;
    }

    private static void Case(string name, string? blocker, bool blockerAfter = false)
    {
        Say($"\n-- {name}");
        Process? b = null;
        if (blocker is not null && !blockerAfter)
        {
            b = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--blocker {blocker}") { UseShellExecute = false });
            Thread.Sleep(2500);
            Say($"   foreground with the stand-in up: {Describe(GetForegroundWindow())}");
        }
        try
        {
            var brand = Branding.Load();
            string title = $"{brand.CompanyName} — {brand.ProductName}";
            using var withdraw = new CancellationTokenSource();
            var ask = typeof(RemoteSupport.Host.HostEngine).Assembly.GetType("RemoteSupport.Host.ConsentPrompt", true)!.GetMethod("Ask")!;
            var asking = Task.Run(() => ask.Invoke(null, new object[] { brand, "Sue Tech", 30, withdraw.Token, "" }));

            IntPtr h = IntPtr.Zero;
            var sw = Stopwatch.StartNew();
            while (h == IntPtr.Zero && sw.ElapsedMilliseconds < 10000) { Thread.Sleep(5); h = FindOurs(title); }
            Check(h != IntPtr.Zero, "the prompt appeared");
            if (h != IntPtr.Zero)
            {
                // What the customer sees a moment after it appears — and, for diagnosis, every change of its
                // always-on-top mark on the way.
                bool? was = null;
                for (var t = Stopwatch.StartNew(); t.ElapsedMilliseconds < 1500; Thread.Sleep(5))
                {
                    bool now = (GetWindowLong(h, -20) & 0x8) != 0;
                    if (now != was) Say($"   {t.ElapsedMilliseconds,5} ms  always-on-top {(now ? "on" : "OFF")}");
                    was = now;
                }
                if (blocker is not null && blockerAfter)
                {
                    b = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--blocker {blocker}") { UseShellExecute = false });
                    Thread.Sleep(3000);   // the stand-in is up, and the prompt has had a tick of its countdown
                    Say($"   foreground with the stand-in up: {Describe(GetForegroundWindow())}");
                }
                Check((GetWindowLong(h, -20) & 0x8) != 0, "it is always-on-top (WS_EX_TOPMOST)");
                var fg = GetForegroundWindow();
                // Case 4: the stand-in was clicked after the prompt came up, so the keyboard is theirs — the
                // prompt must stay in sight, not snatch it back.
                if (!blockerAfter) Check(fg == h, $"it has the keyboard (foreground is {Describe(fg)})");
                GetWindowRect(h, out var r);
                var pts = new[] { ((r.L + r.R) / 2, (r.T + r.B) / 2), (r.L + 14, r.T + 14), (r.R - 14, r.T + 14), (r.L + 14, r.B - 14), (r.R - 14, r.B - 14) };
                int seen = 0; string covered = "";
                foreach (var (x, y) in pts)
                {
                    var at = GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2 /* GA_ROOT */);
                    // Windows' own notifications sit above everything by design, the prompt included; one
                    // arriving during a run is this desktop's, not a fault (it failed a corner on 2026-10-16).
                    if (at == h || ProcessOf(at) == "ShellExperienceHost") seen++; else covered = Describe(at);
                }
                Check(seen == pts.Length, $"it is what the customer sees at its middle and corners ({seen} of {pts.Length}{(covered.Length > 0 ? $"; covered by {covered}" : "")})");
                Say($"   above it, overlapping: {Above(h)}");
                Say($"   its owner: {Describe(GetWindow(h, 4 /* GW_OWNER */))}");
            }
            withdraw.Cancel();
            asking.Wait(5000);
        }
        finally
        {
            if (b is { HasExited: false }) { try { b.Kill(); } catch { } }
            Thread.Sleep(700);
        }
    }

    /// <summary>A stand-in for the customer's other programs, over the middle of the screen: an ordinary
    /// window they just clicked, or an always-on-top one.</summary>
    private static int Blocker(bool topmost)
    {
        Application.EnableVisualStyles();
        var f = new Form
        {
            Text = topmost ? "consentfront stand-in (always on top)" : "consentfront stand-in (ordinary)",
            StartPosition = FormStartPosition.CenterScreen, Size = new Size(900, 700), TopMost = topmost, BackColor = Color.DarkSlateGray,
        };
        f.Shown += (_, _) =>
        {
            // As if the person had just clicked it: a click gives a window the foreground; a program
            // started from a background one does not get it by itself.
            var fg = GetForegroundWindow();
            int fgThread = GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
            AttachThreadInput(me, fgThread, true);
            SetForegroundWindow(f.Handle); f.Activate();
            AttachThreadInput(me, fgThread, false);
        };
        Application.Run(f);
        return 0;
    }

    private static IntPtr FindOurs(string title)
    {
        IntPtr found = IntPtr.Zero; int me = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out int pid);
            if (pid == me && IsWindowVisible(h) && Title(h) == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string Above(IntPtr h)
    {
        GetWindowRect(h, out var me);
        var names = new List<string>();
        for (var w = GetWindow(h, 3 /* GW_HWNDPREV */); w != IntPtr.Zero; w = GetWindow(w, 3))
        {
            if (!IsWindowVisible(w) || !GetWindowRect(w, out var r)) continue;
            if (r.R <= me.L || r.L >= me.R || r.B <= me.T || r.T >= me.B) continue;
            DwmGetWindowAttribute(w, 14 /* DWMWA_CLOAKED */, out int cloaked, 4);
            if (cloaked != 0) continue;
            names.Add(Describe(w));
        }
        return names.Count == 0 ? "nothing" : string.Join("; ", names);
    }

    private static string ProcessOf(IntPtr h)
    {
        GetWindowThreadProcessId(h, out int pid);
        try { return Process.GetProcessById(pid).ProcessName; } catch { return "?"; }
    }

    private static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(none)";
        string p = ProcessOf(h);
        return $"\"{Title(h)}\" [{p}{((GetWindowLong(h, -20) & 0x8) != 0 ? ", topmost" : "")}]";
    }

    private static string Title(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }

    private static void Say(string s) { Console.WriteLine(s); }

    private static void Check(bool ok, string what)
    {
        Say($"   {(ok ? "PASS" : "FAIL")}  {what}");
        if (ok) _pass++; else _fail++;
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern int GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(int a, int b, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int size);
}
