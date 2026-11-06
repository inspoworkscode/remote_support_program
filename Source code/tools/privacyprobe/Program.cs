using System.Runtime.InteropServices;
using RemoteSupport.Host;

// ---------------------------------------------------------------------------------------------
// privacyprobe - roadmap 1.7: does the privacy screen black out the customer's monitors while the
// technician's picture shows what is really there?
//
// The roadmap's warning is the design: "if it fails silently the customer is looking at their
// own desktop while believing they are not ... prove it with a screenshot taken on the customer
// machine, not by eye." So this captures THIS laptop through the host's own two capture paths
// (Desktop Duplication and the GDI fallback) with a MAGENTA window on screen, and then:
//
//   control  the privacy screen WITHOUT capture exclusion   -> capture sees BLACK there
//   test     the privacy screen as the product shows it     -> capture sees MAGENTA there
//
// The control is what makes "magenta" mean something: without it, a privacy screen that never
// appeared would pass. It also checks every monitor is covered edge to edge, that clicks pass
// through to what is underneath, that a topmost window opened afterwards is pushed back under,
// and that Hide leaves nothing behind.
//
// This laptop's screens go black twice, two or three seconds each.
//
//   dotnet run -c Debug --project tools/privacyprobe/privacyprobe.csproj
// ---------------------------------------------------------------------------------------------

