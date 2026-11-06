using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// lookprobe — does every window of the console look finished, in the light AND the dark theme?
//
// Written 2026-10-06 for Clint: "Some boxes are round edges, some have round and square edges and
// some have black lines sticking out… All edges must be rounded with no visible black lines."
//
// What a picture of a window cannot show is a pixel NOTHING paints. A rounded button's corners are
// outside its shape; if no code paints them they keep whatever was on the screen there before —
// black on a new window, a piece of the button that sat there before a redraw, the line of a border
// that has since moved. Exactly the "black lines sticking out". So for every window this:
//
//   1. floods the window and every control in it with magenta, straight onto the screen;
//   2. asks the whole window to repaint itself, the way Windows would;
//   3. reads the screen back and counts what is still magenta — and names the control it is in.
//
// Anything left magenta is a pixel the console never paints. Then, from the same picture, the
// list headers and the space under a list's last row, and from the window's own controls the
// things that cannot be rounded or themed at all: a square-bordered box, a Windows dropdown, date
// picker, spinner, tick box, slider or progress bar, a menu in Windows' own colours.
//
// The REAL windows, no server: every link points at a port nothing listens on. Pictures of each go
// to the output folder — they are to be looked at as well as counted.
//
//   dotnet run -c Release --project tools/lookprobe/lookprobe.csproj -- [outDir] [one window's name]
// ---------------------------------------------------------------------------------------------

