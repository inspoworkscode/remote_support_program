using System.Runtime.InteropServices;
using RemoteSupport.Host;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// noticeprobe - "Being controlled remotely" (2026-10-06, the free version, Stage E).
//
// Clint: "Lets add Being Controlled Remotely." A free computer has no tray app, so this bar is the only
// thing on its screen that says somebody is connected. It must be ON the screen and NOT in the
// technician's picture (it would cover what they are working on). So, as privacyprobe does for the
// privacy screen, this captures THIS laptop through the host's two capture paths with a MAGENTA window
// under where the bar sits:
//
//   control  the bar WITHOUT capture exclusion   -> capture sees the AMBER bar there, words inside it
//   test     the bar as the product shows it     -> capture sees MAGENTA there
//
// The control is what makes "magenta" mean something. It also checks: one bar per monitor, at the top
// centre and inside it; clicks pass through; a topmost window opened afterwards is pushed back under;
// the words name everyone connected; changing them and hiding it leave nothing behind; and the agent's
// real command line carries --notice for a session that asks for it and not for one that does not.
//
// A small amber bar appears at the top of this laptop's screens a few times, a second or two each.
//
//   dotnet run -c Debug --project tools/noticeprobe/noticeprobe.csproj
// ---------------------------------------------------------------------------------------------

internal static class NoticeProbe
{
    private static int pass, fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Console.WriteLine("noticeprobe - \"Being controlled remotely\", against both capture paths");

        // ---- the words, and the agent's command line ------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("== the words, and what reaches the host ==");
        Check("one technician is named", ControlledNotice.Wording(new[] { "Fran Free" }) == "Being controlled remotely by Fran Free");
        Check("two are both named", ControlledNotice.Wording(new[] { "Fran Free", "Sam" }) == "Being controlled remotely by Fran Free, Sam");
        var free = RemoteSupport.Agent.AgentService.HostArguments(new StartSessionMsg { SessionId = "s", HostToken = "t", TechnicianName = "Fran Free", ShowNotice = true }, "ws://x", false);
        var full = RemoteSupport.Agent.AgentService.HostArguments(new StartSessionMsg { SessionId = "s", HostToken = "t", TechnicianName = "Fran Free" }, "ws://x", false);
        Check("a session that asks for the notice starts the host with --notice", free.Contains(" --notice"), free);
        Check("and one that does not, without it", !full.Contains("--notice"), full);

        var mons = ScreenCapture.EnumerateMonitors();
        var prim = mons.First(m => m.Primary);
        const string Words = "Being controlled remotely by Fran Free";

        // Where the bar goes on the primary monitor, learned from a first showing, so the magenta
        // window can be put under it.
        Rectangle bar;
        using (var first = new ControlledNotice())
        {
            first.Set(Words);
            Pump(400);
            bar = first.Bars.FirstOrDefault(b => b.IntersectsWith(new Rectangle(prim.X, prim.Y, prim.Width, prim.Height)));
            first.Set("");
        }
        Pump(300);
        if (bar.IsEmpty) { Check("the bar came up at all", false); return 1; }

        using var magenta = new Form
        {
            FormBorderStyle = FormBorderStyle.None, BackColor = Color.Magenta, TopMost = true, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, AutoScaleMode = AutoScaleMode.None,
            Bounds = Rectangle.Inflate(bar, 40, 40)
        };
        magenta.Show();
        Pump(500);
        magenta.Bounds = Rectangle.Inflate(bar, 40, 40);
        Pump(300);
        // Sampled inside the bar, clear of its rounded ends.
        var inside = Rectangle.Inflate(bar, -bar.Height, -bar.Height / 4);

        Console.WriteLine();
        Console.WriteLine("== the magenta window, on its own ==");
        Check("GDI capture sees it", Look(new ScreenCapture(), prim, inside) == "magenta", Look(new ScreenCapture(), prim, inside));

