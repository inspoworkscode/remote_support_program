using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// siteshots — the screenshots on the public website, made from the REAL windows.
//
// Written 2026-10-14 for Clint: "can we add some screenshots so that people can get a good look and
// feel about what the product is about?" — and the website switches between light and dark like the
// product does, so every picture is made twice, once in each, and the page shows the one that matches.
//
// Nothing here is anybody's real data. The IT company is "Coastline IT", made up, in the product's own
// purple with a logo drawn below; its customers, their people and their machines are made up; the
// customer's screen inside the session window is drawn below too. No server: every link points at a
// port nothing listens on, and each window is handed what the server would have sent it, the way
// tools/lookprobe does. Never point this at a live server or put a real customer's name in it — these
// pictures are published.
//
//   dotnet run -c Release --project tools/siteshots/siteshots.csproj -- [outDir]
//
// outDir defaults to src/RemoteSupport.Platform/wwwroot/shots. It takes the screen for a minute: each
// window is put on top and read back from the screen. Leave the mouse and keyboard alone while it runs.
// ---------------------------------------------------------------------------------------------

internal static class SiteShots
{
    private static string _out = "";
    private static ThemeMode _mode;
    private static int _made, _failed;

    // The made-up company and its made-up customers. Jane Smith at Zenith Accounting is the story that
    // runs through the pictures: she asks for help, she is on the console, her screen is in the session.
    private const string Company = "Coastline IT";
    private const string Tech = "Sue Tech";

    [STAThread]
    private static int Main(string[] args)
    {
        var root = FindRepoRoot() ?? AppContext.BaseDirectory;
        _out = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "src", "RemoteSupport.Platform", "wwwroot", "shots");
        Directory.CreateDirectory(_out);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using (var g = Graphics.FromHwnd(IntPtr.Zero))
            Console.WriteLine($"siteshots — this screen is at {g.DpiX:0} DPI ({g.DpiX / 96f:P0}). Pictures go to {_out}");