internal static class LookProbe
{
    private static int _pass, _fail;
    private static string _out = "";
    private static string _only = "";
    private static ThemeMode _mode;

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        _out = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "look");
        _only = args.Length > 1 ? args[1] : "";
        Directory.CreateDirectory(_out);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        using (var g = Graphics.FromHwnd(IntPtr.Zero))
            Console.WriteLine($"lookprobe — this screen is at {g.DpiX:0} DPI ({g.DpiX / 96f:P0}). Pictures go to {_out}");

        var brand = Branding.Load();
        brand.CompanyName = "Anywhere IT";
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            _mode = mode;
            Theme.Override = mode;
            var t = Theme.FromBranding(brand, AppSurface.Technician);
            Console.WriteLine();
            Console.WriteLine($"==== {mode} ====");
            try { Windows(t, brand); }
            catch (Exception ex) { Check("the windows all opened", false, ex.ToString()); }
            // The customer's side is built on the same library, and Clint saw the same faults there.
            try { CustomerWindows(Theme.FromBranding(brand, AppSurface.Customer), brand); }
            catch (Exception ex) { Check("the customer's windows all opened", false, ex.ToString()); }
        }
        Theme.Override = null;

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    // ================================================================== the windows

    private static void Windows(Theme t, Branding brand)
    {
        Look("signin", () => new LoginForm(brand));

        Look("dashboard", () => new DashboardForm(brand, Link()) { Size = new Size(1500, 860) }, f =>
        {
            var map = (Dictionary<string, DeviceInfo>)Field(f, "_deviceMap")!;
            foreach (var d in new[]
            {
                Dev("ZEN-RECEPTION", "Zenith Accounting", 1, "Jane Smith", online: true),
                Dev("ZEN-ACCOUNTS", "Zenith Accounting", 1, "Bob Jones", online: true, unattended: true),
                Dev("ACME-01", "Acme Ltd", 2, "Sam Lee"),
                Dev("UNKNOWN-PC")
            }) map[d.DeviceId] = d;
            Call(f, "RefreshDevices");
        }, after: f =>
        {
            // Clint: "the tickets button in the console is blue, the rest are not."
            var header = All(f).OfType<BrandHeader>().FirstOrDefault();
            var styles = header == null ? new List<string>() :
                header.Controls.OfType<RoundButton>().Where(b => b.Visible).Select(b => $"{b.Text}={b.Style}").ToList();
            var distinct = header == null ? 0 : header.Controls.OfType<RoundButton>().Where(b => b.Visible).Select(b => Look_(b.Style)).Distinct().Count();
            Check($"dashboard ({_mode}): every button on the header looks the same", distinct == 1, string.Join(", ", styles));
        });

        Look("tickets", () => Tickets(t, brand), f =>
        {
            Call(f, "OnList", new TicketListMsg
            {
                Filter = "mine",
                Tickets = Enumerable.Range(1, 6).Select(i => new TicketSummary
                {
                    Id = i, Number = 100 + i, Subject = i == 1 ? "Laptop will not power on" : $"Printer offline on floor {i}",
                    Category = (int)TicketCategoryKind.Workshop, ClientName = "Zenith Accounting", Status = 1, CreatedUtc = DateTime.UtcNow.AddDays(-i)
                }).ToList()
            });
            f.ShowTicket(Repairable());
        });

        Look("tickets-quote", () => Tickets(t, brand), f =>
        {
            f.ShowTicket(Repairable()); Settle();
            Click(f, "Quote (1)");
        });

        Look("tickets-reschedule", () => Tickets(t, brand), f =>
        {
            f.ShowTicket(CallOut()); Settle();
            Click(f, "☞ Reschedule");
        });

        var session = new SessionInfo { SessionId = "probe", DeviceId = "probe", MachineName = "PROBE-PC", StartedBy = "Probe", Unattended = true };
        SessionForm? sf = null;
        Look("session", () => sf = new SessionForm("ws://127.0.0.1:59999", "probe-token", session, t, brand) { Size = new Size(1320, 850) }, f =>
        {
            ((SessionForm)f).ShowKnowledge(new KnowledgeCountMsg { DeviceId = "probe", ClientId = 5, ClientName = "Zenith Accounting", Count = 3 });
        }, after: f =>
        {
            Menu(f, "_toolsMenu", "_toolsBtn", "session-tools-menu");
            var bar = All(f).OfType<RoundButton>().Where(b => b.Visible && b.Text != "Leave / End" && b.Parent is Card).ToList();
            Check($"session ({_mode}): the toolbar's buttons all look the same, Leave / End apart",
                bar.Select(b => Look_(b.Style)).Distinct().Count() == 1, string.Join(", ", bar.Select(b => $"{b.Text.Trim()}={b.Style}")));

            // Each tool, in its own window, as the Tools menu opens it.
            var tools = (System.Collections.IList)Field(f, "_tools")!;
            for (int i = 0; i < tools.Count; i++)
            {
                Call(f, "OpenTool", i); Settle();
                var name = (string)tools[i]!.GetType().GetField("Name")!.GetValue(tools[i])!;
                var win = (Form)tools[i]!.GetType().GetField("Win")!.GetValue(tools[i])!;
                Inspect(win, "tool-" + name.ToLowerInvariant());
                win.Hide();
            }
        });

        Look("new-ticket", () => new NewTicketDialog(t, Link()));
        Look("two-factor-enrol", () => new TwoFactorDialog(t, Link(), new TechLink.LoginStep { Next = "enrol", SecretGrouped = "ABCD EFGH IJKL MNOP" }));
        Look("two-factor-code", () => new TwoFactorDialog(t, Link(), new TechLink.LoginStep { Next = "code" }));
        Look("recovery-codes", () => new RecoveryCodesDialog(t, Enumerable.Range(0, 10).Select(i => $"abcd-{1000 + i}").ToList()));
        Look("office-chat", () => new StaffChatForm(Link(), t));
        Look("website-chat", () => new WebChatForm(Link(), t));
        Look("latest-activity", () => new ActivityForm(Link(), t, false), f => Call(f, "Take", ActivitySample()));
        Look("watch-processes", () => new MachineWatchForm(Link(), t, Dev("ZEN-RECEPTION", "Zenith Accounting", 1, "Jane Smith", online: true), MachineWatchForm.Page.Processes));
        Look("watch-performance", () => new MachineWatchForm(Link(), t, Dev("ZEN-RECEPTION", "Zenith Accounting", 1, "Jane Smith", online: true), MachineWatchForm.Page.Performance));
        Look("link-device", () => new LinkDeviceDialog(t, Link(), Dev("ZEN-RECEPTION", "Zenith Accounting", 1, "Jane Smith", online: true)));
        Look("reason", () => new ReasonDialog(t, "ZEN-RECEPTION", "Jane Smith", askPassword: true));
        Look("remove-device", () => new RemoveDeviceDialog(t, "RECEPTION-OLD-LAPTOP", true));
        Look("code-entry", () => new CodeEntryDialog(t));
        Look("consent-wait", () => new ConsentWaitForm(t, session));
        Look("billing", () => new BillingPopup(t));
        Look("visitor", () => new VisitorPopup(t, new OutsiderVisitMsg { SessionId = "v", DeviceId = "probe", MachineName = "ZEN-RECEPTION", Visitor = "Jane Smith (Smith & Co IT)", ForWhom = "the accountant" }));
        Look("incoming-call", () => new IncomingCallPopup(t, brand));
        Look("guest", () => new GuestForm("https://example.invalid"));
    }

    /// <summary>The customer's app, and the Allow prompt the host puts on their screen.</summary>
    private static void CustomerWindows(Theme t, Branding brand)
    {
        // A port nothing listens on: the tray window tries to connect, and fails quietly.
        var local = Branding.Load();
        local.CompanyName = brand.CompanyName;
        local.ServerUrl = "http://127.0.0.1:59999";
        Look("client-tray-window", () => new RemoteSupport.Client.MainForm(local));
        Look("client-chat", () => new RemoteSupport.Client.ChatPopup(t, "Anywhere IT"));
        Look("client-installer", () => new RemoteSupport.Client.LauncherForm(local, "anywhere-it", true, false));
        Look("client-free-install", () => new RemoteSupport.Client.FreeInstallForm(local, "branch-offices", "https://example.invalid", false));
        Look("client-request", () => new RemoteSupport.Client.RequestDialog("Lenovo", t, local));
        Look("client-microphone", () => new RemoteSupport.Client.MicQuestion(t, "Anywhere IT Support"));
        Look("client-outside-help", () => new RemoteSupport.Client.OutsiderCodeDialog(t, local));
        Look("client-unattended-password", () => new RemoteSupport.Client.UnattendedPasswordDialog(t, local, hasPassword: false, unattendedOn: true));
        Look("client-banner", () => { var b = new RemoteSupport.Client.SessionBanner(t); b.ShowInProgress("Anywhere IT Support", "Clint"); return b; });
        var consent = typeof(RemoteSupport.Host.HostEngine).Assembly.GetType("RemoteSupport.Host.ConsentPrompt+ConsentForm", throwOnError: true)!;
        Look("client-allow", () => (Form)Activator.CreateInstance(consent, local, "Sue Tech", 60, "")!);
        Look("client-allow-outsider", () => (Form)Activator.CreateInstance(consent, local, "Jane Smith (Smith & Co IT)", 60, "Smith & Co IT")!);
    }

    private static TechLink Link()
    {
        var link = new TechLink("ws://127.0.0.1:59999");   // nothing listens: this looks, it does not talk
        typeof(TechLink).GetProperty("DisplayName")!.SetValue(link, "Sue Tech");
        typeof(TechLink).GetProperty("Roster")!.SetValue(link, new List<string> { "Ann Tech", "Bob Tech", "Sue Tech" });
        return link;
    }

    private static TicketsForm Tickets(Theme t, Branding brand) => new(Link(), t, brand) { Size = new Size(1500, 900) };

    // ================================================================== looking at one window

    private static void Look<T>(string name, Func<T> make, Action<T>? prepare = null, Action<T>? after = null) where T : Form
    {
        if (_only.Length > 0 && !name.StartsWith(_only, StringComparison.OrdinalIgnoreCase)) return;
        Console.WriteLine($"-- {name}");
        T f;
        try { f = make(); }
        catch (Exception ex) { Check($"{name} ({_mode}): opens", false, ex.GetBaseException().Message); return; }
        try
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(30, 30);
            f.Show();
            Settle();
            prepare?.Invoke(f);
            Settle();
            Inspect(f, name);
            after?.Invoke(f);
        }
        catch (Exception ex) { Check($"{name} ({_mode}): could be looked at", false, ex.GetBaseException().ToString()); }
        finally
        {
            try { f.Hide(); f.Dispose(); } catch { }
            Settle();
        }
    }

    private static void Inspect(Form f, string name)
    {
        var tag = $"{name} ({_mode})";
        if (!f.Visible) f.Show();
        // On top, and SEEN to be on top: this runs on a desktop somebody is using, and a window
        // that only believes it is in front reads back whatever is really there (the first runs
        // read the Claude app for half the windows and the session window for every tool).
        foreach (Form o in Application.OpenForms) if (o != f && o.TopMost) o.TopMost = false;
        for (int i = 0; i < 40 && !OnTop(f); i++)
        {
            f.TopMost = false; f.TopMost = true;
            SetWindowPos(f.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            f.Activate(); Settle();
        }

        Structure(f, tag);
        Menus(f, tag);
        using var shot = Unpainted(f, tag, name);
        if (shot != null) { Corners(f, tag, shot); Lists(f, tag, shot); }
        f.TopMost = false;
    }

    // ---- 1. what the window is made of ----------------------------------------------------------

    private static void Structure(Form f, string tag)
    {
        var square = new List<string>();
        var native = new List<string>();
        foreach (var c in All(f))
        {
            var border = c switch
            {
                TextBoxBase tb => tb.BorderStyle,
                ListBox lb => lb.BorderStyle,
                Panel p => p.BorderStyle,
                Label l => l.BorderStyle,
                PictureBox pb => pb.BorderStyle,
                ListView lv => lv.BorderStyle,
                UpDownBase ud => ud.BorderStyle,
                _ => BorderStyle.None
            };
            if (border != BorderStyle.None) square.Add(Name(c));

            var exact = c.GetType();
            bool raw = c is DateTimePicker or NumericUpDown or DomainUpDown or ProgressBar or TrackBar or MonthCalendar or GroupBox
                || (c is ComboBox && c is not ThemedCombo)
                || exact == typeof(CheckBox) || exact == typeof(RadioButton) || exact == typeof(Button);
            if (raw) native.Add(Name(c));
        }
        Check($"{tag}: no square-bordered box", square.Count == 0, string.Join("; ", square.Distinct().Take(6)));
        Check($"{tag}: nothing left in Windows' own look", native.Count == 0, string.Join("; ", native.Distinct().Take(6)));
    }

    private static void Menus(Form f, string tag)
    {
        var menus = new HashSet<ContextMenuStrip>();
        for (var type = f.GetType(); type != null && type != typeof(Form); type = type.BaseType)
            foreach (var fi in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (fi.GetValue(f) is ContextMenuStrip m) menus.Add(m);
        foreach (var c in All(f).Prepend(f)) if (c.ContextMenuStrip != null) menus.Add(c.ContextMenuStrip);
        if (menus.Count == 0) return;
        var plain = menus.Where(m => m.Renderer.GetType().Name != "ThemedMenuRenderer").Select(m => m.Items.Count > 0 ? m.Items[0].Text : "(empty)").ToList();
        Check($"{tag}: every menu ({menus.Count}) wears the palette", plain.Count == 0, plain.Count == 0 ? "" : "Windows' own: menus starting " + string.Join(", ", plain));
    }

    // ---- 2. the pixel nothing paints -------------------------------------------------------------

    private static readonly Color Flood = Color.FromArgb(255, 0, 255);

    private static Bitmap? Unpainted(Form f, string tag, string name)
    {
        try
        {
            // Flood the window and every control in it.
            foreach (var c in All(f).Prepend(f).Where(c => c.Visible && c.IsHandleCreated && c.Width > 0 && c.Height > 0))
            {
                using var g = Graphics.FromHwnd(c.Handle);
                using var b = new SolidBrush(Flood);
                g.FillRectangle(b, new Rectangle(0, 0, c.Width, c.Height));
            }
            // The flood has to be SEEN before its absence means anything: read straight back, the
            // window must be magenta. If it is not, this cannot see the window at all — the first
            // run of this probe read the Claude app that was on top and passed every window.
            using (var flooded = Client(f))
                if (Magenta(flooded, f, null) < flooded.Width * flooded.Height / 4)
                { Check($"{tag}: the flood can be read back (otherwise nothing below means anything)", false); return null; }

            // Then let it repaint itself, all of it, as Windows asks a window to.
            RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME);
            Settle();

            var bmp = Client(f);
            bmp.Save(Path.Combine(_out, $"{name}-{_mode}.png"), ImageFormat.Png);
            var owners = new Dictionary<string, int>();
            int left = Magenta(bmp, f, owners);
            Check($"{tag}: every pixel is painted", left == 0,
                left == 0 ? "" : $"{left} px never painted, in " + string.Join("; ", owners.OrderByDescending(o => o.Value).Take(5).Select(o => $"{o.Key} ×{o.Value}")));
            return bmp;
        }
        catch (Exception ex) { Check($"{tag}: could be read", false, ex.Message); return null; }
    }

    /// <summary>
    /// The window's client area as the window itself last drew it — read from its own surface, so
    /// another window on top of it (this runs on a desktop somebody is using) changes nothing.
    /// </summary>
    /// <summary>Is this window what is actually on the screen, at its middle and its four corners?</summary>
    private static bool OnTop(Form f)
    {
        var cr = f.RectangleToScreen(f.ClientRectangle);
        var points = new[] { new Point(cr.X + cr.Width / 2, cr.Y + cr.Height / 2), new Point(cr.X + 4, cr.Y + 4),
            new Point(cr.Right - 5, cr.Y + 4), new Point(cr.X + 4, cr.Bottom - 5), new Point(cr.Right - 5, cr.Bottom - 5) };
        return points.All(p => GetAncestor(WindowFromPoint(p), GA_ROOT) == f.Handle);
    }

    private static Bitmap Client(Form f)
    {
        var cr = f.RectangleToScreen(f.ClientRectangle);
        var mid = new Point(cr.X + cr.Width / 2, cr.Y + cr.Height / 2);
        var top = GetAncestor(WindowFromPoint(mid), GA_ROOT);
        if (top != f.Handle)
        {
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(top, sb, sb.Capacity);
            Console.WriteLine($"        (on top of it at {mid}: '{sb}', not this window at {cr})");
        }
        var bmp = new Bitmap(cr.Width, cr.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(cr.Location, Point.Empty, cr.Size);
        return bmp;
    }

    private static int Magenta(Bitmap bmp, Form f, Dictionary<string, int>? owners)
    {
        var origin = f.RectangleToScreen(f.ClientRectangle).Location;
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[bmp.Width * bmp.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        bmp.UnlockBits(data);
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = Color.FromArgb(px[y * bmp.Width + x]);
                // Magenta, or magenta an anti-aliased edge was blended onto. 200, not 170 (2026-10-09): the console's
                // Call out button writes in purple #7E22CE, and ClearType's colour fringes on its letters came out as
                // (171..193, 34..78, 206) — 28 "unpainted" pixels that were all inside the words, every one with the
                // purple's own blue. Magenta at 80% or more still counts.
                if (c.R < 200 || c.B < 200 || c.G > 90) continue;
                n++;
                if (owners == null) continue;
                var who = Name(Deepest(f, origin.X + x, origin.Y + y));
                owners[who] = owners.TryGetValue(who, out var k) ? k + 1 : 1;
            }
        return n;
    }

    // ---- 2b. corners -----------------------------------------------------------------------------
    //
    // The flood above cannot see the fault Clint reported, and the first run proved it: every
    // window passed. A control that paints with OptimizedDoubleBuffer draws into an off-screen
    // buffer first and copies the WHOLE of it to the screen — so a pixel it never paints is not
    // left as it was on the screen (magenta), it is whatever the buffer held: black when the buffer
    // is new, or a piece of the last control drawn in it when it is shared. The black corner of the
    // Sign In button and the blue corner of the Photos tab were both that.
    //
    // So this asks the question from where Clint stands instead: a rounded thing's corner is
    // OUTSIDE its shape, so on the screen it must be the colour of what is around it. A square
    // dropdown fails it (its corner is its border), a stale corner fails it, and so does a button
    // cut off by its container (its corner is fill).

    private static readonly HashSet<string> Rounded = new() { "RoundButton", "Pill", "InputBox", "Card", "ThemedCombo", "RoundCheck", "DateBox", "TimeBox", "NumberBox", "ThemedList", "RoundSlider", "RoundProgress" };

    private static void Corners(Form f, string tag, Bitmap shot)
    {
        var bad = new List<string>();
        int looked = 0;
        foreach (var c in All(f).Where(c => Rounded.Contains(c.GetType().Name) && c.Visible && c.Width >= 12 && c.Height >= 12 && Shown(c)))
        {
            var r = f.RectangleToClient(c.RectangleToScreen(new Rectangle(0, 0, c.Width, c.Height)));
            // Each corner pixel, and a point just outside it diagonally, which must be the ground.
            var corners = new[]
            {
                (at: new Point(r.Left, r.Top), outside: new Point(r.Left - 2, r.Top - 2)),
                (at: new Point(r.Right - 1, r.Top), outside: new Point(r.Right + 1, r.Top - 2)),
                (at: new Point(r.Left, r.Bottom - 1), outside: new Point(r.Left - 2, r.Bottom + 1)),
                (at: new Point(r.Right - 1, r.Bottom - 1), outside: new Point(r.Right + 1, r.Bottom + 1))
            };
            var wrong = new List<string>();
            foreach (var (at, outside) in corners)
            {
                if (!Inside(shot, at) || !Inside(shot, outside)) continue;
                // Only where what is just outside is this control's own container (or an ancestor
                // seen through it) — not a neighbour sitting tight against it.
                var screenOut = f.PointToScreen(outside);
                var under = Deepest(f, screenOut.X, screenOut.Y);
                if (under == c || !IsAncestor(under, c)) continue;
                // ...and only where the corner itself is not under something else.
                var screenAt = f.PointToScreen(at);
                if (Deepest(f, screenAt.X, screenAt.Y) != c && !IsAncestor(c, Deepest(f, screenAt.X, screenAt.Y))) continue;
                looked++;
                var a = shot.GetPixel(at.X, at.Y);
                var o = shot.GetPixel(outside.X, outside.Y);
                if (!Near(a, o, 22)) wrong.Add($"{(at.X == r.Left ? "left" : "right")}-{(at.Y == r.Top ? "top" : "bottom")} {Hex(a)} beside {Hex(o)}");
            }
            if (wrong.Count > 0) bad.Add($"{Name(c)} [{string.Join(", ", wrong)}]");
        }
        Check($"{tag}: every rounded control's corners show what is behind it ({looked} looked at)", bad.Count == 0,
            bad.Count == 0 ? "" : $"{bad.Count}: " + string.Join("; ", bad.Take(5)));
    }

    private static bool Shown(Control c) { for (var p = c; p != null; p = p.Parent) if (!p.Visible) return false; return true; }
    private static bool Inside(Bitmap b, Point p) => p.X >= 0 && p.Y >= 0 && p.X < b.Width && p.Y < b.Height;
    private static bool IsAncestor(Control maybe, Control of) { for (var p = of.Parent; p != null; p = p.Parent) if (p == maybe) return true; return false; }
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Control Deepest(Control parent, int sx, int sy)
    {
        foreach (Control c in parent.Controls)   // index 0 is the top of the z-order
        {
            if (!c.Visible || c.Width <= 0 || c.Height <= 0) continue;
            if (parent.RectangleToScreen(c.Bounds).Contains(sx, sy)) return Deepest(c, sx, sy);
        }
        return parent;
    }

    // ---- 3. lists: the header past the last column, and the space under the last row ----------

    private static void Lists(Form f, string tag, Bitmap shot)
    {
        var origin = f.RectangleToScreen(f.ClientRectangle).Location;
        foreach (var lv in All(f).OfType<ListView>().Where(l => l.Visible && l.Width > 40 && l.Height > 40 && l.View == View.Details))
        {
            var lr = lv.RectangleToScreen(lv.ClientRectangle);
            var header = SendMessage(lv.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            int headerH = 0;
            if (header != IntPtr.Zero && GetWindowRect(header, out var hr) && hr.Bottom > hr.Top)
            {
                headerH = hr.Bottom - hr.Top;
                int used = 0; foreach (ColumnHeader col in lv.Columns) used += col.Width;
                var gap = Rectangle.FromLTRB(hr.Left + used + 2, hr.Top + 2, hr.Right - 2, hr.Bottom - 2);
                // What the header's own cells are painted, read just inside the first one — not asked
                // of the window, which a tool window does not keep under a name this could find.
                var want = shot.GetPixel(Math.Clamp(hr.Left + 2 - origin.X, 0, shot.Width - 1), Math.Clamp(hr.Bottom - 3 - origin.Y, 0, shot.Height - 1));
                Color? first = null;
                int off = Count(shot, origin, gap, p => { bool bad = !Near(p, want, 10); if (bad) first ??= p; return bad; });
                Check($"{tag}: list '{Name(lv)}' — the header past the last column is the header's colour", off == 0,
                    off == 0 ? "" : $"{off} px of another colour in {gap.Width}x{gap.Height}, e.g. {Hex(first!.Value)} where {Hex(want)} was wanted");
            }

            int bottom = headerH;
            if (lv.Items.Count > 0) bottom = Math.Max(bottom, lv.GetItemRect(lv.Items.Count - 1).Bottom);
            var empty = Rectangle.FromLTRB(lr.Left + 2, lr.Top + bottom + 4, lr.Right - SystemInformation.VerticalScrollBarWidth - 2, lr.Bottom - 4);
            if (empty.Height > 8 && empty.Width > 8)
            {
                int lines = Count(shot, origin, empty, p => !Near(p, lv.BackColor, 8));
                Check($"{tag}: list '{Name(lv)}' — nothing drawn in the space under the last row", lines == 0, lines == 0 ? "" : $"{lines} px (column rules?)");
            }
        }
    }

    private static int Count(Bitmap shot, Point origin, Rectangle screen, Func<Color, bool> bad)
    {
        int n = 0;
        for (int y = screen.Top; y < screen.Bottom; y++)
            for (int x = screen.Left; x < screen.Right; x++)
            {
                int bx = x - origin.X, by = y - origin.Y;
                if (bx < 0 || by < 0 || bx >= shot.Width || by >= shot.Height) continue;
                if (bad(shot.GetPixel(bx, by))) n++;
            }
        return n;
    }

    private static bool Near(Color a, Color b, int tol) =>
        Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    // ---- a menu, opened ---------------------------------------------------------------------------

    private static void Menu(Form f, string menuField, string buttonField, string picture)
    {
        if (Field(f, menuField) is not ContextMenuStrip menu || Field(f, buttonField) is not Control btn) return;
        menu.Show(btn, new Point(0, btn.Height));
        Settle();
        try
        {
            var r = menu.Bounds;
            using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(menu.Handle, hdc, PW_RENDERFULLCONTENT); } finally { g.ReleaseHdc(hdc); }
            }
            bmp.Save(Path.Combine(_out, $"{picture}-{_mode}.png"), ImageFormat.Png);
        }
        finally { menu.Close(); Settle(); }
    }

    // ================================================================== fixtures

    /// <summary>Latest activity (HANDOVER §164): a page of every kind of line, one of them new and one for "you".</summary>
    private static ActivityListMsg ActivitySample()
    {
        var now = DateTime.UtcNow;
        ActivityEntry E(string kind, string head, int min, string text = "", bool isNew = false, string forYou = "", int files = 0) => new()
        {
            Key = kind + min, Kind = kind, Headline = head, AtUtc = now.AddMinutes(-min), TicketId = 7, TicketNumber = 120 + min,
            Subject = "Reception printer jams", Client = "Zenith Accounting", Who = "Sue Tech", Text = text, IsNew = isNew, ForYou = forYou, Files = files
        };
        return new ActivityListMsg
        {
            People = new() { "Sue Tech", "Ryan Tech" }, More = true,
            Entries = new()
            {
                E(ActivityKinds.Comment, "Comment", 2, "Replaced the roller. Testing it now.", isNew: true, files: 2),
                E(ActivityKinds.Change, "Updated", 9, "reassigned from Tim Tech to Probe", forYou: "assigned it to you"),
                E(ActivityKinds.Step, "Booked in → Inspection findings", 40, "Fuser worn; needs a new one. · Time spent: 30 min"),
                E(ActivityKinds.Customer, "Customer replied", 300, "Thanks, it works now."),
                E(ActivityKinds.Opened, "New ticket", 2000, "Workshop ticket, on it now: Sue Tech. It will not print."),
            }
        };
    }

    private static DeviceInfo Dev(string machine, string client = "", int clientId = 0, string contact = "", bool online = false, bool unattended = false) => new()
    {
        DeviceId = "dev-" + machine.ToLowerInvariant(), MachineName = machine, ClientId = clientId, ClientName = client,
        ContactId = contact.Length > 0 ? 1 : 0, ContactName = contact, UserName = "jane", Online = online,
        AgentOnline = online, ClientOnline = online, UnattendedEnabled = unattended, Unattended = unattended && online
    };

    private static TicketDetail Repairable()
    {
        var d = new TicketDetail
        {
            Ticket = new TicketSummary { Id = 1, Number = 101, Subject = "Laptop will not power on", Category = (int)TicketCategoryKind.Workshop, ClientName = "Zenith Accounting", Status = 1, CreatedUtc = DateTime.UtcNow.AddDays(-1) },
            OpenedBy = "Oscar Office", Stage = "repairable", StageLabel = "Repairable", PreviousStageLabel = "Inspected", WithWhom = "With Sue Tech",
            Actions = new()
            {
                new StageActionInfo { Key = "solved-invoice", Label = "Solved — to be invoiced", Fields = new() { "Details", "Minutes", "Amount" }, IsDecision = true },
                new StageActionInfo { Key = "quote-needed", Label = "Quote needed" },
                new StageActionInfo { Key = "do-not-invoice", Label = "Do not invoice", Fields = new() { "Reason" } }
            },
            AssetMake = "Dell", AssetModel = "Latitude 5420", AssetSerial = "7XK2Q13", Accessories = "Charger", Description = "Will not power on, even on the charger.",
            PhotosIn = 3, PhotosOut = 0, PhotosRequired = 3, People = new() { "Sue Tech", "Bob Tech", "Oscar Office" },
            MayEditLines = true, ShowQuote = true,
            QuoteMachine = new QuoteMachineInfo { Make = "Asus", Model = "VivoBook 123", Serial = "SN123", FromBooking = true },
            MachineTypes = new() { "Desktop", "Laptop", "All-in-one" }
        };
        d.QuoteLines.Add(new QuoteLineInfo { Id = 1, ItemName = "DDR4 memory 8GB SO-DIMM", PartNumber = "KVR26S19S8/8", SerialNumber = "SN-8GB-001", Quantity = 2, HasPhoto = true });
        d.Notes.Add(new TicketNoteInfo { Author = "Oscar Office", Body = "Workshop ticket opened, assigned to Sue Tech.", IsSystem = true, CreatedUtc = DateTime.UtcNow.AddHours(-20) });
        d.Notes.Add(new TicketNoteInfo { Author = "Sue Tech", Body = "Charging port loose.", FormTitle = "Inspection findings", MovedToLabel = "Inspected", CreatedUtc = DateTime.UtcNow.AddHours(-2) });
        return d;
    }

    private static TicketDetail CallOut() => new()
    {
        Ticket = new TicketSummary { Id = 2, Number = 102, Subject = "Server room switch is down", Category = (int)TicketCategoryKind.CallOut, ClientName = "Zenith Accounting", Status = 1, CreatedUtc = DateTime.UtcNow.AddDays(-1), AppointmentUtc = DateTime.UtcNow.AddDays(2) },
        OpenedBy = "Oscar Office", Stage = "callout-booked", StageLabel = "Call out booked", WithWhom = "With Sue Tech",
        Actions = new()
        {
            new StageActionInfo { Key = "work-done", Label = "Work done", Fields = new() { "Details", "Minutes" } },
            new StageActionInfo { Key = "reschedule", Label = "Reschedule", Fields = new() { "Appointment", "Reason" } }
        },
        People = new() { "Sue Tech", "Bob Tech" }
    };

    // ================================================================== plumbing

    /// <summary>The look a button style is drawn with — two styles may share one.</summary>
    private static string Look_(BtnStyle s) => typeof(RoundButton).GetMethod("LookOf", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is { } m
        ? m.Invoke(null, new object[] { s })!.ToString()! : s.ToString();

    private static IEnumerable<Control> All(Control c) { foreach (Control x in c.Controls) { yield return x; foreach (var y in All(x)) yield return y; } }

    private static string Name(Control c)
    {
        var text = c.Text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > 24) text = text[..24] + "…";
        return text.Length > 0 ? $"{c.GetType().Name} '{text}'" : c.GetType().Name + (c.Parent != null ? " in " + c.Parent.GetType().Name : "");
    }

    private static object? Field(object o, string name)
    {
        for (var type = o.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } fi) return fi.GetValue(o);
        return null;
    }

    private static void Call(object o, string method, params object[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(o, args);

    private static void Click(Form f, string text)
    {
        var b = All(f).OfType<Button>().FirstOrDefault(x => x.Visible && x.Text == text)
            ?? throw new InvalidOperationException($"no button '{text}'");
        b.PerformClick();
        Settle();
    }

    private static void Settle() { for (int i = 0; i < 14; i++) { Application.DoEvents(); Thread.Sleep(25); } }

    // ---- Windows ----
    private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100, RDW_FRAME = 0x0400;
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int GA_ROOT = 2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    private const uint PW_RENDERFULLCONTENT = 2;
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private const int LVM_GETHEADER = 0x1000 + 31;
}