        Console.WriteLine();
        Console.WriteLine("== control: the bar WITHOUT exclusion ==");
        using (var ctl = new ControlledNotice())
        {
            ctl.Set(Words, excludeFromCapture: false);
            Pump(1300);   // past two turns of its keep-on-top timer, so it is above the magenta window
            var g = Look(new ScreenCapture(), prim, inside, wordsAllowed: true);
            Check("GDI capture sees the AMBER bar over the magenta — it really is on top", g == "amber", g);
            var d = LookDxgi(prim, inside, wordsAllowed: true);
            Check("Desktop Duplication sees it too", d == "amber", d);
            var (left, right, words) = Margins(prim, bar);
            Check("the words are inside it, with amber either side — they fit", words && left == "amber" && right == "amber", $"left {left}, right {right}, words {(words ? "seen" : "NOT seen")}");
            Check("(the control is not hidden from capture)", !ctl.ExcludedFromCapture);
        }
        Pump(400);

        Console.WriteLine();
        Console.WriteLine("== the bar, as the product shows it ==");
        var n = new ControlledNotice();
        n.Set(Words);
        Pump(1300);
        Check("it is up, and says so", n.On && n.Message == Words, n.Message);
        Check("it is hidden from capture", n.ExcludedFromCapture);
        var g2 = Look(new ScreenCapture(), prim, inside);
        Check("GDI capture sees THROUGH it — magenta", g2 == "magenta", g2);
        var d2 = LookDxgi(prim, inside);
        Check("Desktop Duplication sees THROUGH it — magenta", d2 == "magenta", d2);

