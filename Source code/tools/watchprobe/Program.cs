using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

namespace RemoteSupport.Tools.WatchProbe;

// ---------------------------------------------------------------------------------------------
// watchprobe - the console's Processes and Performance window, drawn with REAL figures from this
// PC (2026-10-05), in light and dark, at this screen's scaling. Pictures beside the harness to look
// at; checks for what a picture can hide: every line as tall as its font (§30), the list sorted and
// kept in place across refreshes, the figures stopping SAID, not frozen.
//
//   dotnet run -c Release --project tools/watchprobe/watchprobe.csproj
// ---------------------------------------------------------------------------------------------
internal static class Program
{
    private static int _pass, _fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Console.WriteLine("watchprobe - the Processes and Performance window");

        // Real readings from this PC, a quarter of a second apart — enough for the graphs to draw a line.
        var watcher = new MachineWatcher(_ => { });
        watcher.Prime();
        var readings = new List<MachineSample>();
        for (int i = 0; i < 16; i++) { Thread.Sleep(250); readings.Add(watcher.Read(withProcesses: true)); }

        var brand = Branding.Load();
        var before = Theme.Override;
        try
        {
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                Theme.Override = mode;
                var name = mode == ThemeMode.Light ? "light" : "dark";
                Console.WriteLine();
                Console.WriteLine($"== {name} ==");
                var t = Theme.FromBranding(brand, AppSurface.Technician);
                var dev = new DeviceInfo { DeviceId = "probe", MachineName = Environment.MachineName, UserName = Environment.UserName, Online = true, CanWatch = true };
                using var f = new MachineWatchForm(new TechLink("ws://127.0.0.1:59999"), t, dev, MachineWatchForm.Page.Processes);
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(0, 0);
                f.ClientSize = new Size(860, 660);   // the size the window opens at
                f.TopMost = true;   // read off the screen: nothing else on the desktop may sit over it
                f.Show();
                Pump(10);
                // Load restores a remembered position; this probe draws at a known place.
                f.Location = new Point(0, 0); f.ClientSize = new Size(860, 660);
                Pump(5);

                var status = Field<Label>(f, "_status")!;
                Check("before any figures it says it is asking", status.Text.StartsWith("Asking"), $"\"{status.Text}\"");

                foreach (var r in readings) { f.Apply(r); Pump(1); }
                Pump(5);
                var list = Field<ListView>(f, "_list")!;
                var last = readings.Last().Processes!;
                Check("one row per process", list.Items.Count == last.Count, $"{list.Items.Count} rows, {last.Count} processes");
                var cpus = list.Items.Cast<ListViewItem>().Select(i => ((ProcessSample)i.Tag!).CpuPercent).ToList();
                Check("busiest first", cpus.Zip(cpus.Skip(1)).All(p => p.First >= p.Second), $"top {string.Join(", ", cpus.Take(4).Select(c => c.ToString("0.0")))}");
                Check("it says it is live, and look only", status.Text.StartsWith("Live") && status.Text.Contains("Look only"), $"\"{status.Text}\"");
                Check("the status line is as tall as its font (§30)", status.Height >= TextRenderer.MeasureText("Ag", status.Font).Height, $"{status.Height} px");
                foreach (var footer in new[] { "_processesFooter", "_performanceFooter" })
                {
                    var l = Field<Label>(f, footer)!;
                    Check($"{footer} holds one line of its font", l.Height - l.Padding.Vertical >= TextRenderer.MeasureText("Ag", l.Font).Height, $"{l.Height} px, padding {l.Padding.Vertical}");
                }
                foreach (var tab in new[] { "_tabProcesses", "_tabPerformance" })
                {
                    var b = Field<RoundButton>(f, tab)!;
                    Check($"{b.Text} fits its words", b.Width > TextRenderer.MeasureText(b.Text, b.Font).Width && b.Height >= TextRenderer.MeasureText("Ag", b.Font).Height, $"{b.Width}x{b.Height}");
                    // The rounded corner shows what is BEHIND the button: the window's own colour,
                    // not black (found by eye the first time this ran — nested see-through panels).
                    using var whole = Capture(f);
                    var at = b.PointToScreen(new Point(1, 1));
                    var c = whole.GetPixel(at.X - f.Bounds.X, at.Y - f.Bounds.Y);
                    // And the button's own middle, to prove the capture is of THIS window at all.
                    var mid = b.PointToScreen(new Point(b.Width / 2, 4));
                    var m = whole.GetPixel(mid.X - f.Bounds.X, mid.Y - f.Bounds.Y);
                    if (b.Style == BtnStyle.Accent) Check($"(the capture is of the window: the selected tab's top edge is its own colour)",
                        Math.Abs(m.R - t.Mist.R) + Math.Abs(m.G - t.Mist.G) + Math.Abs(m.B - t.Mist.B) >= 20, ColorTranslator.ToHtml(m));
                    Check($"{b.Text}'s corner shows the window's colour, not black",
                        Math.Abs(c.R - t.Mist.R) + Math.Abs(c.G - t.Mist.G) + Math.Abs(c.B - t.Mist.B) < 40, $"{ColorTranslator.ToHtml(c)} against {ColorTranslator.ToHtml(t.Mist)}");
                }

                // Every figure fits its column, heading and sort arrow included (§30): no "117…".
                using (var headFont = new Font("Segoe UI", 9f, FontStyle.Bold))
                    foreach (var col in new[] { 1, 2, 3, 4 })
                    {
                        int widest = list.Items.Cast<ListViewItem>().Max(i => TextRenderer.MeasureText(i.SubItems[col].Text, list.Font).Width);
                        widest = Math.Max(widest, TextRenderer.MeasureText(list.Columns[col].Text.TrimEnd('▲', '▼', ' ') + "  ▼", headFont).Width);
                        Check($"the {list.Columns[col].Text.TrimEnd('▲', '▼', ' ')} column holds its widest entry", list.Columns[col].Width - 16 >= widest, $"{list.Columns[col].Width} px for {widest} px");
                    }
                Check("Name takes the room that is left", list.Columns.Cast<ColumnHeader>().Sum(c => c.Width) >= list.ClientSize.Width - 30,
                    $"{list.Columns.Cast<ColumnHeader>().Sum(c => c.Width)} of {list.ClientSize.Width} px");

                // Kept in place: the row selected stays selected through a refresh.
                var pick = list.Items.Cast<ListViewItem>().First(i => ((ProcessSample)i.Tag!).Pid == Environment.ProcessId);
                pick.Selected = true;
                f.Apply(watcher.Read(true)); Pump(3);
                Check("a refresh keeps the selected row selected (rows are updated, not rebuilt)",
                    list.SelectedItems.Count == 1 && ((ProcessSample)list.SelectedItems[0].Tag!).Pid == Environment.ProcessId);

                // Sorting by a heading.
                var colClick = typeof(ListView).GetMethod("OnColumnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
                colClick.Invoke(list, new object[] { new ColumnClickEventArgs(0) });
                Pump(2);
                var names = list.Items.Cast<ListViewItem>().Select(i => ((ProcessSample)i.Tag!).Name).ToList();
                Check("clicking Name sorts A to Z", names.Zip(names.Skip(1)).All(p => string.Compare(p.First, p.Second, StringComparison.OrdinalIgnoreCase) <= 0), names.First());
                Check("...and the heading shows it", list.Columns[0].Text.Contains("▲"), $"\"{list.Columns[0].Text}\"");
                colClick.Invoke(list, new object[] { new ColumnClickEventArgs(3) });
                Pump(2);
                Shot(f, $"watch-processes-{name}");

                f.ShowPage(MachineWatchForm.Page.Performance);
                Pump(5);
                foreach (var g in new[] { "_cpu", "_memory", "_disk", "_network" })
                {
                    var graph = Field<Control>(f, g)!;
                    var value = graph.GetType().GetProperty("Value")!.GetValue(graph) as string;
                    Check($"the {g.TrimStart('_')} card has a figure and room to draw", graph.Width > 200 && graph.Height > 120 && value is { Length: > 0 } && (value != "—" || g == "_disk"),
                        $"{graph.Width}x{graph.Height}, \"{value}\"");
                }
                // The networks, under the Network graph (Clint, 2026-10-05): Private or Public, and the address.
                var netCard = Field<Control>(f, "_network")!;
                var notes = (IReadOnlyList<(string Text, Color Colour)>)netCard.GetType().GetProperty("Notes")!.GetValue(netCard)!;
                var real = readings.Last().Networks ?? new List<NetworkProfile>();
                // "Not connected to any network" on a connected PC passed this once, when Windows refused
                // the call and the failure was swallowed: the reading itself must have worked.
                Check("the networks were read (Windows answered on this thread too)", MachineWatcher.NetworkError == null && real.Count > 0,
                    MachineWatcher.NetworkError ?? $"{real.Count} network(s)");
                Check("the Network card has a line per connected network", real.Count > 0 && notes.Count == real.Count, string.Join(" | ", notes.Select(n => n.Text)));
                if (real.Count > 0)
                    Check("...saying Private or Public, and the IP address", notes[0].Text.Contains(real[0].Category) && real[0].Address.Length > 0 && notes[0].Text.Contains(real[0].Address), notes[0].Text);
                Shot(f, $"watch-performance-{name}");

                // A Public network is amber and says why it matters; two networks are two lines.
                var withPublic = watcher.Read(false);
                withPublic.Networks = new List<NetworkProfile>
                {
                    new() { Name = "Coffee Shop WiFi", Category = "Public", Adapter = "Wi-Fi", Address = "10.20.30.40" },
                    new() { Name = "Network 2", Category = "Private", Adapter = "Ethernet", Address = "192.168.1.45" },
                };
                f.Apply(withPublic); Pump(3);
                notes = (IReadOnlyList<(string Text, Color Colour)>)netCard.GetType().GetProperty("Notes")!.GetValue(netCard)!;
                Check("a Public network is amber, and says sharing is off",
                    notes.Count == 2 && notes[0].Colour == t.WarnText && notes[0].Text.StartsWith("Public network, sharing off") && notes[0].Text.Contains("10.20.30.40"),
                    notes.Count > 0 ? notes[0].Text : "no lines");
                // The address must be READABLE at the size the window opens, not lost to the "…".
                int roomForText = netCard.Width - 24;
                foreach (var n in notes)
                {
                    var upToAddress = n.Text[..(n.Text.IndexOf("  ·  ", n.Text.IndexOf("  ·  ") + 1) is > 0 and var cut ? cut : n.Text.Length)];
                    Check($"the address fits on the card at the window's opening size: \"{upToAddress}\"",
                        TextRenderer.MeasureText(upToAddress, t.Small).Width <= roomForText, $"{TextRenderer.MeasureText(upToAddress, t.Small).Width} of {roomForText} px");
                }
                Check("a Private one is in ordinary ink", notes.Count == 2 && notes[1].Colour == t.Ink && notes[1].Text.Contains("Private network"), notes.Count > 1 ? notes[1].Text : "");
                var g2 = Field<Control>(f, "_network")!;
                Check("the graph still has room under two network lines", g2.Height - (TextRenderer.MeasureText("Ag", t.Small).Height + 2) * 4 - TextRenderer.MeasureText("Ag", t.H2).Height > 60, $"{g2.Height} px card");
                Shot(f, $"watch-public-network-{name}");

                // The machine goes: said in the window, never a frozen graph that looks live.
                f.EndWith($"{Environment.MachineName} went offline.");
                Pump(2);
                Check("when the figures stop, it says why", status.Text.StartsWith($"{Environment.MachineName} went offline.") && status.ForeColor == t.WarnText, $"\"{status.Text}\"");
                var stale = f.Last;
                f.Apply(watcher.Read(true));
                Check("...and nothing arriving late changes it", ReferenceEquals(f.Last, stale));
                Shot(f, $"watch-ended-{name}");
                f.TopMost = false;
                f.Close();
            }
        }
        finally { Theme.Override = before; }

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    private static void Pump(int n) { for (int i = 0; i < n; i++) { Application.DoEvents(); Thread.Sleep(30); } }

    private static T? Field<T>(object o, string name) where T : class =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o) as T;

    private static Bitmap Capture(Form f)
    {
        f.Activate(); Pump(8);
        var r = f.Bounds;
        var bmp = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        return bmp;
    }

    private static void Shot(Form f, string name)
    {
        try
        {
            f.Activate(); Pump(8);
            var r = f.Bounds;
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            var dir = Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name + ".png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"        picture: {path}");
        }
        catch (Exception ex) { Console.WriteLine($"        (no picture: {ex.Message})"); }
    }
}
