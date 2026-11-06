using System.Reflection;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// soundcheck - proves the ding and the ring (2026-10-07). Clint: "When someone receives a chat
// message, I would like it to play a sound. A simple ding ding type sound. Also when we get incoming
// support call requests ... more like a constant ringing which is not overly intrusive."
//
// Two halves.
//
//   The SOUNDS are asked of Chimes directly: valid WAVs, two notes rising in the ding, a ring of two
//   bursts with real quiet between them and a long rest before the next, and nothing clipped.
//
//   WHEN they play is asked of the REAL DashboardForm (and its office and website chat windows),
//   driven through its own methods with the messages the hub sends, pointed at a port nothing
//   listens on. Sounds.Played says what played; Sounds.IsRinging says whether it is ringing.
//
// It plays the sounds out loud while it runs. No server, no customer machine.
//
//   dotnet run -c Release --project tools/soundcheck/soundcheck.csproj
//   dotnet run -c Release --project tools/soundcheck/soundcheck.csproj -- --save <folder>
//       writes ding.wav and ring.wav there to listen to, and runs nothing else.
// ---------------------------------------------------------------------------------------------

internal static class SoundCheck
{
    private static int _pass, _fail;
    private static readonly List<string> Played = new();

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--save")
        {
            Directory.CreateDirectory(args[1]);
            File.WriteAllBytes(Path.Combine(args[1], "ding.wav"), Chimes.Ding());
            File.WriteAllBytes(Path.Combine(args[1], "ring.wav"), Chimes.Ring());
            // Three rings at the real spacing: what somebody waiting sounds like. Raw PCM copied into
            // silence, and the two sizes in the header written for the longer file.
            var ring = Chimes.Ring();
            int every = Chimes.RingEverySeconds * 22050 * 2, data = every * 3;
            var waiting = new byte[44 + data];
            Array.Copy(ring, waiting, 44);
            for (int i = 0; i < 3; i++) Array.Copy(ring, 44, waiting, 44 + i * every, ring.Length - 44);
            BitConverter.GetBytes(36 + data).CopyTo(waiting, 4);
            BitConverter.GetBytes(data).CopyTo(waiting, 40);
            File.WriteAllBytes(Path.Combine(args[1], "ringing-12-seconds.wav"), waiting);
            Console.WriteLine($"ding.wav, ring.wav and ringing-12-seconds.wav are in {args[1]}");
            return 0;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Console.WriteLine("soundcheck - the ding and the ring");

        TheSounds();

        // This PC's own mute setting is put back however the run ends.
        var saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteSupport", "sounds.json");
        var before = File.Exists(saved) ? File.ReadAllText(saved) : null;
        try
        {
            SetMutedQuietly(false);
            RemoteSupport.UI.Sounds.Played += n => Played.Add(n);
            Console_();
        }
        finally
        {
            if (before == null) File.Delete(saved); else File.WriteAllText(saved, before);
        }

        Console.WriteLine();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ================================================================ the sounds
    private static void TheSounds()
    {
        Console.WriteLine();
        Console.WriteLine("== the sounds ==");
        foreach (var (name, wav) in new[] { ("ding", Chimes.Ding()), ("ring", Chimes.Ring()) })
        {
            bool header = wav.Length > 44 && Text(wav, 0) == "RIFF" && Text(wav, 8) == "WAVE" && Text(wav, 12) == "fmt "
                && BitConverter.ToInt16(wav, 20) == 1 && BitConverter.ToInt16(wav, 22) == 1
                && BitConverter.ToInt32(wav, 24) == 22050 && BitConverter.ToInt16(wav, 34) == 16
                && Text(wav, 36) == "data" && BitConverter.ToInt32(wav, 40) == wav.Length - 44
                && BitConverter.ToInt32(wav, 4) == wav.Length - 8;
            Check($"{name}: a 22 kHz, 16-bit, mono PCM WAV whose sizes add up", header, $"{wav.Length} bytes");
            var s = Samples(wav);
            double secs = s.Length / 22050.0;
            Check($"{name}: about a second long", secs is > 0.9 and < 1.3, $"{secs:0.00} s");
            double peak = s.Max(Math.Abs);
            Check($"{name}: nothing clipped", peak < 0.99, $"peak {peak:0.000}");
            Check($"{name}: ends in silence, so it never clicks", s.TakeLast(110).All(v => Math.Abs(v) < 0.01));
            Check($"{name}: the same bytes every time it is asked", ReferenceEquals(wav, name == "ding" ? Chimes.Ding() : Chimes.Ring()));
        }

        var d = Samples(Chimes.Ding());
        var r = Samples(Chimes.Ring());
        // Two notes, rising: C6 strongest before the second is struck, E6 strongest after.
        double c1 = Goertzel(d, 0.02, 0.15, 1046.5), e1 = Goertzel(d, 0.02, 0.15, 1318.5);
        double c2 = Goertzel(d, 0.20, 0.35, 1046.5), e2 = Goertzel(d, 0.20, 0.35, 1318.5);
        Check("ding: two notes — C6 first", c1 > 5 * e1, $"C6 {c1:0.0} vs E6 {e1:0.0}");
        Check("ding: ...then E6, higher", e2 > c2, $"E6 {e2:0.0} vs C6 {c2:0.0}");

        double burst1 = Rms(r, 0.05, 0.40), gap = Rms(r, 0.47, 0.58), burst2 = Rms(r, 0.67, 1.02);
        Check("ring: two bursts with real quiet between them", burst1 > 0.05 && burst2 > 0.05 && gap < 0.005,
            $"bursts {burst1:0.000} and {burst2:0.000}, the gap {gap:0.0000}");
        Check("ring: quieter than the ding — it repeats, so it must not shout", r.Max(Math.Abs) < d.Max(Math.Abs) * 0.75,
            $"ring peak {r.Max(Math.Abs):0.00}, ding {d.Max(Math.Abs):0.00}");
        Check("ring: more rest than sound — it rings every few seconds, not constantly",
            Chimes.RingEverySeconds - r.Length / 22050.0 >= 2.5, $"every {Chimes.RingEverySeconds} s, {r.Length / 22050.0:0.00} s of it sound");
    }

    // ================================================================ when they play
    private static void Console_()
    {
        Console.WriteLine();
        Console.WriteLine("== the console ==");
        var brand = Branding.Load();
        var link = new TechLink("ws://127.0.0.1:59999");
        typeof(TechLink).GetField("<DisplayName>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(link, "Tess Tech");
        var f = new DashboardForm(brand, link)
        { StartPosition = FormStartPosition.Manual, WindowState = FormWindowState.Normal, Location = new Point(0, 0), Size = new Size(1920, 900) };
        f.Show();
        Pump(15);
        // Opened at its size and never resized: the first layout ran before the window was on screen,
        // when every Visible reads false. Reading the bell's Visible back there hid Light/Dark for
        // good (dashtabs, 131 of 133 until the layout kept flags of its own).
        Check("a window never resized still shows the bell AND Light/Dark",
            Field<RoundButton>(f, "_soundBtn")!.Visible && Field<RoundButton>(f, "_modeBtn")!.Visible);

        var customer = new ChatMsg { DeviceId = "dev-1", From = "Sue Jones", FromTech = false, Text = "my screen is frozen", Ts = DateTimeOffset.UtcNow };

        // ---- what a (re)connect hands back is drawn, never dinged
        Fresh();
        Call(f, "LoadState", new StateMsg { You = "Tess Tech", RecentChat = new() { customer, customer } });
        Check("signing in: the recent chat handed back does not ding", Played.Count == 0 && !RemoteSupport.UI.Sounds.IsRinging, Said());

        // ---- the session chat card
        Fresh();
        Call(f, "AppendChat", customer, true);
        Check("a customer's chat message dings", Played.SequenceEqual(new[] { "ding" }), Said());
        Fresh();
        Call(f, "AppendChat", new ChatMsg { DeviceId = "dev-1", From = "Tess Tech", FromTech = true, Text = "on it", Ts = DateTimeOffset.UtcNow }, true);
        Check("my own message does not", Played.Count == 0, Said());
        Fresh();
        Call(f, "AppendChat", customer, true);
        Call(f, "AppendChat", customer, true);
        Call(f, "AppendChat", customer, true);
        Check("three at once ding once", Played.Count == 1, Said());

        // ---- calls ring while they wait
        var a = new CallInfo { CallId = "call-a", DeviceId = "dev-1", MachineName = "ZEN-SUE", ContactName = "Sue Jones", Issue = "frozen", Status = CallStatus.Ringing };
        Fresh();
        Call(f, "UpsertCall", a);
        Check("a support request rings at once", RemoteSupport.UI.Sounds.IsRinging && Played.SequenceEqual(new[] { "ring" }), Said());
        Pump((Chimes.RingEverySeconds * 1000 + 400) / 40);
        Check($"...and again {Chimes.RingEverySeconds} s later while it waits", Played.Count(p => p == "ring") == 2, Said());
        Call(f, "UpsertCall", Copy(a, CallStatus.Accepted));
        Check("answered: it stops", !RemoteSupport.UI.Sounds.IsRinging);
        Fresh();
        Pump((Chimes.RingEverySeconds * 1000 + 400) / 40);
        Check("...and stays stopped", Played.Count == 0, Said());

        var b = Copy(a, CallStatus.Ringing, "call-b"); var c = Copy(a, CallStatus.Ringing, "call-c");
        Call(f, "UpsertCall", b); Call(f, "UpsertCall", c);
        Dismiss(f, "call-b");
        Check("two waiting, one dismissed here: still ringing for the other", RemoteSupport.UI.Sounds.IsRinging);
        Dismiss(f, "call-c");
        Check("both dismissed here: it stops", !RemoteSupport.UI.Sounds.IsRinging);
        Call(f, "UpsertCall", Copy(b, CallStatus.Cancelled)); Call(f, "UpsertCall", Copy(c, CallStatus.Cancelled));
        var d = Copy(a, CallStatus.Ringing, "call-d");
        Call(f, "UpsertCall", d);
        Check("a new request rings again after a dismissal", RemoteSupport.UI.Sounds.IsRinging);
        Call(f, "UpsertCall", Copy(d, CallStatus.Cancelled));
        Check("the customer cancels: it stops", !RemoteSupport.UI.Sounds.IsRinging);
        Fresh();
        Call(f, "LoadState", new StateMsg { You = "Tess Tech", Calls = new() { Copy(a, CallStatus.Ringing, "call-e") } });
        Check("signing in while somebody is already waiting rings", RemoteSupport.UI.Sounds.IsRinging && Played.Contains("ring"), Said());
        Call(f, "LoadState", new StateMsg { You = "Tess Tech" });
        Check("...and a reconnect showing nobody waiting stops it", !RemoteSupport.UI.Sounds.IsRinging);

        // ---- website chat: a visitor nobody has answered rings; the one I answer dings
        var web = Field<WebChatForm>(f, "_webChat")!;
        Fresh();
        Call(web, "TakeChat", new WebChatInfo { Id = 7, Name = "Visitor" });
        Check("website chat: a visitor waiting rings", RemoteSupport.UI.Sounds.IsRinging && Played.Contains("ring"), Said());
        Fresh();
        Call(web, "TakeLine", new WebChatLineMsg { ChatId = 7, Line = new() { ChatId = 7, Id = 1, FromVisitor = true, Name = "Visitor", Text = "hello?" } });
        Check("...their lines while nobody has answered do not ding over the ring", !Played.Contains("ding"), Said());
        Call(web, "TakeChat", new WebChatInfo { Id = 7, Name = "Visitor", AnsweredBy = "Tess Tech" });
        Check("...answered: the ring stops", !RemoteSupport.UI.Sounds.IsRinging);
        Fresh();
        Call(web, "TakeLine", new WebChatLineMsg { ChatId = 7, Line = new() { ChatId = 7, Id = 2, FromVisitor = true, Name = "Visitor", Text = "thanks" } });
        Check("...the visitor I am answering dings", Played.SequenceEqual(new[] { "ding" }), Said());
        Call(web, "TakeChat", new WebChatInfo { Id = 8, Name = "Other visitor", AnsweredBy = "Colin Colleague" });
        Fresh();
        Call(web, "TakeLine", new WebChatLineMsg { ChatId = 8, Line = new() { ChatId = 8, Id = 3, FromVisitor = true, Name = "Other visitor", Text = "hi" } });
        Check("...a visitor a colleague is answering does not", Played.Count == 0 && !RemoteSupport.UI.Sounds.IsRinging, Said());

        // ---- a hand-over (2026-10-15): the dings move with the chat
        Fresh();
        Call(web, "TakeChat", new WebChatInfo { Id = 7, Name = "Visitor", AnsweredBy = "Colin Colleague", HandedOverBy = "Tess Tech" });
        Call(web, "TakeLine", new WebChatLineMsg { ChatId = 7, Line = new() { ChatId = 7, Id = 4, Name = "", Note = true, Text = "Colin Colleague has taken over this chat." } });
        Check("hand-over: giving my chat away plays nothing — and its note line does not ding", Played.Count == 0, Said());
        Call(web, "TakeLine", new WebChatLineMsg { ChatId = 7, Line = new() { ChatId = 7, Id = 5, FromVisitor = true, Name = "Visitor", Text = "still there?" } });
        Check("...and that visitor's lines no longer ding for me", Played.Count == 0, Said());
        Call(web, "TakeChat", new WebChatInfo { Id = 8, Name = "Other visitor", AnsweredBy = "Tess Tech", HandedOverBy = "Colin Colleague" });
        Check("...a colleague's chat handed TO me dings once, as it arrives", Played.SequenceEqual(new[] { "ding" }), Said());
        Fresh();
        Call(web, "TakeLine", new WebChatLineMsg { ChatId = 8, Line = new() { ChatId = 8, Id = 6, FromVisitor = true, Name = "Other visitor", Text = "hello again" } });
        Check("...and its visitor's lines ding for me from then on", Played.SequenceEqual(new[] { "ding" }), Said());
        Fresh();
        Call(web, "TakeChat", new WebChatInfo { Id = 9, Name = "Waiting visitor" });
        Check("a waiting visitor rings", RemoteSupport.UI.Sounds.IsRinging);
        Call(web, "TakeChat", new WebChatInfo { Id = 9, Name = "Waiting visitor", AnsweredBy = "Colin Colleague", HandedOverBy = "Sam Supervisor" });
        Check("...given to a colleague before anybody answered: the ring stops, and nothing dings for me", !RemoteSupport.UI.Sounds.IsRinging && !Played.Contains("ding"), Said());
        Call(web, "TakeChat", new WebChatInfo { Id = 9, Name = "Waiting visitor", AnsweredBy = "Tess Tech", HandedOverBy = "Sam Supervisor" });
        Check("...then on to me: it dings for me", Played.Count(p => p == "ding") == 1 && !RemoteSupport.UI.Sounds.IsRinging, Said());
        Fresh();
        Call(web, "TakeChat", new WebChatInfo { Id = 10, Name = "Self served", AnsweredBy = "Tess Tech", HandedOverBy = "Tess Tech" });
        Check("...taking one myself is not news to me: no ding", Played.Count == 0, Said());

        // ---- office chat
        var staff = Field<StaffChatForm>(f, "_staffChat")!;
        Fresh();
        Call(staff, "TakeMessage", new StaffChatMsg { Message = new() { Id = 1, From = "Colin Colleague", To = "Tess Tech", Text = "lunch?", SentUtc = DateTime.UtcNow } });
        Check("office chat: a message to me dings", Played.SequenceEqual(new[] { "ding" }), Said());
        Fresh();
        Call(staff, "TakeMessage", new StaffChatMsg { Message = new() { Id = 2, From = "Colin Colleague", To = "", Text = "cake in the kitchen", SentUtc = DateTime.UtcNow } });
        Check("office chat: a message to Everyone dings", Played.SequenceEqual(new[] { "ding" }), Said());
        Fresh();
        Call(staff, "TakeMessage", new StaffChatMsg { Message = new() { Id = 3, From = "Tess Tech", To = "Colin Colleague", Text = "yes", SentUtc = DateTime.UtcNow } });
        Check("office chat: my own message does not", Played.Count == 0, Said());
        staff.Hide();
        web.Hide();

        // ---- mute
        var btn = Field<RoundButton>(f, "_soundBtn")!;
        var tray = Field<ToolStripMenuItem>(f, "_traySoundItem")!;
        Check("the header has a bell to mute with, and the tray menu has Mute sounds", btn.Visible && btn.Text == "🔔" && btn.AccessibleName == "Mute sounds" && tray.Text == "Mute sounds", $"\"{btn.Text}\" ({btn.AccessibleName}) / \"{tray.Text}\"");
        btn.PerformClick();
        Pump(3);
        Check("clicking it mutes: the bell is crossed out, and both then offer Unmute sounds", RemoteSupport.UI.Sounds.Muted && btn.Text == "🔕" && btn.AccessibleName == "Unmute sounds" && tray.Text == "Unmute sounds", $"\"{btn.Text}\" / \"{tray.Text}\"");
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteSupport", "sounds.json");
        Check("it is remembered on this PC", File.Exists(file) && File.ReadAllText(file).Contains("true"), File.Exists(file) ? File.ReadAllText(file) : "no file");
        Fresh();
        Call(f, "AppendChat", customer, true);
        Call(f, "UpsertCall", Copy(a, CallStatus.Ringing, "call-f"));
        Check("muted: a message and a waiting call play nothing", Played.Count == 0, Said());
        Check("...though the console still knows somebody is waiting", RemoteSupport.UI.Sounds.IsRinging);
        Fresh();
        tray.PerformClick();
        Pump((Chimes.RingEverySeconds * 1000 + 400) / 40);
        Check("unmuted from the tray menu: the call that is still waiting rings again", !RemoteSupport.UI.Sounds.Muted && Played.Contains("ring") && btn.Text == "🔔", Said());
        Call(f, "UpsertCall", Copy(a, CallStatus.Cancelled, "call-f"));

        // ---- the header: room for it, and nothing on the company's words
        foreach (var width in new[] { 1920, 1366 })
        {
            f.Size = new Size(width, 800);
            Pump(10);
            var mode = Field<RoundButton>(f, "_modeBtn")!;
            var nt = Field<RoundButton>(f, "_newTicketBtn")!;
            var header = f.Controls.OfType<BrandHeader>().First();
            int leftmost = new[] { mode, btn, nt }.Where(x => x.Visible).Min(x => x.Left);
            Check($"header at {width} px: no button on the header's words", header.TextRight <= leftmost, $"words end {header.TextRight}, left-most button {leftmost}");
            Check($"header at {width} px: Light/Dark steps out before Mute sounds does", !mode.Visible || btn.Visible,
                $"Light/Dark {(mode.Visible ? "shown" : "out")}, Mute sounds {(btn.Visible ? "shown" : "out")}");
            if (btn.Visible) Check($"header at {width} px: Mute sounds sits left of New ticket without touching it", btn.Right < nt.Left && btn.Top == nt.Top);
            // The bell exists so the header keeps Light/Dark at a desktop width; the words did not.
            if (width == 1920) Check("header at 1920 px: the bell AND Light/Dark both fit", btn.Visible && mode.Visible);
            if (btn.Visible) Check($"header at {width} px: the bell is drawn (not an empty box)", Ink(f, btn) > 20, $"{Ink(f, btn)} px of glyph");
            var shotPath = Path.Combine(Path.GetTempPath(), $"soundcheck-header-{width}.png");
            using (var bmp = new Bitmap(header.Width, header.Height)) { header.DrawToBitmap(bmp, new Rectangle(0, 0, header.Width, header.Height)); bmp.Save(shotPath); }
            Console.WriteLine($"        (the header: {shotPath})");
        }

        // ---- the free version has nothing to mute
        Call(f, "LoadState", new StateMsg { You = "Tess Tech", Free = true });
        Pump(10);
        Check("free version: no Mute sounds in the header or the tray menu", !btn.Visible && !tray.Visible);
        Field<NotifyIcon>(f, "_tray")?.Dispose();
        f.Dispose();
    }

    // ================================================================ helpers
    private static void Fresh()
    {
        Pump(2);
        Played.Clear();
        // The ding's "several at once ding once" window, so each check starts from silence.
        typeof(RemoteSupport.UI.Sounds).GetField("_lastDing", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, DateTime.MinValue);
    }

    private static void SetMutedQuietly(bool b) => RemoteSupport.UI.Sounds.Muted = b;

    private static string Said() => Played.Count == 0 ? "nothing played" : "played: " + string.Join(", ", Played);

    private static CallInfo Copy(CallInfo c, CallStatus s, string? id = null) => new()
    { CallId = id ?? c.CallId, DeviceId = c.DeviceId, MachineName = c.MachineName, ContactName = c.ContactName, Issue = c.Issue, Status = s, Created = c.Created };

    private static void Dismiss(DashboardForm f, string callId)
    {
        ((HashSet<string>)f.GetType().GetField("_dismissedCalls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f)!).Add(callId);
        Call(f, "UpdatePopup");
    }

    private static void Call(object o, string method, params object[] args)
    {
        var m = o.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).First(x => x.Name == method && x.GetParameters().Length == args.Length);
        m.Invoke(o, args);
        Pump(2);
    }

    private static void Pump(int n) { for (int i = 0; i < n; i++) { Application.DoEvents(); Thread.Sleep(40); } }

    private static T? Field<T>(object o, string field) where T : class =>
        o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o) as T;

    /// <summary>Pixels inside the button, away from its edge, that differ clearly from its centre-left
    /// ground: the glyph. An emoji the font cannot draw shows as nothing or as a thin box.</summary>
    private static int Ink(Form f, Control b)
    {
        using var bmp = new Bitmap(b.Width, b.Height);
        b.DrawToBitmap(bmp, new Rectangle(0, 0, b.Width, b.Height));
        var ground = bmp.GetPixel(6, b.Height / 2);
        int n = 0;
        for (int y = b.Height / 5; y < b.Height * 4 / 5; y++)
            for (int x = b.Width / 5; x < b.Width * 4 / 5; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (Math.Abs(p.R - ground.R) + Math.Abs(p.G - ground.G) + Math.Abs(p.B - ground.B) > 120) n++;
            }
        return n;
    }

    private static string Text(byte[] b, int at) => System.Text.Encoding.ASCII.GetString(b, at, 4);

    private static double[] Samples(byte[] wav)
    {
        var s = new double[(wav.Length - 44) / 2];
        for (int i = 0; i < s.Length; i++) s[i] = BitConverter.ToInt16(wav, 44 + i * 2) / 32768.0;
        return s;
    }

    private static double Rms(double[] s, double from, double to)
    {
        int a = (int)(from * 22050), b = Math.Min(s.Length, (int)(to * 22050));
        double sum = 0; for (int i = a; i < b; i++) sum += s[i] * s[i];
        return Math.Sqrt(sum / Math.Max(1, b - a));
    }

    /// <summary>How much of one frequency is in a stretch of samples.</summary>
    private static double Goertzel(double[] s, double from, double to, double freq)
    {
        int a = (int)(from * 22050), b = Math.Min(s.Length, (int)(to * 22050));
        double w = 2 * Math.PI * freq / 22050, k = 2 * Math.Cos(w), p1 = 0, p2 = 0;
        for (int i = a; i < b; i++) { double p = s[i] + k * p1 - p2; p2 = p1; p1 = p; }
        return Math.Sqrt(p1 * p1 + p2 * p2 - k * p1 * p2);
    }
}