        var wins = n.Windows;
        var screens = mons.Select(m => new Rectangle(m.X, m.Y, m.Width, m.Height)).ToList();
        var rects = wins.Select(h => { GetWindowRect(h, out var r); return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); }).ToList();
        Check($"one bar per monitor ({mons.Count})", wins.Count == mons.Count, $"{wins.Count}");
        Check("each at the top centre of a monitor, and inside it", rects.All(r => screens.Any(s =>
                s.Contains(r) && Math.Abs((r.Left + r.Right) / 2 - (s.Left + s.Right) / 2) <= 2 && r.Top - s.Top < s.Height / 20)),
            string.Join(" ", rects.Select(R)));
        var ex = wins.Select(h => GetWindowLong(h, -20 /*GWL_EXSTYLE*/)).ToList();
        Check("topmost, click-through and never activated", ex.All(e => (e & 0x8) != 0 && (e & 0x20) != 0 && (e & 0x8000000) != 0));
        var hit = WindowFromPoint(new POINT { X = bar.X + bar.Width / 2, Y = bar.Y + bar.Height / 2 });
        Check("a click on it reaches what is underneath", GetAncestor(hit, 2) == magenta.Handle,
            wins.Contains(hit) ? "the bar took it" : GetAncestor(hit, 2) == magenta.Handle ? "the magenta window got it" : "another window got it");
        using (var late = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = Color.Lime, TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = bar })
        {
            late.Show();
            Pump(1300);
            var primaryBar = wins[Math.Max(0, rects.FindIndex(r => r.IntersectsWith(bar)))];
            Check("a topmost window opened afterwards is pushed back under it", Above(primaryBar, late.Handle));
        }

        n.Set(ControlledNotice.Wording(new[] { "Fran Free", "Sam" }));
        Pump(500);
        Check("a second technician joining: the words name both", n.Message == "Being controlled remotely by Fran Free, Sam", n.Message);
        Check("and the first bars are gone, not left beside the new", wins.All(h => !IsWindow(h)));
        var wins2 = n.Windows;
        n.Set("");
        Pump(300);
        Check("everybody gone: nothing left on the screen", !n.On && wins2.All(h => !IsWindow(h)));
        n.Dispose();

        magenta.Close();
        Console.WriteLine();
        Console.WriteLine($"{pass} of {pass + fail} checks passed.");
        return fail == 0 ? 0 : 1;
    }

    private static string R(Rectangle r) => $"{r.Width}x{r.Height}@{r.X},{r.Y}";

    private static string Name(Color c) =>
        c.R > 190 && c.G < 70 && c.B > 190 ? "magenta"
        : c.R > 150 && c.G > 60 && c.G < 120 && c.B < 50 ? "amber"
        : c.R > 220 && c.G > 220 && c.B > 220 ? "white"
        : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>One frame of the primary monitor as a bitmap, through a capture path.</summary>
    private static Bitmap? Frame(IScreenSource src)
    {
        using (src)
        {
            src.Refresh();
            int idx = src.Monitors.FindIndex(m => m.Primary);
            List<(int x, int y, int w, int h, byte[] jpeg)> tiles = new();
            for (int i = 0; i < 4; i++) { tiles = src.CaptureTiles(idx, 95, true); if (tiles.Count > 0) break; Thread.Sleep(100); }
            if (tiles.Count == 0) return null;
            var m = src.Monitors[idx];
            var bmp = new Bitmap(m.Width, m.Height);
            using var g = Graphics.FromImage(bmp);
            foreach (var t in tiles) { using var ms = new MemoryStream(t.jpeg); using var tile = new Bitmap(ms); g.DrawImage(tile, t.x, t.y, t.w, t.h); }
            return bmp;
        }
    }

    /// <summary>
    /// The colour the capture shows inside the region, on a 5x5 grid. With <paramref name="wordsAllowed"/>,
    /// white (the bar's letters) counts with amber: a grid point may land on a letter.
    /// </summary>
    private static string Look(IScreenSource src, MonitorInfo prim, Rectangle region, bool wordsAllowed = false)
    {
        using var bmp = Frame(src);
        if (bmp == null) return "no picture";
        var seen = new List<string>();
        for (int gx = 1; gx <= 5; gx++)
            for (int gy = 1; gy <= 5; gy++)
            {
                var name = Name(bmp.GetPixel(region.X - prim.X + region.Width * gx / 6, region.Y - prim.Y + region.Height * gy / 6));
                seen.Add(wordsAllowed && name == "white" ? "amber" : name);
            }
        var distinct = seen.Where(s => !s.StartsWith('#') || !wordsAllowed).Distinct().ToList();
        // JPEG blends the letters' edges into in-between colours; in the control those are the bar too.
        if (wordsAllowed && distinct.Count == 1 && seen.Count(s => s == "amber") >= 15) return "amber";
        return seen.Distinct().Count() == 1 ? seen[0] : "mixed: " + string.Join(",", seen.Distinct());
    }

    private static string LookDxgi(MonitorInfo prim, Rectangle region, bool wordsAllowed = false)
    {
        try { return Look(new DxgiCapture(), prim, region, wordsAllowed); }
        catch (Exception ex) { return "Desktop Duplication failed: " + ex.Message; }
    }

    /// <summary>The control bar's ends (just inside the rounding, mid-height) and whether its middle has white letters.</summary>
    private static (string left, string right, bool words) Margins(MonitorInfo prim, Rectangle bar)
    {
        using var bmp = Frame(new ScreenCapture());
        if (bmp == null) return ("no picture", "no picture", false);
        // Kept, to be looked at: what the person at the computer sees.
        try { using var crop = bmp.Clone(Rectangle.Intersect(Rectangle.Inflate(new Rectangle(bar.X - prim.X, bar.Y - prim.Y, bar.Width, bar.Height), 30, 20), new Rectangle(0, 0, bmp.Width, bmp.Height)), bmp.PixelFormat); crop.Save(Path.Combine(AppContext.BaseDirectory, "notice-control.png")); } catch { }
        int y = bar.Y - prim.Y + bar.Height / 2;
        int lx = bar.X - prim.X + bar.Height / 2 + 2, rx = bar.Right - prim.X - bar.Height / 2 - 3;
        bool words = false;
        for (int x = lx; x < rx; x++) if (Name(bmp.GetPixel(x, y)) == "white") { words = true; break; }
        return (Name(bmp.GetPixel(lx, y)), Name(bmp.GetPixel(rx, y)), words);
    }

    private static bool Above(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero) return false;
        for (var h = GetWindow(a, 2 /*GW_HWNDNEXT*/); h != IntPtr.Zero; h = GetWindow(h, 2)) if (h == b) return true;
        return false;
    }

    private static void Pump(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(15); } }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
}