        var work = Path.Combine(Path.GetTempPath(), "rs-siteshots");
        Directory.CreateDirectory(work);
        var brand = Branding.Load();
        brand.CompanyName = Company;
        brand.ProductName = Company + " Support";
        brand.PrimaryColor = "#2e1065";
        brand.AccentColor = "#7c3aed";
        brand.SupportPhone = "021 555 0100";
        brand.SupportEmail = "help@coastline-it.example";
        brand.ServerUrl = "http://127.0.0.1:59999";   // nothing listens: these windows look, they do not talk
        brand.Logo = MakeLogo(Path.Combine(work, "logo.png"));
        brand.WelcomeText = $"Need help? Click Request Support and a {Company} technician will connect with you.";

        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            _mode = mode;
            Theme.Override = mode;
            Console.WriteLine($"==== {mode} ====");
            var t = Theme.FromBranding(brand, AppSurface.Technician);
            var ct = Theme.FromBranding(brand, AppSurface.Customer);
            Console_(t, brand);
            Session(t, brand);
            Tickets(t, brand);
            Performance(t);
            Customer(ct, brand);
        }
        Theme.Override = null;
        Console.WriteLine();
        Console.WriteLine($"{_made} pictures made, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    // ================================================================== the technician's side

    private static void Console_(Theme t, Branding brand)
    {
        Shot("console", () => new DashboardForm(brand, Link()), new Size(1860, 1060), f =>
        {
            var now = DateTimeOffset.UtcNow;
            Call(f, "LoadState", new StateMsg
            {
                You = Tech,
                OnlineTechnicians = new() { "Ann Tech", "Bob Tech", Tech },
                AllTechnicians = new() { "Ann Tech", "Bob Tech", Tech },
                Devices = new()
                {
                    Dev("ZEN-RECEPTION", "Zenith Accounting", 1, "Jane Smith", true, false, "Windows 11 Pro", "Intel Core i5-1235U", 16, 476, 251),
                    Dev("ZEN-ACCOUNTS", "Zenith Accounting", 1, "Peter Naidoo", true, false, "Windows 11 Pro", "Intel Core i7-1255U", 16, 953, 610),
                    Dev("ZEN-PARTNER", "Zenith Accounting", 1, "Lindiwe Dube", false, false, "Windows 11 Pro", "AMD Ryzen 7 5825U", 32, 953, 702),
                    Dev("HV-FRONTDESK", "Harbour View Dental", 2, "Megan Botha", true, false, "Windows 10 Pro", "Intel Core i5-10500", 8, 238, 61),
                    Dev("HV-SURGERY1", "Harbour View Dental", 2, "Dr Sam Pillay", true, false, "Windows 11 Pro", "Intel Core i5-12400", 16, 476, 330),
                    Dev("HV-XRAY", "Harbour View Dental", 2, "Megan Botha", true, true, "Windows 10 Pro", "Intel Core i7-9700", 16, 953, 401),
                    Dev("MILKWOOD-PC1", "Milkwood Guest House", 3, "Thandi Mokoena", true, false, "Windows 11 Home", "Intel Core i3-1215U", 8, 238, 97),
                    Dev("PROTEA-LAPTOP2", "Protea Legal", 4, "André Venter", false, false, "Windows 11 Pro", "Intel Core i7-1360P", 16, 953, 512),
                    Dev("PROTEA-SERVER", "Protea Legal", 4, "", true, true, "Windows Server 2022", "Intel Xeon E-2336", 64, 3725, 2210),
                },
                Sessions = new()
                {
                    new SessionInfo { SessionId = "s1", DeviceId = "dev-hv-frontdesk", MachineName = "HV-FRONTDESK", StartedBy = "Bob Tech",
                        Technicians = new() { "Bob Tech" }, HostConnected = true, Started = now.AddMinutes(-7) },
                },
                RecentChat = new()
                {
                    new ChatMsg { DeviceId = "dev-zen-reception", From = "Jane Smith", Text = "Hi, my email keeps asking for my password since this morning.", Ts = now.AddMinutes(-4) },
                    new ChatMsg { DeviceId = "dev-zen-reception", From = Tech, FromTech = true, Text = "Morning Jane, I'll have a look now. Please press Allow when it asks.", Ts = now.AddMinutes(-3) },
                    new ChatMsg { DeviceId = "dev-zen-reception", From = "Jane Smith", Text = "Thank you!", Ts = now.AddMinutes(-3) },
                },
            });
            Connected(f, t);
            // Jane's machine selected, the way it is just before a double-click.
            if (Field(f, "_devices") is ListView lv)
                foreach (ListViewItem it in lv.Items) if (it.Text == "Jane Smith" || it.SubItems.Cast<ListViewItem.ListViewSubItem>().Any(s => s.Text == "ZEN-RECEPTION")) { it.Selected = true; break; }
        });
    }

    private static void Session(Theme t, Branding brand)
    {
        var session = new SessionInfo { SessionId = "s2", DeviceId = "dev-zen-reception", MachineName = "ZEN-RECEPTION", StartedBy = Tech,
            Technicians = new() { Tech }, HostConnected = true };
        Shot("session", () => new SessionForm("ws://127.0.0.1:59999", "shot-token", session, t, brand), new Size(1720, 1060), f =>
        {
            ((SessionForm)f).ShowKnowledge(new KnowledgeCountMsg { DeviceId = session.DeviceId, ClientId = 1, ClientName = "Zenith Accounting", Count = 4 });
            var view = (ScreenView)Field(f, "_view")!;
            view.Reset(1920, 1080);
            var canvas = (Bitmap)Field(view, "_canvas")!;
            canvas.SetResolution(144, 144);   // the same picture whatever this PC's own scaling is
            using (var g = Graphics.FromImage(canvas)) CustomerDesktop(g, 1920, 1080, brand, withBanner: true);
            view.SetRemoteCursor(0, 0, false, 0);
            view.Invalidate();
            // What the host would have told it: the monitor, who is in the session, and that the link is up.
            if (Field(f, "_monitorBox") is ComboBox mb) { mb.Items.Clear(); mb.Items.Add("1: 1920×1080"); mb.SelectedIndex = 0; }
            if (Field(f, "_partLabel") is Label pl) pl.Text = "Participants: " + Tech;
        }, last: f => Call(f, "SetPill", "● connected", true));
    }

    private static void Tickets(Theme t, Branding brand)
    {
        Shot("tickets", () => new TicketsForm(Link(), t, brand), new Size(1860, 1080), f =>
        {
            var today = DateTime.UtcNow;
            Call(f, "OnList", new TicketListMsg
            {
                Filter = "mine",
                Tickets = new()
                {
                    Sum(101, "Laptop will not power on", TicketCategoryKind.Workshop, "Zenith Accounting", "Repairable", today.AddDays(-1)),
                    Sum(102, "Email keeps asking for a password", TicketCategoryKind.RemoteSupport, "Zenith Accounting", "In progress", today.AddHours(-1)),
                    Sum(103, "Network switch down in the surgery", TicketCategoryKind.CallOut, "Harbour View Dental", "Call out booked", today.AddDays(-2)),
                    Sum(104, "Monthly backup check", TicketCategoryKind.Recurring, "Protea Legal", "Due", today.AddDays(-3)),
                    Sum(105, "New printer at reception", TicketCategoryKind.WalkIn, "Milkwood Guest House", "Booked in", today.AddDays(-3)),
                    Sum(106, "Quote: three laptops for new staff", TicketCategoryKind.General, "Protea Legal", "Quote needed", today.AddDays(-4)),
                    Sum(107, "Slow computer at the front desk", TicketCategoryKind.RemoteSupport, "Harbour View Dental", "Solved — to be invoiced", today.AddDays(-5)),
                }
            });
            f.ShowTicket(Workshop());
        });
    }

    private static void Performance(Theme t)
    {
        var dev = Dev("ZEN-RECEPTION", "Zenith Accounting", 1, "Jane Smith", true, false, "Windows 11 Pro", "Intel Core i5-1235U", 16, 476, 251);
        Shot("performance", () => new MachineWatchForm(Link(), t, dev, MachineWatchForm.Page.Performance), null, f =>
        {
            var w = (MachineWatchForm)f;
            var start = DateTimeOffset.UtcNow.AddSeconds(-60);
            for (int i = 0; i <= 60; i++)
            {
                double x = i / 60.0;
                w.Apply(new MachineSample
                {
                    Ts = start.AddSeconds(i), LogicalProcessors = 12,
                    CpuPercent = 14 + 9 * Math.Sin(i / 4.0) + (i is > 30 and < 40 ? 38 * Math.Sin((i - 30) / 10.0 * Math.PI) : 0),
                    MemoryTotalMb = 16_103, MemoryUsedMb = (long)(9_200 + 600 * x),
                    DiskActivePercent = 4 + 3 * Math.Abs(Math.Sin(i / 3.0)) + (i is > 31 and < 38 ? 30 : 0),
                    DiskReadBytesPerSec = (long)(2_400_000 + 1_800_000 * Math.Abs(Math.Sin(i / 5.0))), DiskWriteBytesPerSec = 640_000,
                    NetReceiveBytesPerSec = (long)(180_000 + 120_000 * Math.Abs(Math.Cos(i / 6.0))), NetSendBytesPerSec = 42_000,
                    UptimeSeconds = 3 * 86_400 + 5 * 3600 + 17 * 60 + i, ProcessCount = 212, ThreadCount = 2_840,
                    Networks = new()
                    {
                        new NetworkProfile { Name = "Zenith-Office", Category = "Private", Adapter = "Wi-Fi", Address = "192.168.10.24" },
                    },
                });
            }
        });
    }

    // ================================================================== the customer's side

    private static void Customer(Theme ct, Branding brand)
    {
        // The request window, filled in, with the picture of her screen it offers to send.
        Shot("request", () =>
        {
            using var shot = new Bitmap(1920, 1080);
            shot.SetResolution(144, 144);
            using (var g = Graphics.FromImage(shot)) CustomerDesktop(g, 1920, 1080, brand, withBanner: false);
            var jpeg = new MemoryStream();
            shot.Save(jpeg, ImageFormat.Jpeg);
            var preview = new Bitmap(shot, new Size(960, 540));
            var snap = (RemoteSupport.Client.ScreenSnap)Activator.CreateInstance(typeof(RemoteSupport.Client.ScreenSnap),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { jpeg.ToArray(), preview, false }, null)!;
            return new RemoteSupport.Client.RequestDialog("Jane Smith", ct, brand, snap);
        }, null, f =>
        {
            Type(f, "_subject", "Email keeps asking for my password");
            Type(f, "_details", "Since this morning my email asks for my password every few minutes and nothing sends.");
            if (Field(f, "_name") is InputBox name) { name.Box.SelectionStart = name.Box.TextLength; name.Box.SelectionLength = 0; }
            if (Field(f, "_details") is InputBox details) { details.Box.Focus(); details.Box.SelectionStart = details.Box.TextLength; }
        });

        // What she sees the moment a technician connects: who, and Allow or Deny.
        var consent = typeof(RemoteSupport.Host.HostEngine).Assembly.GetType("RemoteSupport.Host.ConsentPrompt+ConsentForm", throwOnError: true)!;
        Shot("allow", () => (Form)Activator.CreateInstance(consent, brand, Tech, 60, "")!);

        // The tray app's own window: the Request Support button with the company's name on it.
        Shot("customer-app", () => new RemoteSupport.Client.MainForm(brand), null, f =>
        {
            // A word from the technician, as it would be a minute into her morning.
            Call(f, "AppendChat", new ChatMsg { From = Tech, FromTech = true, Text = "Morning Jane, I'll have a look now. Please press Allow when it asks.", Ts = DateTimeOffset.UtcNow.AddMinutes(-3) });
            Call(f, "AppendChat", new ChatMsg { From = "Jane Smith", Text = "Thank you!", Ts = DateTimeOffset.UtcNow.AddMinutes(-3) });
        }, last: f =>
        {
            // Connected. Last: its link keeps trying the port nobody listens on, and says so each time.
            if (Field(f, "_link") is { } link) SetField(link, "OnConnectedChanged", null);   // it stops saying so
            SetField(f, "_connected", true);
            Call(f, "UpdateStatus");
        });
    }

    // ================================================================== fixtures

    private static TechLink Link()
    {
        var link = new TechLink("ws://127.0.0.1:59999");
        typeof(TechLink).GetProperty("DisplayName")!.SetValue(link, Tech);
        typeof(TechLink).GetProperty("Roster")!.SetValue(link, new List<string> { "Ann Tech", "Bob Tech", Tech });
        return link;
    }

    /// <summary>The status pill says connected, as it would a second after signing in.</summary>
    private static void Connected(Form f, Theme t)
    {
        if (Field(f, "_statusPill") is Pill p) p.Set("● connected", t.OkBg, t.OkText);
    }

    private static DeviceInfo Dev(string machine, string client, int clientId, string contact, bool online, bool unattended,
        string os, string cpu, int ramGb, int diskGb, int freeGb) => new()
    {
        DeviceId = "dev-" + machine.ToLowerInvariant(), MachineName = machine, ClientId = clientId, ClientName = client,
        ContactId = contact.Length > 0 ? clientId * 10 + machine.Length : 0, ContactName = contact,
        UserName = contact.Length > 0 ? contact.Split(' ')[0].ToLowerInvariant() : "administrator", Os = os,
        Online = online, AgentOnline = online, ClientOnline = online && !unattended,
        UnattendedEnabled = unattended, Unattended = unattended && online, CanWatch = online,
        LastSeen = DateTimeOffset.UtcNow.AddMinutes(online ? 0 : -190),
        Spec = new SystemSpec { Os = os, Cpu = cpu, Cores = 8, RamMb = ramGb * 1024, DiskTotalGb = diskGb, DiskFreeGb = freeGb },
    };

    private static TicketSummary Sum(int n, string subject, TicketCategoryKind cat, string client, string stage, DateTime created) => new()
    {
        Id = n - 100, Number = n, Subject = subject, Category = (int)cat, ClientName = client, Status = 1, StageLabel = stage,
        AssignedTo = Tech, CreatedUtc = created, UpdatedUtc = created,
    };

    private static TicketDetail Workshop()
    {
        var now = DateTime.UtcNow;
        var d = new TicketDetail
        {
            Ticket = Sum(101, "Laptop will not power on", TicketCategoryKind.Workshop, "Zenith Accounting", "Repairable", now.AddDays(-1)),
            OpenedBy = "Oscar Office", Stage = "repairable", StageLabel = "Repairable", PreviousStageLabel = "Inspected", WithWhom = "With " + Tech,
            Actions = new()
            {
                new StageActionInfo { Key = "solved-invoice", Label = "Solved — to be invoiced", Fields = new() { "Details", "Minutes", "Amount" }, IsDecision = true },
                new StageActionInfo { Key = "quote-needed", Label = "Quote needed" },
                new StageActionInfo { Key = "do-not-invoice", Label = "Do not invoice", Fields = new() { "Reason" } }
            },
            AssetMake = "Dell", AssetModel = "Latitude 5420", AssetSerial = "7XK2Q13", Accessories = "Charger",
            Description = "Will not power on, even on the charger. Died during a meeting.",
            PhotosIn = 4, PhotosOut = 0, PhotosRequired = 3, People = new() { Tech, "Bob Tech", "Oscar Office" },
            MayEditLines = true, ShowQuote = true,
            MachineTypes = new() { "Desktop", "Laptop", "All-in-one" }
        };
        d.QuoteLines.Add(new QuoteLineInfo { Id = 1, ItemName = "Charging port, Latitude 5420", PartNumber = "DC-IN-5420", Quantity = 1 });
        d.Notes.Add(new TicketNoteInfo { Author = "Oscar Office", Body = "Workshop ticket opened, assigned to Sue Tech.", IsSystem = true, CreatedUtc = now.AddHours(-26) });
        d.Notes.Add(new TicketNoteInfo { Author = Tech, Body = "The charging port is loose and the battery reports 41% health. Port to be replaced; a new battery recommended.",
            FormTitle = "Inspection findings", MovedToLabel = "Inspected", CreatedUtc = now.AddHours(-5) });
        d.Notes.Add(new TicketNoteInfo { Author = "Oscar Office", Body = "@Sue Tech Peter at Zenith approved the battery too — go ahead with both.", CreatedUtc = now.AddHours(-2) });
        d.Notes.Add(new TicketNoteInfo { Author = Tech, Body = "Both parts in stock. Repair under way.", CreatedUtc = now.AddMinutes(-40) });
        return d;
    }

    // ================================================================== drawn: a logo and a customer's screen

    /// <summary>Coastline IT's logo: a wave in a purple circle. Made up, like the company.</summary>
    private static string MakeLogo(string path)
    {
        using var bmp = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var fill = new LinearGradientBrush(new Rectangle(8, 8, 240, 240), Color.FromArgb(0x8b, 0x5c, 0xf6), Color.FromArgb(0x4c, 0x1d, 0x95), 45f))
                g.FillEllipse(fill, 8, 8, 240, 240);
            using var pen = new Pen(Color.White, 20) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawBezier(pen, 52, 150, 92, 96, 120, 96, 136, 140);
            g.DrawBezier(pen, 136, 140, 152, 184, 180, 184, 204, 120);
            using var thin = new Pen(Color.FromArgb(200, 255, 255, 255), 12) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawBezier(thin, 70, 190, 100, 168, 150, 206, 186, 182);
        }
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>
    /// Jane's screen: a Windows desktop with a mail program asking for her password again — the fault she rang about.
    /// Drawn, not captured: a capture would be somebody's real screen, and these pictures are published.
    /// </summary>
    private static void CustomerDesktop(Graphics g, int w, int h, Branding brand, bool withBanner)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        // Wallpaper: a deep blue with two soft blooms.
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(0x0b, 0x2a, 0x5b), Color.FromArgb(0x1a, 0x5f, 0xb4), 35f))
            g.FillRectangle(bg, 0, 0, w, h);
        Bloom(g, new RectangleF(w * .45f, h * .05f, w * .7f, h * 1.1f), Color.FromArgb(150, 0x6c, 0xb6, 0xff));
        Bloom(g, new RectangleF(w * .55f, h * .25f, w * .4f, h * .6f), Color.FromArgb(120, 0xc4, 0xe0, 0xff));

        // Desktop icons, left.
        using var iconFont = new Font("Segoe UI", 11f);
        DesktopIcon(g, 24, 20, Color.FromArgb(0xe8, 0xee, 0xf6), "Recycle Bin", iconFont, glyph: "♻");
        DesktopIcon(g, 24, 160, Color.FromArgb(0xf5, 0xc4, 0x51), "Client files", iconFont, glyph: "");
        DesktopIcon(g, 24, 300, Color.FromArgb(0x7c, 0x3a, 0xed), brand.CompanyName + " Support", iconFont, glyph: "", logo: brand.LogoPath);

        // The mail program's window.
        var win = new Rectangle(250, 110, 1250, 760);
        Shadow(g, win, 18);
        Round(g, win, 10, Color.White);
        using (var titleBrush = new SolidBrush(Color.FromArgb(0xf3, 0xf3, 0xf3))) FillTop(g, win, 44, 10, titleBrush);
        using var title = new Font("Segoe UI", 10.5f);
        using var body = new Font("Segoe UI", 10.5f);
        using var bold = new Font("Segoe UI Semibold", 10.5f);
        using var ink = new SolidBrush(Color.FromArgb(0x1b, 0x1b, 0x1b));
        using var grey = new SolidBrush(Color.FromArgb(0x60, 0x60, 0x60));
        g.DrawString("Inbox — jane@zenith-accounting.example — Mail", title, ink, win.X + 16, win.Y + 12);
        g.DrawString("—      ▢      ✕", title, grey, win.Right - 130, win.Y + 12);
        // Left folder pane.
        using (var pane = new SolidBrush(Color.FromArgb(0xf7, 0xf8, 0xfa))) g.FillRectangle(pane, win.X, win.Y + 44, 230, win.Height - 54);
        using (var blue = new SolidBrush(Color.FromArgb(0x0f, 0x6c, 0xbd))) Round(g, new Rectangle(win.X + 16, win.Y + 62, 198, 40), 6, blue.Color);
        g.DrawString("✉  New mail", bold, Brushes.White, win.X + 34, win.Y + 72);
        string[] folders = { "Inbox   12", "Drafts", "Sent items", "Clients", "Invoices", "Archive" };
        for (int i = 0; i < folders.Length; i++)
        {
            if (i == 0) using (var sel = new SolidBrush(Color.FromArgb(0xe3, 0xec, 0xf7))) g.FillRectangle(sel, win.X + 8, win.Y + 122, 214, 34);
            g.DrawString(folders[i], i == 0 ? bold : body, ink, win.X + 26, win.Y + 128 + i * 40);
        }
        // Message list.
        (string from, string subject, string when)[] mail =
        {
            ("SARS eFiling", "Your submission has been received", "08:52"),
            ("Peter Naidoo", "Re: Quarterly figures for Milkwood", "08:31"),
            ("Harbour View Dental", "Statement for September", "Yesterday"),
            ("Lindiwe Dube", "Partner meeting moved to Thursday", "Yesterday"),
            ("Bank notifications", "Payment confirmation", "Mon"),
            ("Protea Legal", "Signed engagement letter", "Mon"),
            ("Thandi Mokoena", "VAT201 question", "Fri"),
        };
        int lx = win.X + 246, ly = win.Y + 62;
        for (int i = 0; i < mail.Length; i++)
        {
            int y = ly + i * 72;
            if (i == 1) using (var sel = new SolidBrush(Color.FromArgb(0xe8, 0xf1, 0xfb))) g.FillRectangle(sel, lx - 8, y - 6, 420, 68);
            g.DrawString(mail[i].from, bold, ink, lx, y);
            g.DrawString(mail[i].when, body, grey, lx + 330, y);
            g.DrawString(mail[i].subject, body, grey, lx, y + 26);
            using var rule = new Pen(Color.FromArgb(0xe6, 0xe6, 0xe6));
            g.DrawLine(rule, lx - 8, y + 62, lx + 412, y + 62);
        }
        // Reading pane.
        int rx = win.X + 690;
        g.DrawString("Re: Quarterly figures for Milkwood", new Font("Segoe UI Semibold", 15f), ink, rx, win.Y + 64);
        g.DrawString("Peter Naidoo  ·  08:31", body, grey, rx, win.Y + 104);
        using (var para = new Font("Segoe UI", 10.5f))
        {
            g.DrawString("Hi Jane,\n\nThe figures are attached. Could you send the\nfinal pack to the client before Friday?\n\nThanks,\nPeter", para, ink, rx, win.Y + 150);
        }

        // The fault: the password prompt, over the mail.
        var dlg = new Rectangle(win.X + 380, win.Y + 210, 520, 300);
        Shadow(g, dlg, 24);
        Round(g, dlg, 10, Color.White);
        using (var edge = new Pen(Color.FromArgb(0xd0, 0xd0, 0xd0))) RoundEdge(g, dlg, 10, edge);
        g.DrawString("Windows Security", body, grey, dlg.X + 22, dlg.Y + 16);
        g.DrawString("Enter your password", new Font("Segoe UI Semibold", 16f), ink, dlg.X + 22, dlg.Y + 46);
        g.DrawString("jane@zenith-accounting.example", body, grey, dlg.X + 22, dlg.Y + 90);
        using (var box = new Pen(Color.FromArgb(0x8a, 0x8a, 0x8a))) g.DrawRectangle(box, dlg.X + 22, dlg.Y + 130, dlg.Width - 44, 38);
        g.DrawString("Password", body, grey, dlg.X + 32, dlg.Y + 139);
        g.DrawString("☐  Remember my credentials", body, ink, dlg.X + 22, dlg.Y + 186);
        Round(g, new Rectangle(dlg.X + 22, dlg.Bottom - 62, 230, 40), 5, Color.FromArgb(0x00, 0x67, 0xc0));
        g.DrawString("OK", bold, Brushes.White, dlg.X + 126, dlg.Bottom - 53);
        Round(g, new Rectangle(dlg.Right - 252, dlg.Bottom - 62, 230, 40), 5, Color.FromArgb(0xf3, 0xf3, 0xf3));
        g.DrawString("Cancel", bold, ink, dlg.Right - 162, dlg.Bottom - 53);

        // Taskbar.
        using (var bar = new SolidBrush(Color.FromArgb(235, 0xee, 0xf1, 0xf6))) g.FillRectangle(bar, 0, h - 56, w, 56);
        int cx = w / 2 - 3 * 52;
        Color[] apps = { Color.FromArgb(0x00, 0x78, 0xd4), Color.FromArgb(0xf5, 0xc4, 0x51), Color.FromArgb(0x10, 0x9e, 0x8a), Color.FromArgb(0x0f, 0x6c, 0xbd), Color.FromArgb(0x21, 0x73, 0x46), Color.FromArgb(0x7c, 0x3a, 0xed) };
        for (int i = 0; i < apps.Length; i++)
        {
            var r = new Rectangle(cx + i * 52 + 8, h - 46, 36, 36);
            if (i == 3) using (var on = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) FillRound(g, new Rectangle(r.X - 6, r.Y - 4, 48, 44), 6, on);
            if (i == 0)
            {
                using var b = new SolidBrush(apps[0]);
                for (int q = 0; q < 4; q++) g.FillRectangle(b, r.X + 3 + (q % 2) * 16, r.Y + 3 + (q / 2) * 16, 14, 14);
            }
            else Round(g, r, 8, apps[i]);
        }
        using (var clock = new Font("Segoe UI", 10f))
        {
            g.DrawString("09:14", clock, ink, w - 110, h - 50);
            g.DrawString("2026/10/14", clock, ink, w - 128, h - 28);
        }

        // The bar the customer sees while a technician is connected.
        if (withBanner)
        {
            var b = new Rectangle(w / 2 - 300, 0, 600, 44);
            FillRoundBottom(g, b, 14, Color.FromArgb(0x6d, 0x28, 0xd9));
            using var bf = new Font("Segoe UI Semibold", 11f);
            var words = $"●  {Tech} from {brand.CompanyName} is connected";
            var size = g.MeasureString(words, bf);
            g.DrawString(words, bf, Brushes.White, b.X + (b.Width - size.Width) / 2, b.Y + (b.Height - size.Height) / 2);
        }
    }

    private static void Bloom(Graphics g, RectangleF r, Color c)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(r);
        using var pb = new PathGradientBrush(path) { CenterColor = c, SurroundColors = new[] { Color.FromArgb(0, c) } };
        g.FillEllipse(pb, r);
    }

    private static void DesktopIcon(Graphics g, int x, int y, Color c, string label, Font font, string glyph, string? logo = null)
    {
        var r = new Rectangle(x + 22, y, 52, 52);
        if (logo != null && File.Exists(logo)) using (var img = Image.FromFile(logo)) g.DrawImage(img, r);
        else
        {
            Round(g, r, 8, c);
            if (glyph.Length > 0) using (var gf = new Font("Segoe UI Symbol", 20f)) g.DrawString(glyph, gf, Brushes.DimGray, r.X + 9, r.Y + 6);
        }
        var fmt = new StringFormat { Alignment = StringAlignment.Center };
        var box = new RectangleF(x - 14, y + 58, 124, 76);
        using (var sh = new SolidBrush(Color.FromArgb(140, 0, 0, 0))) g.DrawString(label, font, sh, new RectangleF(box.X + 1, box.Y + 1, box.Width, box.Height), fmt);
        g.DrawString(label, font, Brushes.White, box, fmt);
    }

    private static GraphicsPath RoundPath(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
    private static void Round(Graphics g, Rectangle r, int rad, Color c) { using var b = new SolidBrush(c); FillRound(g, r, rad, b); }
    private static void FillRound(Graphics g, Rectangle r, int rad, Brush b) { using var p = RoundPath(r, rad); g.FillPath(b, p); }
    private static void RoundEdge(Graphics g, Rectangle r, int rad, Pen pen) { using var p = RoundPath(r, rad); g.DrawPath(pen, p); }
    private static void FillTop(Graphics g, Rectangle win, int height, int rad, Brush b)
    {
        var clip = g.Clip;
        g.SetClip(new Rectangle(win.X, win.Y, win.Width, height));
        FillRound(g, win, rad, b);
        g.Clip = clip;
    }
    private static void FillRoundBottom(Graphics g, Rectangle r, int rad, Color c)
    {
        using var b = new SolidBrush(c);
        FillRound(g, new Rectangle(r.X, r.Y - rad, r.Width, r.Height + rad), rad, b);
    }
    private static void Shadow(Graphics g, Rectangle r, int spread)
    {
        for (int i = spread; i > 0; i -= 3)
        {
            using var b = new SolidBrush(Color.FromArgb(Math.Max(2, 26 - i), 0, 0, 0));
            FillRound(g, new Rectangle(r.X - i, r.Y - i + spread / 2, r.Width + i * 2, r.Height + i * 2), 10 + i, b);
        }
    }

    // ================================================================== taking one picture

    /// <param name="size">The client area in screen pixels, set once the window is on the screen (so after Windows has
    /// scaled it); null keeps the window's own size.</param>
    private static void Shot<T>(string name, Func<T> make, Size? size = null, Action<T>? prepare = null, Action<T>? last = null) where T : Form
    {
        Console.Write($"  {name} ({_mode}) … ");
        T? f = null;
        try
        {
            f = make();
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(0, 0);
            f.Show();
            Settle();
            if (size is { } s) { f.WindowState = FormWindowState.Normal; f.ClientSize = s; f.Location = new Point(0, 0); Settle(); }
            prepare?.Invoke(f);
            Settle(); Settle();
            // The pointer out of the way: over a window it raises a tooltip, and the tooltip is in the picture.
            var screen = Screen.FromControl(f).Bounds;
            Cursor.Position = new Point(screen.Right - 2, screen.Top + screen.Height / 2);
            OnTop(f);
            // Last, because some windows put their own state back as they go: a link that cannot reach a server says so.
            if (last != null) { last(f); Settle(); }
            RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME);
            Settle();
            using var bmp = Capture(f);
            // PNG keeps the words sharp. The session is the exception: its customer's wallpaper is a photograph-like
            // gradient, 400 KB as a PNG and a quarter of that as a JPEG that looks the same.
            bool jpeg = name == "session";
            var file = Path.Combine(_out, $"{name}-{_mode.ToString().ToLowerInvariant()}.{(jpeg ? "jpg" : "png")}");
            if (jpeg)
            {
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var ps = new EncoderParameters(1);
                ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                bmp.Save(file, codec, ps);
            }
            else bmp.Save(file, ImageFormat.Png);
            Console.WriteLine($"{bmp.Width}x{bmp.Height}, {new FileInfo(file).Length / 1024} KB");
            _made++;
        }
        catch (Exception ex) { Console.WriteLine("FAILED — " + ex.GetBaseException().Message); _failed++; }
        finally
        {
            try { f?.Hide(); f?.Dispose(); } catch { }
            Settle();
        }
    }

    /// <summary>The window's client area as it is on the screen — so the window must be on top and wholly on the screen.</summary>
    private static Bitmap Capture(Form f)
    {
        var cr = f.RectangleToScreen(f.ClientRectangle);
        var screen = Screen.FromControl(f).Bounds;
        if (!screen.Contains(cr)) throw new InvalidOperationException($"the window ({cr}) does not fit on the screen ({screen}) — make it smaller");
        var bmp = new Bitmap(cr.Width, cr.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(cr.Location, Point.Empty, cr.Size);
        return bmp;
    }

    private static void OnTop(Form f)
    {
        foreach (Form o in Application.OpenForms) if (o != f && o.TopMost) o.TopMost = false;
        for (int i = 0; i < 40; i++)
        {
            f.TopMost = false; f.TopMost = true;
            SetWindowPos(f.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            f.Activate(); Settle();
            var c = f.RectangleToScreen(f.ClientRectangle);
            var hit = WindowFromPoint(new Point(c.X + c.Width / 2, c.Y + c.Height / 2));
            if (hit != IntPtr.Zero && GetAncestor(hit, GA_ROOT) == f.Handle) return;
        }
    }

    // ================================================================== plumbing

    private static void Type(Form f, string field, string text)
    {
        if (Field(f, field) is InputBox box) box.Box.Text = text;
    }

    private static object? Field(object o, string name)
    {
        for (var type = o.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } fi) return fi.GetValue(o);
        return null;
    }

    private static void SetField(object o, string name, object? value)
    {
        for (var type = o.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } fi) { fi.SetValue(o, value); return; }
        throw new MissingFieldException(o.GetType().Name, name);
    }

    private static void Call(object o, string method, params object[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(o, args);

    private static void Settle() { for (int i = 0; i < 14; i++) { Application.DoEvents(); Thread.Sleep(25); } }

    private static string? FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "src", "RemoteSupport.Platform"))) return d.FullName;
        return null;
    }

    private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100, RDW_FRAME = 0x0400;
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);
    private const int GA_ROOT = 2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, int flags);
}
