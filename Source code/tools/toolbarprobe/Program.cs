using System.Drawing.Imaging;
using System.Reflection;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// Does anything on the session toolbar overlap anything else?
//
// It opens the REAL SessionForm — same class, same constructor DashboardForm uses — at several
// widths, measures every control on the toolbar and the status bar, and writes a screenshot of
// each. No server, no session, no customer machine: the link fails to connect and the window is
// built and laid out regardless, which is all this asks about.
//
// It exists because of a specific, cheap mistake that nearly shipped. Adding the Listen button
// pushed the connection pill half under it at the default 1320 px window, and nothing detected
// that except a screenshot somebody looked at (§18 of the handover). Adding the System menu is
// the same kind of change, so it gets the same kind of check — but automatic, and at several
// widths rather than the one the developer's window happened to be.
//
// Overlap is computed rather than eyeballed. A screenshot proves what it looked like; the
// numbers are what fails a build.
//
//   dotnet run -c Release --project tools/toolbarprobe/toolbarprobe.csproj -- <out-dir>
// ---------------------------------------------------------------------------------------------

internal static class ToolbarProbe
{
    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "toolbar");
        Directory.CreateDirectory(outDir);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var brand = Branding.Load();
        // AppSurface.Technician: this probe measures the technician console's own window, and
        // the surface decides the palette the controls are sized and drawn against.
        var theme = Theme.FromBranding(brand, AppSurface.Technician);
        var session = new SessionInfo { SessionId = "probe", DeviceId = "probe", MachineName = "PROBE-PC", StartedBy = "Probe", Unattended = true };

        int pass = 0, fail = 0;
        void Check(string what, bool ok, string detail = "")
        {
            if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
            else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        }

        // 1280 is the window's MinimumSize � the narrowest it can be made, and therefore the
        // worst case that can actually happen. 1320 is its default and the width the Listen
        // button broke. 1600 is a normal desktop. A layout that only works at one width is the
        // thing being looked for.
        foreach (var width in new[] { 1280, 1320, 1600 })
        {
            Console.WriteLine();
            Console.WriteLine($"== {width} px wide ==");

            // A localhost address nothing is listening on: the link's retry loop runs in the
            // background and fails, which is fine and is not what this measures.
            using var f = new SessionForm("ws://127.0.0.1:59999", "probe-token", session, theme, brand);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(0, 0);
            f.Size = new Size(width, 850);
            f.Show();
            for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(40); }

            // The knowledge base (3.8.5): the server's answer for this machine's client, as the
            // dashboard hands it over — so the status bar is measured WITH its notice on it.
            f.ShowKnowledge(new KnowledgeCountMsg { DeviceId = "probe", ClientId = 5, ClientName = "Zenith Accounting", Count = 3 });
            for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
            {
                var menu = Field<ContextMenuStrip>(f, "_toolsMenu");
                var pill = Field<Control>(f, "_kbPill");
                var status = Field<Panel>(f, "_status");
                Check($"Tools names the client's knowledge base ({width} px)",
                    menu?.Items.Cast<ToolStripItem>().Any(i => i.Text == "Knowledge base for Zenith Accounting (3)") == true);
                Check($"the status bar says so the moment the session opens ({width} px)",
                    pill is { Visible: true } && pill.Text.Contains("Knowledge base for Zenith Accounting (3)")
                    && pill.Width >= TextRenderer.MeasureText(pill.Text, pill.Font).Width, $"\"{pill?.Text}\", {pill?.Width} px");
                // Nothing docked beside it may sit on it; the participants line fills what is left.
                var others = status == null || pill == null ? new List<Control>()
                    : status.Controls.Cast<Control>().Where(c => c != pill && c.Visible && c.Dock != DockStyle.Fill && c.Width > 0 && c.Bounds.IntersectsWith(pill.Bounds)).ToList();
                Check($"...and nothing else on the status bar sits on it ({width} px)", status != null && others.Count == 0,
                    string.Join(", ", others.Select(c => $"{c.GetType().Name} {c.Bounds}")));
            }

            var bars = new[] { "_toolbar", "_status" };
            foreach (var barName in bars)
            {
                var bar = Field<Panel>(f, barName);
                if (bar == null) { Check($"found {barName}", false); continue; }
                // The controls actually on the bar, one level down through the card that holds them.
                var kids = Leaves(bar).Where(c => c.Visible && c.Width > 6 && c.Height > 6 && c is not Panel).ToList();
                // Only CLICKABLE controls covering each other count as a failure.
                //
                // A caption sitting a pixel inside its combo box, or a TrackBar's generous empty
                // margin brushing the checkbox beside it, is how this toolbar has always looked
                // and neither costs anybody a click. Failing on those would make this probe cry
                // wolf, and a diagnostic that reports faults the code does not have is worse than
                // no diagnostic — it sends the next person to the wrong file (§18). They are
                // printed as notes so a real change in them is still visible.
                bool clash = false;
                foreach (var (a, b) in Pairs(kids))
                {
                    var ra = Screenward(a, bar); var rb = Screenward(b, bar);
                    if (!ra.IntersectsWith(rb)) continue;
                    var hit = Rectangle.Intersect(ra, rb);
                    if (Clickable(a) && Clickable(b))
                    {
                        clash = true;
                        Check($"{barName}: {Name(a)} is not covered by {Name(b)}", false,
                              $"{hit.Width}x{hit.Height} px of overlap at {hit.X},{hit.Y} — one of them eats the other's clicks");
                    }
                    else Console.WriteLine($"        note: {Name(a)} and {Name(b)} touch by {hit.Width}x{hit.Height} px (a caption or a slider margin, not a clickable clash)");
                }
                if (!clash) Check($"{barName}: no clickable control is covered at {width} px", true, $"{kids.Count} controls");
                foreach (var c in kids.OrderBy(c => Screenward(c, bar).X))
                    Console.WriteLine($"        {Name(c),-22} {Screenward(c, bar)}");

                // A control that has been pushed off the end is invisible in exactly the way a
                // covered one is, and is just as easy to ship.
                foreach (var c in kids)
                {
                    var r = Screenward(c, bar);
                    if (r.Right > bar.Width + 1 || r.X < -1)
                        Check($"{barName}: {Name(c)} is inside the bar", false, $"{r} against a bar {bar.Width} px wide");
                }
            }

            var png = Path.Combine(outDir, $"session-{width}.png");
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(png, ImageFormat.Png);
            }
            Console.WriteLine($"        screenshot: {png}");
            f.Hide();
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
        }

        // ---- the toolbar must survive pop-out and full screen --------------------------------
        // Reported on 2026-09-15: both modes lost the toolbar, so there was no monitor selector,
        // no Control tick box and no System menu until the screen was popped back in. The tabs
        // had already been fixed for exactly this once; the toolbar had not.
        Console.WriteLine();
        Console.WriteLine("== the toolbar in every viewing mode ==");
        using (var f = new SessionForm("ws://127.0.0.1:59999", "probe-token", session, theme, brand))
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(0, 0);
            f.Size = new Size(1360, 850);
            f.Show();
            Settle(f);

            // Docked is the control: if the toolbar is not reachable here, nothing below means
            // anything.
            Check("docked: the System menu is on screen", Reachable(f, "System"));

            // ---- stage 3.5.4: the side panel GOES, and Tools is on the top toolbar -----------
            // Clint's first complaint about this window, 2026-09-18: "there are too many tabs
            // making pop out and full screen ineffective". The first attempt made the five tab
            // buttons into one menu and left the panel where it was, which is why the correction
            // had to be spelled out: "the whole idea is to get rid of the side bar on the right.
            // listen should be added to the tools bar so that all tabs sit at the top instead of
            // anything being on the side."
            //
            // So the checks below are about the PANEL, not the menu. A menu that looks tidy on a
            // panel that is still 400 px wide is the half step that was already built once.

            // There must be no side panel left to find. Asked of the field rather than of the
            // picture, because a panel collapsed to a strip would photograph as "gone" and still
            // be taking the width — and a collapsed panel is exactly what the thing it replaced
            // could already do.
            Check("the side panel is gone from the code, not merely narrow",
                  f.GetType().GetField("_side", BindingFlags.Instance | BindingFlags.NonPublic) == null,
                  "_side no longer exists");

            // And the picture has the width. This is the whole of what the change buys, so it is
            // measured rather than asserted: the screen host must span the canvas but for its own
            // margins, with no 400 px column beside it.
            var canvas = Field<Panel>(f, "_canvas");
            var screenHost = Field<Panel>(f, "_screenHost");
            Check("the remote desktop has the full width of the window",
                  canvas != null && screenHost != null && screenHost.Width >= canvas.Width - 4,
                  screenHost == null || canvas == null ? "not found"
                      : $"{screenHost.Width} px of a {canvas.Width} px canvas — the panel used to take 400");

            // Tools is on the TOP toolbar, beside System, and not anywhere else.
            var toolsBtn = Field<Control>(f, "_toolsBtn");
            var toolbar = Field<Panel>(f, "_toolbar");
            Check("the Tools menu is on the top toolbar",
                  toolsBtn != null && toolbar != null && IsUnder(toolsBtn, toolbar),
                  toolsBtn == null ? "no Tools button" : "beside System, where System is");

            var menu = Field<ContextMenuStrip>(f, "_toolsMenu");
            var names = menu == null ? new List<string>()
                                     : menu.Items.Cast<ToolStripItem>().Select(i => i.Text ?? "").ToList();
            // Nothing may be lost by the change. A menu that quietly dropped Sound would look
            // tidier and be worse.
            foreach (var want in new[] { "Chat", "Files", "Scripts", "Sound", "Info" })
                Check($"Tools still offers {want}", names.Contains(want),
                      names.Count == 0 ? "the menu is empty" : string.Join(", ", names));
            // Listen moved off the toolbar and onto this menu, which is the half of the
            // correction that is easy to forget: it is the one control that was in two places.
            Check("Listen moved onto the Tools menu", names.Contains("Listen"),
                  string.Join(", ", names));
            Check("and off the toolbar", !Reachable(f, "Listen"),
                  "one place for it, not two");

            // ---- and every tool opens as its own movable window ------------------------------
            // A panel that became five windows nobody can move is the same panel. These are the
            // properties that make a window worth having: it is a separate top-level window, it
            // can be dragged and resized, and it is not there until it is asked for.
            Check("nothing is open before anything is asked for",
                  !Application.OpenForms.Cast<Form>().Any(w => w.Text.StartsWith("Scripts", StringComparison.Ordinal)),
                  "a session starts as the picture and nothing else");

            Call(f, "OpenTool", 2);
            Settle(f);
            var scripts = Application.OpenForms.Cast<Form>().FirstOrDefault(w => w.Text.StartsWith("Scripts", StringComparison.Ordinal));
            Check("choosing a tool opens it in a window of its own", scripts != null,
                  scripts == null ? "no window titled Scripts" : scripts.Text);
            if (scripts != null)
            {
                Check("that window is movable and resizable", scripts.FormBorderStyle == FormBorderStyle.Sizable,
                      scripts.FormBorderStyle.ToString() + " — a fixed window on a second monitor is a panel with extra steps");
                Check("it is a separate top-level window, not a child of the session window",
                      scripts.Parent == null && scripts.Owner == f, "owned by the session, so it goes when the session does");
                Check("and it keeps out of the taskbar", !scripts.ShowInTaskbar,
                      "five per session, times three sessions, is a taskbar nobody can read");
                Shoot(scripts, outDir, "tool-scripts");

                // Closing a tool must put it away, not throw away what is in it. The script
                // output and the chat log are the session's history.
                var page = Field<Control>(f, "_scriptOut");
                scripts.Close();
                Settle(f);
                Check("closing a tool hides it rather than destroying it",
                      !scripts.Visible && !scripts.IsDisposed && page is { IsDisposed: false },
                      "what is in it survives being closed");
                Call(f, "OpenTool", 2);
                Settle(f);
                Check("and it opens again", scripts.Visible);
                scripts.Close();
                Settle(f);
            }

            Call(f, "TogglePopOut");
            Settle(f);
            var popped = Field<Form>(f, "_popped");
            Check("popped out: the pop-out window exists", popped != null);
            if (popped != null)
            {
                Check("popped out: the toolbar moved into the pop-out window", Reachable(popped, "System"),
                      Reachable(popped, "System") ? "System, Monitor and Control are with the screen" : "the toolbar stayed behind on the session window");
                Check("popped out: the Tools menu came too", Reachable(popped, "Tools"));
                Check("popped out: the window is wide enough to lay the toolbar out", popped.ClientSize.Width >= 1280,
                      popped.ClientSize.Width + " px");
                var po = NoOverlap(Field<Panel>(f, "_toolbar")!);
                Check("popped out: no toolbar button is covered by another", po.ok, po.detail);
                // The reason the panel does not have to travel any more. A tool window is the
                // pop-out's SIBLING — both are owned by the session window — so neither is
                // trapped underneath the other, which is what forced the panel to be carried
                // into the pop-out in the first place.
                Call(f, "OpenTool", 0);
                Settle(f);
                var chat = Application.OpenForms.Cast<Form>().FirstOrDefault(w => w.Text.StartsWith("Chat", StringComparison.Ordinal));
                Check("popped out: a tool can still be opened, and is not trapped behind the screen",
                      chat is { Visible: true } && chat.Owner == f && chat.Owner != popped,
                      chat == null ? "no Chat window" : "owned by the session window, a sibling of the pop-out");
                chat?.Close();
                Shoot(f, outDir, "mode-popped-session");
                Shoot(popped, outDir, "mode-popped-screen");
            }
            Call(f, "TogglePopOut");     // pop back in
            Settle(f);
            Check("popped back in: the toolbar returned to the session window", Reachable(f, "System"));
            Check("popped back in: and so did the Tools menu", Reachable(f, "Tools"));

            Call(f, "ToggleFullScreen");
            Settle(f);
            Check("full screen: the toolbar is still on screen", Reachable(f, "System"),
                  Reachable(f, "System") ? "monitor, Control and the System menu all reachable" : "the toolbar was hidden");
            var fo = NoOverlap(Field<Panel>(f, "_toolbar")!);
            Check("full screen: no toolbar button is covered by another", fo.ok, fo.detail);
            Check("full screen: the connection pill is still on screen", Reachable(f, "connecting") || Reachable(f, "reconnecting") || Reachable(f, "disconnected"),
                  "the status bar carries it, and it is what tells a frozen picture from a dropped socket");
            // The thing a docked panel could never do. Full screen used to mean choosing between
            // the whole picture and the tools; an owned window floats above a full-screen owner,
            // so it is both.
            Call(f, "OpenTool", 0);
            Settle(f);
            var fsChat = Application.OpenForms.Cast<Form>().FirstOrDefault(w => w.Text.StartsWith("Chat", StringComparison.Ordinal));
            Check("full screen: a tool still opens, over the picture rather than instead of it",
                  fsChat is { Visible: true }, fsChat == null ? "no Chat window" : fsChat.Bounds.ToString());
            Shoot(f, outDir, "mode-fullscreen");
            fsChat?.Close();
            Settle(f);
            Call(f, "ToggleFullScreen");
            Settle(f);
            Check("back from full screen: the toolbar is still there", Reachable(f, "System"));
            f.Hide();
        }

        // ---- the knowledge base for a machine that belongs to nobody, or a client with none ----
        Console.WriteLine();
        Console.WriteLine("== the knowledge base, when there is nothing to say ==");
        using (var f = new SessionForm("ws://127.0.0.1:59999", "probe-token", session, theme, brand))
        {
            f.Show();
            for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var menu = Field<ContextMenuStrip>(f, "_toolsMenu");
            var pill = Field<Control>(f, "_kbPill");
            Check("before the server answers, Tools still offers the knowledge base",
                menu?.Items.Cast<ToolStripItem>().Any(i => i.Text == "Knowledge base") == true && pill is { Visible: false });
            f.ShowKnowledge(new KnowledgeCountMsg { DeviceId = "probe", ClientId = 5, ClientName = "Zenith Accounting", Count = 0 });
            Check("a client with no articles: named on Tools, nothing on the status bar",
                menu?.Items.Cast<ToolStripItem>().Any(i => i.Text == "Knowledge base for Zenith Accounting (0)") == true && pill is { Visible: false });
            f.ShowKnowledge(new KnowledgeCountMsg { DeviceId = "probe" });
            Check("a machine that belongs to nobody: just Knowledge base, nothing on the status bar",
                menu?.Items.Cast<ToolStripItem>().Any(i => i.Text == "Knowledge base") == true && pill is { Visible: false });
            var opened = new List<string>();
            f.OpenPortal = p => opened.Add(p);
            f.ShowKnowledge(new KnowledgeCountMsg { DeviceId = "probe", ClientId = 5, ClientName = "Zenith Accounting", Count = 3 });
            menu?.Items.Cast<ToolStripItem>().First(i => i.Text.StartsWith("Knowledge base")).PerformClick();
            Check("choosing it opens that client's articles in the browser", opened.SequenceEqual(new[] { "/portal/kb?client=5" }), string.Join(", ", opened));
            f.Hide();
        }

        // ---- the free version (2026-10-06, Stage F): Files and printing, and no privacy screen ----
        Console.WriteLine();
        Console.WriteLine("== the free version's session window ==");
        using (var f = new SessionForm("ws://127.0.0.1:59999", "probe-token", session, theme, brand, free: true))
        {
            f.Show();
            for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var tools = Field<ContextMenuStrip>(f, "_toolsMenu")!.Items.Cast<ToolStripItem>().Where(i => i.Available && i is not ToolStripSeparator).Select(i => i.Text).ToList();
            Check("Tools offers Files and the two printing items, and nothing else",
                tools.Count == 3 && tools.Contains("Files") && tools.Contains("Print on my printer") && tools.Contains("Print a file on their printer…"),
                string.Join(" | ", tools));
            var sys = Field<ContextMenuStrip>(f, "_sysMenu")!.Items.Cast<ToolStripItem>().Where(i => i.Available && i is not ToolStripSeparator).Select(i => i.Text).ToList();
            Check("System keeps Ctrl+Alt+Del, Task Manager, lock and restart — and loses the privacy screen",
                sys.Contains("Ctrl+Alt+Del") && sys.Contains("Restart the machine…") && !sys.Any(s => s.StartsWith("Privacy")), string.Join(" | ", sys));
            f.Hide();
        }
        using (var f = new SessionForm("ws://127.0.0.1:59999", "probe-token", session, theme, brand))
        {
            f.Show();
            for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var tools = Field<ContextMenuStrip>(f, "_toolsMenu")!.Items.Cast<ToolStripItem>().Count(i => i.Available && i is not ToolStripSeparator);
            Check("a full company's Tools is untouched", tools > 3, $"{tools} items");
            f.Hide();
        }

        // ---- what the three connection states actually look like ----------------------------
        // Reconnect is a feature whose whole job is to be visible: silent recovery looks exactly
        // like nothing happening, which is how drag and drop worked for a fortnight and was
        // reported as broken the first time anyone used it. These are the pictures to look at
        // before believing the banner says anything useful.
        Console.WriteLine();
        Console.WriteLine("== the connection states ==");
        using (var f = new SessionForm("ws://127.0.0.1:59999", "probe-token", session, theme, brand))
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(0, 0);
            f.Size = new Size(1320, 850);
            f.Show();
            for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(40); }

            foreach (var (name, act) in new (string, Action)[]
            {
                ("reconnecting", () => { Call(f, "SetPill", "○ reconnecting", false); Stale(f, true); Call(f, "ShowBanner", "Connection to PROBE-PC lost — getting back in. The session is held open for a moment.", false, false); }),
                ("disconnected", () => Call(f, "MarkDead", "The technician's connection was lost and did not come back.")),
                ("restarting",   () => { Call(f, "SetPill", "○ restarting", false); Call(f, "ShowBanner", "PROBE-PC is restarting — waiting for it to come back (4:52 left).", false, true); }),
            })
            {
                act();
                for (int i = 0; i < 12; i++) { Application.DoEvents(); Thread.Sleep(40); }
                var p = Path.Combine(outDir, $"state-{name}.png");
                using var bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(p, ImageFormat.Png);
                Console.WriteLine($"        {name,-14} {p}");
            }
            f.Hide();
        }

        Console.WriteLine();
        Console.WriteLine($"{pass} of {pass + fail} checks passed.");
        Console.WriteLine($"screenshots in {outDir}");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>Something a technician presses or drags, and therefore something that being
    /// covered actually costs them. A Label is a caption; covering one is untidy, not a fault.</summary>
    private static bool Clickable(Control c) => c is ButtonBase or ComboBox or TrackBar && c is not Label;

    private static string Name(Control c)
    {
        var text = c switch
        {
            CheckBox cb => cb.Text,
            Label l => l.Text,
            ButtonBase b => b.Text,
            ComboBox => "(monitor list)",
            TrackBar => "(quality)",
            _ => c.GetType().Name
        };
        if (string.IsNullOrWhiteSpace(text)) text = c.GetType().Name;
        return text.Length > 20 ? text[..20] : text;
    }

    /// <summary>Bounds of a control relative to the bar it lives on, whatever it is nested in.</summary>
    private static Rectangle Screenward(Control c, Control bar)
    {
        var p = new Point(0, 0);
        for (var x = c; x != null && x != bar; x = x.Parent) { p.X += x.Left; p.Y += x.Top; }
        return new Rectangle(p, c.Size);
    }

    private static IEnumerable<Control> Leaves(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c.Controls.Count == 0) yield return c;
            else { yield return c; foreach (var g in Leaves(c)) yield return g; }
        }
    }

    private static IEnumerable<(Control, Control)> Pairs(List<Control> xs)
    {
        for (int i = 0; i < xs.Count; i++)
            for (int j = i + 1; j < xs.Count; j++)
                yield return (xs[i], xs[j]);
    }

    private static void Settle(Form f) { for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(40); } }

    /// <summary>Is this control somewhere inside that container, however deeply nested? The
    /// question "is Tools on the top toolbar" is exactly this and nothing else.</summary>
    private static bool IsUnder(Control c, Control container)
    {
        for (var p = c.Parent; p != null; p = p.Parent) if (p == container) return true;
        return false;
    }

    /// <summary>
    /// Is any clickable control on this bar covered by another? Separate from the width sweep so
    /// it can be asked again in each viewing mode — which is the gap that let a popped-out toolbar
    /// ship with "Pop in" underneath "Leave / End". Everything was present and reachable, so every
    /// assertion passed; only the screenshot showed them stacked.
    /// </summary>
    private static (bool ok, string detail) NoOverlap(Panel bar)
    {
        var kids = Leaves(bar).Where(c => c.Visible && c.Width > 6 && c.Height > 6 && c is not Panel).ToList();
        foreach (var (a, b) in Pairs(kids))
        {
            if (!Clickable(a) || !Clickable(b)) continue;
            var ra = Screenward(a, bar); var rb = Screenward(b, bar);
            if (!ra.IntersectsWith(rb)) continue;
            var hit = Rectangle.Intersect(ra, rb);
            return (false, $"{Name(a)} and {Name(b)} overlap by {hit.Width}x{hit.Height} px");
        }
        // A button pushed off the end is just as invisible as one underneath another.
        foreach (var c in kids.Where(Clickable))
        {
            var r = Screenward(c, bar);
            if (r.Right > bar.Width + 1) return (false, $"{Name(c)} runs off the end of a {bar.Width} px bar");
        }
        return (true, $"{kids.Count} controls, {bar.Width} px wide");
    }

    /// <summary>
    /// Is a control with this caption actually on screen in this window — visible, non-zero, and
    /// with every parent visible too? Walking the tree matters: a control whose PARENT is hidden
    /// reports Visible=true itself, so asking the control alone would have passed cheerfully while
    /// the whole toolbar was invisible, which is the bug being tested for.
    /// </summary>
    private static bool Reachable(Control root, string caption)
    {
        foreach (var c in Leaves(root))
        {
            if (!c.Text.Contains(caption, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Width < 4 || c.Height < 4) continue;
            bool shown = true;
            for (var p = c; p != null && p != root; p = p.Parent) if (!p.Visible) { shown = false; break; }
            if (shown) return true;
        }
        return false;
    }

    private static void Shoot(Form f, string outDir, string name)
    {
        try
        {
            using var bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height));
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            var p = Path.Combine(outDir, name + ".png");
            bmp.Save(p, ImageFormat.Png);
            Console.WriteLine($"        screenshot: {p}");
        }
        catch (Exception ex) { Console.WriteLine($"        (could not screenshot {name}: {ex.GetType().Name})"); }
    }

    /// <summary>Drive one of the window's own private state changes, so the picture taken is the
    /// real UI reacting rather than a mock-up of it.</summary>
    private static void Call(object o, string method, params object[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args);

    private static void Stale(object form, bool value)
    {
        var view = form.GetType().GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        view.GetType().GetProperty("Stale")!.SetValue(view, value);
    }

    private static T? Field<T>(object o, string name) where T : class =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o) as T;
}