internal static class PrivacyProbe
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
        // Real pixels everywhere in this process, so the magenta window, the capture tiles and the
        // monitor rectangles are all in the same units.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Console.WriteLine("privacyprobe - the privacy screen, against both capture paths");
        Console.WriteLine();

        var mons = ScreenCapture.EnumerateMonitors();
        var prim = mons.First(m => m.Primary);
        // In the top fifth: the privacy screen writes its message across the middle, and grey
        // letters sampled in the control case would read as "not black".
        var region = new Rectangle(prim.X + prim.Width / 2 - 200, prim.Y + prim.Height / 10, 400, 300);

        using var magenta = new Form
        {
            FormBorderStyle = FormBorderStyle.None, BackColor = Color.Magenta, TopMost = true, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Bounds = region, AutoScaleMode = AutoScaleMode.None
        };
        magenta.Show();
        Pump(500);
        magenta.Bounds = region;
        Pump(300);

        Console.WriteLine("== the magenta window, on its own ==");
        Check("GDI capture sees it", Look(new ScreenCapture(), prim, region) == "magenta", Look(new ScreenCapture(), prim, region));
        Check("Desktop Duplication sees it", LookDxgi(prim, region) == "magenta", LookDxgi(prim, region));

        const string Msg = "Anywhere IT is working on this computer. Your screen will come back when they finish.";

        Console.WriteLine();
        Console.WriteLine("== control: the privacy screen WITHOUT exclusion ==");
        using (var ctl = new PrivacyScreen())
        {
            var err = ctl.Show(Msg, excludeFromCapture: false);
            Pump(700);
            Check("(it comes up)", err == "" && ctl.On, err);
            Check("GDI capture sees BLACK over the magenta - the window really is on top", Look(new ScreenCapture(), prim, region) == "black", Look(new ScreenCapture(), prim, region));
            Check("Desktop Duplication sees BLACK too", LookDxgi(prim, region) == "black", LookDxgi(prim, region));
            ctl.Hide();
        }
        Pump(400);

        Console.WriteLine();
        Console.WriteLine("== the privacy screen, as the product shows it ==");
        var ps = new PrivacyScreen();
        var e2 = ps.Show(Msg);
        Pump(700);
        Check("it comes up", e2 == "" && ps.On, e2);
        Check("GDI capture sees THROUGH it - magenta", Look(new ScreenCapture(), prim, region) == "magenta", Look(new ScreenCapture(), prim, region));
        Check("Desktop Duplication sees THROUGH it - magenta", LookDxgi(prim, region) == "magenta", LookDxgi(prim, region));

        var wins = ps.Windows;
        var physical = mons.Select(m => new Rectangle(m.X, m.Y, m.Width, m.Height)).ToList();
        var rects = wins.Select(h => { GetWindowRect(h, out var r); return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); }).ToList();
        Check($"one window per monitor ({mons.Count})", wins.Count == mons.Count, $"{wins.Count} window(s)");
        Check("each monitor covered edge to edge", physical.All(m => rects.Contains(m)),
            $"monitors {string.Join(" ", physical.Select(R))}; windows {string.Join(" ", rects.Select(R))}");

        var hit = WindowFromPoint(new POINT { X = region.X + 200, Y = region.Y + 150 });
        Check("a click passes through it to what is underneath", GetAncestor(hit, 2) == magenta.Handle,
            hit == IntPtr.Zero ? "nothing" : wins.Contains(hit) ? "the privacy window took it" : GetAncestor(hit, 2) == magenta.Handle ? "the magenta window got it" : "another window got it");

        using (var late = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = Color.Lime, TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(region.X, region.Y, 120, 120) })
        {
            late.Show();
            Pump(1300);   // two turns of the 500 ms keep-on-top timer
            Check("a topmost window opened afterwards is pushed back under it", Above(wins[0], late.Handle),
                "the privacy window is " + (Above(wins[0], late.Handle) ? "above" : "BELOW") + " it");
        }

        ps.Hide();
        Pump(300);
        Check("Hide leaves nothing behind", wins.All(h => !IsWindow(h)) && !ps.On);
        Check("and the screen is the screen again - capture sees magenta without it", Look(new ScreenCapture(), prim, region) == "magenta");

        magenta.Close();
        Console.WriteLine();
        Console.WriteLine($"{pass} of {pass + fail} checks passed.");
        return fail == 0 ? 0 : 1;
    }

    private static string R(Rectangle r) => $"{r.Width}x{r.Height}@{r.X},{r.Y}";

    /// <summary>What colour the capture shows inside the region: a 5x5 grid, all must agree.</summary>
    private static string Look(IScreenSource src, RemoteSupport.Shared.MonitorInfo prim, Rectangle region)
    {
        using (src)
        {
            src.Refresh();
            int idx = src.Monitors.FindIndex(m => m.Primary);
            List<(int x, int y, int w, int h, byte[] jpeg)> tiles = new();
            for (int i = 0; i < 4; i++) { tiles = src.CaptureTiles(idx, 90, true); if (tiles.Count > 0) break; Thread.Sleep(100); }
            if (tiles.Count == 0) return "no picture";
            var seen = new List<string>();
            for (int gx = 1; gx <= 5; gx++)
                for (int gy = 1; gy <= 5; gy++)
                {
                    int px = region.X - prim.X + region.Width * gx / 6, py = region.Y - prim.Y + region.Height * gy / 6;
                    var t = tiles.FirstOrDefault(t => px >= t.x && px < t.x + t.w && py >= t.y && py < t.y + t.h);
                    if (t.jpeg == null) { seen.Add("?"); continue; }
                    using var ms = new MemoryStream(t.jpeg);
                    using var bmp = new Bitmap(ms);
                    var c = bmp.GetPixel(px - t.x, py - t.y);
                    seen.Add(c.R > 190 && c.G < 70 && c.B > 190 ? "magenta" : c.R < 40 && c.G < 40 && c.B < 40 ? "black" : $"#{c.R:X2}{c.G:X2}{c.B:X2}");
                }
            return seen.Distinct().Count() == 1 ? seen[0] : "mixed: " + string.Join(",", seen.Distinct());
        }
    }

    private static string LookDxgi(RemoteSupport.Shared.MonitorInfo prim, Rectangle region)
    {
        try { return Look(new DxgiCapture(), prim, region); }
        catch (Exception ex) { return "Desktop Duplication failed: " + ex.Message; }
    }

    /// <summary>Is <paramref name="a"/> above <paramref name="b"/> in the z-order?</summary>
    private static bool Above(IntPtr a, IntPtr b)
    {
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
}
