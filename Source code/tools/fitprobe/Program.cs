using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// fitprobe — does each window hold its own words, at the scaling it is actually run at?
//
// Written 2026-10-02 for internal testing's report:
//   2. the microphone question was covered by a Windows notification — now a window of its own;
//   3. Tools → Info "opens a blank window" — pale text on white, in the dark theme;
//   4. the technician was not shown that the customer was being asked — the waiting window;
//   6. the sign-in window cut its labels off "on certain resolutions", and "Forgot your password?"
//      sat at the left instead of the middle.
// And 2026-10-02 (HANDOVER §77): the Connect window with an unattended password, and the tray app's
// password window — which it caught with a button off the left of the window.
//
// The REAL windows, opened on this machine at whatever scaling Windows is set to (HANDOVER §30:
// two of three earlier faults of this kind were invisible at 100%), in the light AND the dark
// theme. Nothing is sent anywhere: the session window points at a port nothing listens on.
// Pictures of each go to the output folder, to be looked at as well as counted.
//
//   dotnet run -c Release --project tools/fitprobe/fitprobe.csproj [outDir]
// ---------------------------------------------------------------------------------------------

internal static class FitProbe
{
    private static int _pass, _fail;
    private static string _out = "";

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        _out = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "fit");
        Directory.CreateDirectory(_out);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        using (var g = Graphics.FromHwnd(IntPtr.Zero))
            Console.WriteLine($"This screen is at {g.DpiX:0} DPI ({g.DpiX / 96f:P0}). Pictures go to {_out}");

        var brand = Branding.Load();
        brand.CompanyName = "Anywhere IT";
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            Theme.Override = mode;
            var tech = Theme.FromBranding(brand, AppSurface.Technician);
            var cust = Theme.FromBranding(brand, AppSurface.Customer);
            Console.WriteLine();
            Console.WriteLine($"==== {mode} ====");
            SignIn(brand, mode);
            Waiting(tech, mode);
            Mic(cust, mode);
            Info(tech, brand, mode);
            PasswordWindows(tech, cust, brand, mode);
            OutsiderWindows(cust, brand, mode);
            VisitWindows(tech, mode);
            ConsentPromptWindows(brand, mode);
        }
        Theme.Override = null;

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    // ---- 6. the sign-in window ------------------------------------------------------------------
    private static void SignIn(Branding brand, ThemeMode mode)
    {
        Console.WriteLine("-- sign in (item 6)");
        using var f = new LoginForm(brand) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        Show(f);
        var card = All(f).OfType<Card>().First();
        LabelsFit(card, "sign-in");

        var link = All(card).OfType<LinkLabel>().Single();
        Check("\"Forgot your password?\" spans the card, so there is a middle to centre it in",
              !link.AutoSize && link.Width >= card.ClientSize.Width - card.Padding.Horizontal - 2, $"{link.Width} of {card.ClientSize.Width - card.Padding.Horizontal} px");
        Check("and is centred", link.TextAlign == ContentAlignment.MiddleCenter);

        // Nothing overlaps and nothing falls off the bottom of the card.
        var parts = card.Controls.Cast<Control>().Where(c => c.Visible && c.Height > 0).OrderBy(c => c.Top).ToList();
        bool overlap = parts.Zip(parts.Skip(1)).Any(p => p.First.Bottom > p.Second.Top);
        Check("no part of it overlaps the next", !overlap, string.Join(", ", parts.Select(p => $"{Name(p)}@{p.Top}-{p.Bottom}")));
        Check("and the card holds all of it", parts.Max(p => p.Bottom) <= card.ClientSize.Height - card.Padding.Bottom + 1,
              $"lowest at {parts.Max(p => p.Bottom)}, card ends at {card.ClientSize.Height - card.Padding.Bottom}");
        var button = parts.OfType<RoundButton>().Single();
        Check("from the top: button, message, link — the link at the very bottom",
              link.Top > button.Bottom, $"button ends {button.Bottom}, link starts {link.Top}");
        Snap(f, $"signin-{mode}");
    }

    // ---- 4 and 5. waiting for the person at the machine ---------------------------------------
    private static void Waiting(Theme t, ThemeMode mode)
    {
        Console.WriteLine("-- waiting for them to accept (items 4, 5)");
        var s = new SessionInfo { SessionId = "probe", DeviceId = "probe", MachineName = "DESKTOP-OPHQIDH", StartedBy = "Probe" };
        using var f = new ConsentWaitForm(t, s) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        Show(f);
        LabelsFit(f, "connecting");
        Snap(f, $"wait-connecting-{mode}");

        Check("it stays up while they are being asked", f.Update(new SessionInfo { SessionId = "probe", MachineName = s.MachineName, AwaitingConsent = true, ConsentSeconds = 60 }));
        Pump();
        LabelsFit(f, "asking");
        Check("and says so, with the time left", Texts(f).Any(x => x.StartsWith("Waiting for DESKTOP-OPHQIDH to accept")) && Texts(f).Any(x => x.Contains("declined in 1:00") || x.Contains("declined in 0:59")),
              string.Join(" | ", Texts(f)));
        Snap(f, $"wait-asking-{mode}");

        var why = "Nobody at DESKTOP-OPHQIDH answered within 60 seconds, so the connection was not allowed.";
        Check("when it ends it stays up to say why", f.Update(new SessionInfo { SessionId = "probe", MachineName = s.MachineName, Ended = true, EndReason = why }));
        Pump();
        LabelsFit(f, "ended");
        Check("in the hub's own words", Texts(f).Contains(why));
        Check("and its button now just closes it", All(f).OfType<RoundButton>().Single().Text == "Close");
        Snap(f, $"wait-ended-{mode}");

        using var g = new ConsentWaitForm(t, s);
        Check("and a yes is the end of waiting: the caller opens the session window",
              !g.Update(new SessionInfo { SessionId = "probe", MachineName = s.MachineName, HostConnected = true }));
    }

    // ---- 2. the microphone question ---------------------------------------------------------------
    private static void Mic(Theme t, ThemeMode mode)
    {
        Console.WriteLine("-- the microphone question (item 2)");
        using var f = new RemoteSupport.Client.MicQuestion(t, "Anywhere IT Remote Support");
        bool? answer = null;
        f.Answered += a => answer = a;
        Check("it opens in the middle of the screen, on top", f.StartPosition == FormStartPosition.CenterScreen && f.TopMost);
        f.Ask("Jason");
        Pump();
        var wa = Screen.FromControl(f).WorkingArea;
        var mid = new Point(f.Left + f.Width / 2, f.Top + f.Height / 2);
        Check("and it IS in the middle", Math.Abs(mid.X - (wa.Left + wa.Width / 2)) < 4 && Math.Abs(mid.Y - (wa.Top + wa.Height / 2)) < 4,
              $"centre {mid}, screen centre {wa.Left + wa.Width / 2},{wa.Top + wa.Height / 2}");
        LabelsFit(f, "microphone question");
        foreach (var b in All(f).OfType<RoundButton>())
            Check($"\"{b.Text}\" fits its button", TextRenderer.MeasureText(b.Text, b.Font).Width + 16 <= b.Width, $"{b.Width} px");
        Check("there is no default button — an Enter meant for something else cannot answer it", f.AcceptButton == null);
        Snap(f, $"mic-{mode}");

        f.Close();   // the X
        Pump();
        Check("closing it with the X answers \"Not now\"", answer == false && !f.Visible);
        answer = null;
        f.Ask("Jason");
        Pump();
        f.Withdraw();
        Check("withdrawn when the call ends, without an answer", answer == null && !f.Visible);
    }

    // ---- 3. Tools → Info -----------------------------------------------------------------------
    private static void Info(Theme t, Branding brand, ThemeMode mode)
    {
        Console.WriteLine("-- Tools → Info (item 3)");
        var s = new SessionInfo { SessionId = "probe", DeviceId = "probe", MachineName = "DESKTOP-STD8M9C", StartedBy = "Probe" };
        using var f = new SessionForm("ws://127.0.0.1:59999", "probe-token", s, t, brand) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        Show(f);
        var box = typeof(SessionForm).GetField("_infoList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f) as Control;
        if (box == null) { Check("found the Info text", false); return; }
        double ratio = Contrast(box.ForeColor, box.BackColor);
        Check("its text is readable on its own ground (WCAG AA, 4.5:1)", ratio >= 4.5,
              $"{ratio:0.0}:1 — {Hex(box.ForeColor)} on {Hex(box.BackColor)}");
        Check("and the ground is the theme's, not Windows' white", box.BackColor.ToArgb() == t.Surface.ToArgb(), Hex(box.BackColor));

        var longLine = "the logged-on user, through the support app — UAC prompts show black, elevated windows cannot be controlled, and scripts are not run as administrator.";
        typeof(SessionForm).GetMethod("HandleMsg", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f, new object[] { new SysInfoMsg { Info = new() { ["Machine"] = "DESKTOP-STD8M9C", ["Host running as"] = longLine } } });
        Pump();
        Check("what the machine says arrives in it", box.Text.Contains("Host running as: " + longLine));
        Check("and a long line wraps rather than running off the edge", box is RichTextBox { WordWrap: true });
    }

    // ---- the unattended password (2026-10-02) ----------------------------------------------------
    private static void PasswordWindows(Theme tech, Theme cust, Branding brand, ThemeMode mode)
    {
        Console.WriteLine("-- the unattended password: Connect, and the tray app's window");
        using (var f = new ReasonDialog(tech, "DESKTOP-OPHQIDH", "Clint", askPassword: true, reason: "Checking my own machine from the office",
                   complaint: "That is not the unattended password for DESKTOP-OPHQIDH. 4 more tries before it refuses for 15 minutes.")
               { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) })
        {
            Show(f);
            var card = All(f).OfType<Card>().First();
            LabelsFit(card, "connect, with password");
            Stacked(card, "connect, with password");
            Snap(f, $"connect-password-{mode}");
        }
        // The free version's Connect (2026-10-06): the password alone, no reason.
        using (var f = new ReasonDialog(tech, "BRANCH-A", "", askPassword: true, askReason: false)
               { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) })
        {
            Show(f);
            var card = All(f).OfType<Card>().First();
            LabelsFit(card, "free connect, password only");
            Stacked(card, "free connect, password only");
            Snap(f, $"free-connect-password-{mode}");
        }
        // Removing a machine on the full version (2026-10-06): the reason and the note, online and offline.
        foreach (var online in new[] { true, false })
        {
            using var f = new RemoveDeviceDialog(tech, "RECEPTION-OLD-LAPTOP", online) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            Show(f);
            var card = All(f).OfType<Card>().First();
            var where = online ? "remove, online" : "remove, offline";
            LabelsFit(card, where);
            Stacked(card, where);
            foreach (var b in All(f).OfType<RoundButton>())
                Check($"{where}: \"{b.Text}\" fits its button and the window",
                    TextRenderer.MeasureText(b.Text, b.Font).Width + 16 <= b.Width && b.Left >= 0 && b.Right <= b.Parent!.ClientSize.Width, $"{b.Width} px");
            Snap(f, $"remove-device-{(online ? "online" : "offline")}-{mode}");
        }
        foreach (var has in new[] { false, true })
        {
            using var f = new RemoteSupport.Client.UnattendedPasswordDialog(cust, brand, hasPassword: has, unattendedOn: !has)
                { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            Show(f);
            var card = All(f).OfType<Card>().First();
            var where = has ? "change password" : "set password";
            LabelsFit(card, where);
            Stacked(card, where);
            foreach (var b in All(f).OfType<RoundButton>())
                Check($"{where}: \"{b.Text}\" fits its button", TextRenderer.MeasureText(b.Text, b.Font).Width + 16 <= b.Width, $"{b.Width} px");
            // Each button fitting its words is not enough: the first run had three that did, in a row
            // that ran off the left of the window.
            foreach (var b in All(f).OfType<RoundButton>())
                Check($"{where}: \"{b.Text}\" is inside the window", b.Left >= 0 && b.Right <= b.Parent!.ClientSize.Width, $"{b.Left}..{b.Right} of {b.Parent!.ClientSize.Width}");
            Snap(f, $"tray-password-{(has ? "change" : "set")}-{mode}");
        }
        // The free version's install window (2026-10-06): add this computer, or change its password.
        foreach (var installed in new[] { false, true })
        {
            using var f = new RemoteSupport.Client.FreeInstallForm(brand, "branch-offices", "https://example.invalid", installed)
                { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            Show(f);
            var card = All(f).OfType<Card>().First();
            var where = installed ? "free install, change password" : "free install, add";
            LabelsFit(card, where);
            Stacked(card, where);
            foreach (var b in All(f).OfType<RoundButton>())
            {
                Check($"{where}: \"{b.Text}\" fits its button", TextRenderer.MeasureText(b.Text, b.Font).Width + 16 <= b.Width, $"{b.Width} px");
                Check($"{where}: \"{b.Text}\" is inside the window", b.Left >= 0 && b.Right <= b.Parent!.ClientSize.Width, $"{b.Left}..{b.Right} of {b.Parent!.ClientSize.Width}");
            }
            Snap(f, $"free-install-{(installed ? "change" : "add")}-{mode}");
        }
        Check("the tray app's rule is the server's: 7 characters refused, 8 taken, two different refused",
              RemoteSupport.Client.UnattendedPasswordDialog.Problem("1234567", "1234567").Length > 0
              && RemoteSupport.Client.UnattendedPasswordDialog.Problem("12345678", "12345678") == ""
              && RemoteSupport.Client.UnattendedPasswordDialog.Problem("12345678", "12345679").Length > 0);
    }

    // ---- the customer's Allow prompt (the host's), for their own IT company and for somebody from outside --
    // Never looked at by eye before 2026-10-06: it is drawn by the host, in a real session. Private to the
    // host, so it is made by reflection — the REAL window, not a copy of it.
    private static void ConsentPromptWindows(Branding brand, ThemeMode mode)
    {
        Console.WriteLine("-- the customer's Allow prompt");
        var type = typeof(RemoteSupport.Host.HostEngine).Assembly.GetType("RemoteSupport.Host.ConsentPrompt+ConsentForm", throwOnError: true)!;
        foreach (var (who, company, where) in new[]
                 {
                     ("Sue Tech", "", "prompt, own company"),
                     ("Victoria Montgomery-Smith (Smith & Co Chartered Accountants, a guest — not checked)", "Smith & Co Chartered Accountants, a guest — not checked", "prompt, from outside")
                 })
        {
            using var f = (Form)Activator.CreateInstance(type, brand, who, 60, company)!;
            f.StartPosition = FormStartPosition.Manual; f.Location = new Point(40, 40);
            Show(f);
            LabelsFit(f, where);
            var box = All(f).OfType<CheckBox>().FirstOrDefault();
            if (company.Length > 0)
            {
                Check($"{where}: the files box is there, and UNTICKED", box is { Checked: false, Visible: true });
                if (box != null)
                {
                    var need = TextRenderer.MeasureText(box.Text, box.Font, new Size(box.ClientSize.Width - 24, int.MaxValue), TextFormatFlags.WordBreak).Height;
                    Check($"{where}: \"{Short(box.Text)}\" fits its box", need <= box.ClientSize.Height, $"needs {need} px, has {box.ClientSize.Height}");
                }
            }
            else Check($"{where}: no files box — their own company's technician", box == null);
            foreach (var b in All(f).OfType<RoundButton>())
                Check($"{where}: \"{b.Text}\" inside the window", b.Left >= 0 && b.Right <= b.Parent!.ClientSize.Width);
            Snap(f, $"consent-{(company.Length > 0 ? "outsider" : "own")}-{mode}");
        }
    }

    // ---- outside help, step 2 (2026-10-06): the console's code box, and the pop-up its own company sees --
    private static void VisitWindows(Theme tech, ThemeMode mode)
    {
        Console.WriteLine("-- outside help: Connect with a code, and the visit pop-up");
        using (var f = new CodeEntryDialog(tech) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) })
        {
            Show(f);
            var card = All(f).OfType<Card>().First();
            LabelsFit(card, "code box");
            Stacked(card, "code box");
            foreach (var b in All(f).OfType<RoundButton>())
                Check($"code box: \"{b.Text}\" fits its button and is inside the window",
                    TextRenderer.MeasureText(b.Text, b.Font).Width + 16 <= b.Width && b.Left >= 0 && b.Right <= b.Parent!.ClientSize.Width, $"{b.Left}..{b.Right} of {b.Parent!.ClientSize.Width}");
            Snap(f, $"console-code-{mode}");
        }
        Check("the code box reads a code as the server does",
            CodeEntryDialog.Problem("123 456-789") == "" && CodeEntryDialog.Problem("12345").Length > 0 && CodeEntryDialog.Problem("").Length > 0);
        using (var p = new VisitorPopup(tech, new OutsiderVisitMsg
               {
                   SessionId = "s", DeviceId = "d", MachineName = "ACCOUNTS-LAPTOP-07",
                   Visitor = "Victoria Montgomery-Smith (Smith & Co Chartered Accountants IT)", ForWhom = "Smith & Co Chartered Accountants", MadeBy = "Annabel at ACCOUNTS-LAPTOP-07"
               }) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) })
        {
            Show(p);
            LabelsFit(p, "visit pop-up");
            foreach (var b in All(p).OfType<RoundButton>())
                Check($"visit pop-up: \"{b.Text}\" fits its button and is inside the window",
                    TextRenderer.MeasureText(b.Text, b.Font).Width + 16 <= b.Width && b.Left >= 0 && b.Right <= b.Parent!.ClientSize.Width, $"{b.Left}..{b.Right} of {b.Parent!.ClientSize.Width}");
            Snap(p, $"console-visit-{mode}");
        }
        // The GUEST console's one window (step 3): its own theme, no company.
        using (var g = new GuestForm("https://theremotesupportprogram.com") { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) })
        {
            Show(g);
            var card = All(g).OfType<Card>().First();
            LabelsFit(card, "guest window");
            Stacked(card, "guest window");
            foreach (var b in All(g).OfType<RoundButton>())
                Check($"guest window: \"{b.Text}\" fits its button and is inside the window",
                    TextRenderer.MeasureText(b.Text, b.Font).Width + 16 <= b.Width && b.Left >= 0 && b.Right <= b.Parent!.ClientSize.Width, $"{b.Left}..{b.Right} of {b.Parent!.ClientSize.Width}");
            Snap(g, $"console-guest-{mode}");
        }
        Check("guest mode: --guest, and the guest file name, make a guest console; an ordinary console is not one",
            GuestForm.GuestServer(new[] { "--guest", "--server", "https://x.example" }, "C:\\a\\RemoteSupport.Technician.exe") == "https://x.example"
            && GuestForm.GuestServer(Array.Empty<string>(), "C:\\Downloads\\remote-support-guest (1).exe") != null
            && GuestForm.GuestServer(Array.Empty<string>(), "C:\\Downloads\\anywhere-it-console.exe") == null);
        Check("the guest window's rules are the server's: a name, then a nine-digit code",
            GuestForm.Problem("", "", "123456789").Length > 0 && GuestForm.Problem("Jane", "", "1234").Length > 0 && GuestForm.Problem("Jane", "", "123 456 789") == "");

        // ---- a technician brings somebody in (2026-10-22, §155): Get outside help, both faces; Let them in?; the pop-up ----
        void Buttons(Form f, string where)
        {
            foreach (var b in All(f).OfType<RoundButton>().Where(b => b.Visible))
                Check($"{where}: \"{b.Text}\" fits its button and is inside the window",
                    TextRenderer.MeasureText(b.Text, b.Font).Width + 16 <= b.Width && b.Left >= 0 && b.Right <= b.Parent!.ClientSize.Width, $"{b.Left}..{b.Right} of {b.Parent!.ClientSize.Width}");
        }
        Control Face(Form f) => All(f).OfType<FlowLayoutPanel>().First(fl => fl.Visible && fl.Controls.OfType<RoundButton>().Any(b => b.Visible)).Parent!;
        using (var o = new OutsideHelpForm(tech, "ACCOUNTS-LAPTOP-07", "https://theremotesupportprogram.com", "Victoria Montgomery-Smith", "Anywhere I.T Solutions (Pty) Ltd")
               { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) })
        {
            Show(o);
            LabelsFit(Face(o), "get outside help, who for");
            Stacked(Face(o), "get outside help, who for");
            Buttons(o, "get outside help, who for");
            Snap(o, $"console-invite-ask-{mode}");
            o.Apply(new OutsiderInviteMsg
            {
                SessionId = "s", Code = "123456789", For = "Victoria Montgomery-Smith, Smith & Co Chartered Accountants IT",
                ExpiresUtc = DateTime.UtcNow.AddMinutes(10), State = "Waiting for them to type it."
            });
            Pump();
            var face = Face(o);
            LabelsFit(face, "get outside help, the code");
            Stacked(face, "get outside help, the code");
            Buttons(o, "get outside help, the code");
            Check("get outside help: the message holds the code and the browser link with it",
                All(o).OfType<TextBox>().Any(t => t.Text.Contains("Code: 123 456 789") && t.Text.Contains("/guest/web#123456789")));
            Snap(o, $"console-invite-code-{mode}");
            o.Apply(new OutsiderInviteMsg { SessionId = "s", State = "Victoria Montgomery-Smith (Smith & Co Chartered Accountants IT) is connected, with files.",
                Connected = "Victoria Montgomery-Smith (Smith & Co Chartered Accountants IT)" });
            Pump();
            LabelsFit(Face(o), "get outside help, connected");
            Buttons(o, "get outside help, connected");
            Check("get outside help: once they are in, Remove them is offered", All(o).OfType<RoundButton>().Any(b => b.Visible && b.Text == "Remove them"));
            Snap(o, $"console-invite-connected-{mode}");
        }
        using (var d = new OutsiderAllowDialog(tech, new OutsiderJoinAskMsg
               {
                   SessionId = "s", MachineName = "ACCOUNTS-LAPTOP-07", Seconds = 120, ForWhom = "Smith & Co Chartered Accountants",
                   Visitor = "Victoria Montgomery-Smith (Smith & Co Chartered Accountants, a guest — not checked)"
               }) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) })
        {
            Show(d);
            var card = All(d).OfType<Card>().First();
            LabelsFit(card, "let them in?");
            Stacked(card, "let them in?");
            Buttons(d, "let them in?");
            var box = All(d).OfType<RoundCheck>().FirstOrDefault();
            Check("let them in?: the files box is there, and UNTICKED", box is { Checked: false, Visible: true });
            if (box != null)
            {
                var need = TextRenderer.MeasureText(box.Text, box.Font, new Size(box.ClientSize.Width - 24, int.MaxValue), TextFormatFlags.WordBreak).Height;
                Check($"let them in?: \"{Short(box.Text)}\" fits its box", need <= box.ClientSize.Height, $"needs {need} px, has {box.ClientSize.Height}");
            }
            Snap(d, $"console-letin-{mode}");
            d.Close();
        }
        using (var p = new VisitorPopup(tech, new OutsiderVisitMsg
               {
                   SessionId = "s", DeviceId = "d", MachineName = "ACCOUNTS-LAPTOP-07", InvitedBy = "Victoria Montgomery-Smith", MadeBy = "Victoria Montgomery-Smith",
                   Visitor = "Bartholomew Featherstonehaugh (Smith & Co Chartered Accountants IT)", ForWhom = "Smith & Co Chartered Accountants"
               }) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) })
        {
            Show(p);
            LabelsFit(p, "visit pop-up, brought in");
            Buttons(p, "visit pop-up, brought in");
            Check("visit pop-up, brought in: Remove, not End it", All(p).OfType<RoundButton>().Any(b => b.Text == "Remove") && !All(p).OfType<RoundButton>().Any(b => b.Text == "End it"));
            Snap(p, $"console-visit-invited-{mode}");
        }
        // The billing pop-up (2026-10-05), with a long subject and the longest stage name.
        using (var bp = new BillingPopup(tech) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) })
        {
            bp.Arrived(new BillingArrivalInfo { TicketId = 1, Number = 12345, Subject = "Office Wi-Fi drops every hour on the second floor near the boardroom",
                Stage = "Do not invoice — ticket to be closed", By = "Victoria Montgomery-Smith" }, null);
            Pump();
            LabelsFit(bp, "billing pop-up");
            foreach (var b in All(bp).OfType<RoundButton>())
                Check($"billing pop-up: \"{b.Text}\" fits its button and is inside the window",
                    TextRenderer.MeasureText(b.Text, b.Font).Width + 16 <= b.Width && b.Left >= 0 && b.Right <= b.Parent!.ClientSize.Width, $"{b.Left}..{b.Right} of {b.Parent!.ClientSize.Width}");
            Snap(bp, $"console-billing-{mode}");
        }
    }

    // ---- outside help (2026-10-06, roadmap 3.9): the tray app's code window, both faces ---------------
    private static void OutsiderWindows(Theme cust, Branding brand, ThemeMode mode)
    {
        Console.WriteLine("-- outside help: the tray app's code window");
        using var f = new RemoteSupport.Client.OutsiderCodeDialog(cust, brand, "Annabel Featherstonehaugh") { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        Show(f);
        void Face(string where)
        {
            // The face showing: one of the two panels directly on the card. The hidden one is never laid out.
            var face = All(f).OfType<Card>().First().Controls.OfType<Panel>().First(p => p.Visible);
            LabelsFit(face, where);
            Stacked(face, where);
            foreach (var b in All(face).OfType<RoundButton>())
                Check($"{where}: \"{b.Text}\" fits its button and is inside the window",
                    TextRenderer.MeasureText(b.Text, b.Font).Width + 16 <= b.Width && b.Left >= 0 && b.Right <= b.Parent!.ClientSize.Width, $"{b.Left}..{b.Right} of {b.Parent!.ClientSize.Width}");
        }
        Face("code window, the warning");
        Snap(f, $"tray-outsider-ask-{mode}");
        f.ShowCode(new OutsiderCodeMsg { Code = "012345678", For = "Smith & Co Chartered Accountants (Pty) Ltd", ExpiresUtc = DateTime.UtcNow.AddMinutes(10) });
        Pump();
        Face("code window, the code");
        Check("the code reads in threes", All(f).OfType<Label>().Any(l => l.Visible && l.Text == "012 345 678"));
        // Send it, as a technician's code can be (§155): Copy, WhatsApp and Email, ready the moment the code is.
        Check("the code window offers Copy the message, WhatsApp and Email, ready", new[] { "Copy the message", "WhatsApp", "Email" }
            .All(t => All(f).OfType<RoundButton>().Any(b => b.Visible && b.Enabled && b.Text == t)));
        Snap(f, $"tray-outsider-code-{mode}");
    }

    /// <summary>Nothing in the card overlaps the next thing, and the card holds all of it.</summary>
    private static void Stacked(Control card, string where)
    {
        var parts = card.Controls.Cast<Control>().Where(c => c.Visible && c.Height > 0).OrderBy(c => c.Top).ToList();
        bool overlap = parts.Zip(parts.Skip(1)).Any(p => p.First.Bottom > p.Second.Top);
        Check($"{where}: no part overlaps the next", !overlap, string.Join(", ", parts.Select(p => $"{Name(p)}@{p.Top}-{p.Bottom}")));
        Check($"{where}: the card holds all of it", parts.Max(p => p.Bottom) <= card.ClientSize.Height - card.Padding.Bottom + 1,
              $"lowest at {parts.Max(p => p.Bottom)}, card ends at {card.ClientSize.Height - card.Padding.Bottom}");
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary>Every visible label holds its own text at this scaling, wrapped to its width.</summary>
    private static void LabelsFit(Control root, string where)
    {
        foreach (var l in All(root).OfType<Label>().Where(l => l.Visible && l.Text.Length > 0))
        {
            int w = Math.Max(1, l.ClientSize.Width - l.Padding.Horizontal);
            var need = TextRenderer.MeasureText(l.Text, l.Font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak);
            int have = l.ClientSize.Height - l.Padding.Vertical;
            Check($"{where}: \"{Short(l.Text)}\" fits", need.Height <= have, $"needs {need.Height} px, has {have}");
        }
    }

    private static IEnumerable<string> Texts(Control root) => All(root).OfType<Label>().Select(l => l.Text);

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control c in root.Controls) { yield return c; foreach (var d in All(c)) yield return d; }
    }

    private static void Show(Form f) { f.Show(); Pump(); }
    private static void Pump() { for (int i = 0; i < 12; i++) { Application.DoEvents(); Thread.Sleep(25); } }

    private static void Snap(Form f, string name)
    {
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(Path.Combine(_out, name + ".png"), ImageFormat.Png);
    }

    private static string Name(Control c) => c switch { LinkLabel l => "link", Label l => $"'{Short(l.Text)}'", RoundButton b => $"[{b.Text}]", _ => c.GetType().Name };
    private static string Short(string s) => s.Length > 28 ? s[..28] + "…" : s;
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double ch(int v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
        }
        double x = L(a), y = L(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
