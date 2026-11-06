using System.Drawing.Imaging;
using System.Reflection;
using RemoteSupport.Client;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// The two ends of asking for help: what the customer must say, and what the technician reads.
//
// Stage 3.5.7. The customer's dialog asked "What do you need help with? (optional)", so calls
// could and did ring with nothing attached — and the alert on the technician's side had a "No
// description given." line to prove it. A call with no reason is a call answered blind: whoever
// takes it opens by asking the question the customer was already asked once, of somebody who has
// been waiting.
//
// It also covers stage 3.5.6, the customer's tray menu, which is two items now.
//
// WHY A HARNESS. Both halves are the kind of thing that passes a glance and fails in use. A
// required field that is not actually required looks identical to one that is until somebody
// leaves it blank, and the order of five labels on an alert is exactly the sort of change a later
// tidy-up reverses without noticing — which would put the one fact that decides how you answer a
// call back underneath the one that does not.
//
// No server, no session, no customer machine.
//
//   dotnet run -c Release --project tools/callprobe/callprobe.csproj [-- <out-dir>]
// ---------------------------------------------------------------------------------------------

internal static class CallProbe
{
    private static int _pass, _fail;

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "call");
        Directory.CreateDirectory(outDir);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var brand = Branding.Load();
        var customerTheme = Theme.FromBranding(brand, AppSurface.Customer);
        var techTheme = Theme.FromBranding(brand, AppSurface.Technician);

        // ---- 3.5.7, the customer's end -------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("== the request dialog: a call must say what is wrong ==");
        var dlg = new RequestDialog("Lenovo", customerTheme, brand);
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new Point(40, 40);
        dlg.Show();
        Settle();

        var label = Leaves(dlg).OfType<Label>().FirstOrDefault(l => l.Text.StartsWith("What do you need help with", StringComparison.Ordinal));
        Check("the dialog still asks what is wrong", label != null, label?.Text ?? "the question is gone");
        Check("and no longer calls it optional", label != null && !label.Text.Contains("optional", StringComparison.OrdinalIgnoreCase),
              label?.Text ?? "");

        var ok = Leaves(dlg).OfType<Button>().FirstOrDefault(b => b.Text.Contains("Request", StringComparison.Ordinal));
        Check("the Request button is there to press", ok != null);

        // Empty: it must refuse, stay open, and say why.
        SetIssue(dlg, "");
        ok!.PerformClick();
        Settle();
        Check("an empty subject is refused", dlg.Visible && dlg.DialogResult != DialogResult.OK,
              "the dialog stays open");
        var hint = Leaves(dlg).OfType<Label>().FirstOrDefault(l => l.Text.Contains("Please say in a few words", StringComparison.Ordinal));
        Check("and the dialog says why, in itself rather than in a second window", hint is { Text.Length: > 0 },
              hint?.Text ?? "nothing was said");
        // The refusal is worth a picture as well as an assertion: a hint that exists but is drawn
        // off the bottom of the card, or in a colour nobody notices, passes the assertion above.
        if (hint != null)
        {
            Check("and the complaint is on screen, inside the card", hint.Visible && hint.Bottom <= dlg.ClientSize.Height,
                  $"{hint.Bounds} in a dialog {dlg.ClientSize.Height} px tall");
            // And it fits the room it has, sideways and down. The first version of this sentence
            // ran off the right-hand edge of the card and stopped at "it is the first", which is
            // a complaint that needs a complaint of its own.
            var need = TextRenderer.MeasureText(hint.Text, hint.Font, new Size(hint.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak);
            Check("and the whole sentence fits, rather than stopping mid-way",
                  need.Height <= hint.Height && need.Width <= hint.ClientSize.Width,
                  $"needs {need.Width}x{need.Height}, has {hint.ClientSize.Width}x{hint.Height}");
        }
        Shoot(dlg, outDir, "request-dialog-refused");

        // A keystroke is not a description. The floor exists to stop "." getting past the
        // question, not to grade the answer — anything stronger would obstruct somebody in a
        // hurry writing something true and short.
        SetIssue(dlg, ".");
        ok.PerformClick();
        Settle();
        Check("a single character is refused too", dlg.Visible && dlg.DialogResult != DialogResult.OK, "\".\"");

        // And the complaint must clear itself the moment they start answering it.
        SetIssue(dlg, "Outlook will not open");
        Settle();
        var hintAfter = Leaves(dlg).OfType<Label>().FirstOrDefault(l => l.Text.Contains("Please say in a few words", StringComparison.Ordinal));
        Check("typing clears the complaint", hintAfter == null || hintAfter.Text.Length == 0,
              "the answer to it is what they are doing");

        Shoot(dlg, outDir, "request-dialog");
        ok.PerformClick();
        Settle();
        Check("a real subject is accepted — with the description left empty, which is optional", dlg.DialogResult == DialogResult.OK && dlg.Details.Length == 0, dlg.DialogResult.ToString());
        Check("and it is what comes out of the dialog as the subject", dlg.Subject == "Outlook will not open", dlg.Subject);
        dlg.Dispose();

        // ---- 2026-10-09: a subject, a description, and a picture of the screen ----------------
        // Clint: the one box became the ticket's subject whole, and a long one "creates a mess of the tickets";
        // and "a screenshot option when a client requests support would be a great feature".
        Console.WriteLine();
        Console.WriteLine("== the request: a subject, a description, a picture ==");
        using (var shot = ScreenSnap.Take(null))
        {
            Check("a picture of this screen can be taken", shot != null && shot.Jpeg.Length > 1000, shot == null ? "none" : $"{shot.Jpeg.Length / 1024} KB");
            if (shot != null)
            {
                using var back = Image.FromStream(new MemoryStream(shot.Jpeg));
                Check("...it is a JPEG that opens, no wider than 1920", back.RawFormat.Guid == ImageFormat.Jpeg.Guid && back.Width <= ScreenSnap.MaxWidth && back.Width > 100,
                    $"{back.Width}x{back.Height} from a {SystemInformation.VirtualScreen.Width}x{SystemInformation.VirtualScreen.Height} desktop");
                Check("...small enough to travel with the request", shot.Jpeg.Length < 3 * 1024 * 1024, $"{shot.Jpeg.Length / 1024} KB");
            }
            var d2 = new RequestDialog("Lenovo", customerTheme, brand, shot) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            d2.Show(); Settle();
            var subjectBox = Leaves(d2).OfType<TextBox>().FirstOrDefault(t => !t.Multiline && t.MaxLength == SupportRequest.SubjectMax);
            Check("the subject is one line, at most 80 characters", subjectBox != null, subjectBox == null ? "no such box" : $"MaxLength {subjectBox.MaxLength}");
            var detailsBox = Leaves(d2).OfType<TextBox>().FirstOrDefault(t => t.Multiline);
            var detailsLabel = Leaves(d2).OfType<Label>().FirstOrDefault(l => l.Text.Contains("optional", StringComparison.OrdinalIgnoreCase));
            Check("the description is its own box, and says it is optional", detailsBox != null && detailsLabel != null, detailsLabel?.Text ?? "");
            var tick = Leaves(d2).OfType<CheckBox>().FirstOrDefault(c => c.Text.Contains("picture of my screen"));
            var pic = Leaves(d2).OfType<PictureBox>().FirstOrDefault();
            Check("the picture is offered TICKED, with the picture itself to look at (Clint's answer)",
                tick is { Checked: true } && pic is { Visible: true, Image: not null } && d2.IncludeScreenshot, $"tick {tick?.Checked}, picture {pic?.Visible}");
            var card = Leaves(d2).OfType<Card>().FirstOrDefault();
            if (card != null)
                foreach (var c in Leaves(card).Where(c => c.Visible && c.Parent == card))
                    Check($"'{Name(c)}' sits inside the request window", c.Top >= 0 && c.Bottom <= card.ClientSize.Height + 1 && c.Height > 0,
                        $"{c.Top}..{c.Bottom} of {card.ClientSize.Height}");
            if (tick?.Text is { } tt)
            {
                var need = TextRenderer.MeasureText(tt, tick.Font);
                Check("the tick's words fit, above the picture rather than under it", tick.Parent != null && tick.Right <= tick.Parent.ClientSize.Width && need.Width <= tick.Width + 2 && (pic == null || tick.Bottom <= pic.Top), $"{tick.Bounds} above {pic?.Bounds}, needs {need.Width}");
            }
            Shoot(d2, outDir, "request-dialog-with-picture");
            if (tick != null) { tick.Checked = false; Settle(); }
            Check("unticked, the picture is not sent and not shown", !d2.IncludeScreenshot && pic is { Visible: false });
            Shoot(d2, outDir, "request-dialog-picture-unticked");
            SetIssue(d2, "Sage will not open");
            if (detailsBox != null) detailsBox.Text = "It says the licence server cannot be found.\r\nSince this morning.";
            Leaves(d2).OfType<Button>().First(b => b.Text.Contains("Request", StringComparison.Ordinal)).PerformClick(); Settle();
            Check("the subject and the description come out separately",
                d2.DialogResult == DialogResult.OK && d2.Subject == "Sage will not open" && d2.Details.StartsWith("It says the licence server"), $"{d2.Subject} / {d2.Details}");
            d2.Dispose();
        }
        var noPic = new RequestDialog("Lenovo", customerTheme, brand, null) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        noPic.Show(); Settle();
        Check("with no picture to offer (a locked screen), there is no tick, and nothing is sent",
            !Leaves(noPic).OfType<CheckBox>().Any() && !noPic.IncludeScreenshot);
        Shoot(noPic, outDir, "request-dialog-no-picture");
        noPic.Dispose();

        // ---- 2026-10-13: the name remembered, and a picture of what is actually open -----------------------------
        // Clint: "the app should remember the client name ... It should default to the last name entered", and "it
        // does it of the desktop only. If a client has explorer open with a folder list for example, it does not
        // screenshot that." The capture takes an open Explorer window whole (measured); the window was not on the
        // screen — the shortcut is on the desktop, and getting to it minimises everything.
        Console.WriteLine();
        Console.WriteLine("== the name: the last one given comes back (LastContact) ==");
        var nameDir = Path.Combine(outDir, "contact");
        if (Directory.Exists(nameDir)) Directory.Delete(nameDir, true);
        LastContact.FilePath = Path.Combine(nameDir, "contact.json");
        Check("with nothing remembered, it is the Windows username as before", LastContact.Load("winuser") == "winuser");
        LastContact.Remember("  Sue at reception ");
        Check("a name sent with a request comes back, trimmed, after a restart", LastContact.Load("winuser") == "Sue at reception", LastContact.Load("winuser"));
        LastContact.Remember("   ");
        Check("a blank name is not remembered over it", LastContact.Load("winuser") == "Sue at reception");
        File.WriteAllText(LastContact.FilePath, "{not json");
        Check("a damaged file is the old default, never an error", LastContact.Load("winuser") == "winuser");
        LastContact.Remember("Sue at reception");
        var named = new RequestDialog(LastContact.Load("winuser"), customerTheme, brand, null);
        named.Show(); Settle();
        var nameBox = Leaves(named).OfType<TextBox>().FirstOrDefault(b => !b.Multiline && b.MaxLength != SupportRequest.SubjectMax && b.MaxLength != GivenPerson.EmailMax);
        Check("the request window opens with it filled in", nameBox?.Text == "Sue at reception", nameBox?.Text ?? "no name box");
        named.Dispose();
        var mainSrc = File.ReadAllText(Path.Combine(RepoRoot(), "src", "RemoteSupport.Client", "MainForm.cs"));
        Check("the tray app starts from it and remembers what is sent",
            mainSrc.Contains("LastContact.Load(GivenPerson.Installed()?.Name is { Length: > 0 } n ? n : Environment.UserName)")
            && mainSrc.Contains("LastContact.Remember(dlg.ContactName, dlg.ContactEmail)")
            && mainSrc.Contains("dlg.Screenshot, dlg.ContactEmail)"));

        // ---- 2026-10-23: the email beside the name, and the name it was installed for ------------------------------
        Console.WriteLine();
        Console.WriteLine("== the email: optional, filled in, and only ever beside its own name ==");
        var jane = GivenPerson.Make("Jane", "Smith", "jane@acme.co.za", null);
        File.Delete(LastContact.FilePath);
        Check("before any request: the name it was installed for, ahead of Windows'", LastContact.Load(jane.Name) == "Jane Smith");
        Check("...and its email with it", LastContact.EmailFor("Jane Smith", jane) == "jane@acme.co.za");
        LastContact.Remember("Mary Jones", "mary@acme.co.za");
        Check("a request sent with an email: both come back next time", LastContact.Load(jane.Name) == "Mary Jones" && LastContact.EmailFor("Mary Jones", jane) == "mary@acme.co.za");
        Check("...and Jane's email is never offered beside somebody else's name", LastContact.EmailFor("Peter Pan", jane) == "");
        LastContact.Remember("Jane Smith", "");
        Check("a request with the email left blank: the install's, for the install's name", LastContact.EmailFor("Jane Smith", jane) == "jane@acme.co.za");
        var withMail = new RequestDialog("Jane Smith", customerTheme, brand, null, "jane@acme.co.za");
        withMail.Show(); Settle();
        var mailBox = Leaves(withMail).OfType<TextBox>().FirstOrDefault(b => b.MaxLength == GivenPerson.EmailMax);
        var mailLabel = Leaves(withMail).OfType<Label>().FirstOrDefault(l => l.Text == "Your email (optional)");
        Check("the request window has the email, filled in, under the name", mailBox?.Text == "jane@acme.co.za" && mailLabel != null
            && mailBox.Parent!.Top > Leaves(withMail).OfType<TextBox>().First(b => b.Text == "Jane Smith").Parent!.Top, mailBox?.Text ?? "no email box");
        SetIssue(withMail, "Printer offline");
        mailBox!.Text = "jane at acme";
        var send = Leaves(withMail).OfType<Button>().First(b => b.Text.Contains("Request", StringComparison.Ordinal));
        send.PerformClick(); Settle();
        var mailHint = Leaves(withMail).OfType<Label>().FirstOrDefault(l => l.Text.StartsWith("That email address does not look right", StringComparison.Ordinal));
        Check("half an address is refused, in the window", withMail.DialogResult != DialogResult.OK && mailHint != null, mailHint?.Text ?? "not refused");
        var mailNeed = mailHint == null ? Size.Empty : TextRenderer.MeasureText(mailHint.Text, mailHint.Font, new Size(mailHint.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak);
        Check("...and the whole sentence fits", mailHint != null && mailNeed.Height <= mailHint.Height && mailNeed.Width <= mailHint.ClientSize.Width,
            $"needs {mailNeed.Width}x{mailNeed.Height}, has {mailHint?.ClientSize.Width}x{mailHint?.Height}");
        Shoot(withMail, outDir, "request-dialog-email");
        mailBox.Text = "";
        send.PerformClick(); Settle();
        Check("...and a blank one is fine: it is optional", withMail.DialogResult == DialogResult.OK && withMail.ContactEmail == "");
        withMail.Dispose();

        Console.WriteLine();
        Console.WriteLine("== the picture: what is open, and taking it again ==");
        Console.WriteLine("        open on this screen now, as ScreenSnap counts it:");
        foreach (var w in ScreenSnap.ProgramsShowing().Take(12)) Console.WriteLine($"          {w}");
        // A desktop-only picture, made as Take makes one when every window is minimised — doing that for real here
        // would minimise the screen of whoever is running this.
        var ctor = typeof(ScreenSnap).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        using (var real = ScreenSnap.Take())
        {
            var desktopOnly = (ScreenSnap)ctor.Invoke(new object[] { real!.Jpeg, new Bitmap(real.Preview), true });
            var dDesk = new RequestDialog("Lenovo", customerTheme, brand, desktopOnly) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            dDesk.Show(); Settle();
            var note = Leaves(dDesk).OfType<Label>().FirstOrDefault(l => l.Text.StartsWith("Only your desktop", StringComparison.Ordinal));
            Check("a picture of only the desktop SAYS so beside it, and how to fix it", note is { Visible: true } && note.Text.Contains("take a new picture"), note?.Text ?? "not said");
            var again = Leaves(dDesk).OfType<Button>().FirstOrDefault(b => b.Text == "Take a new picture");
            var pic = Leaves(dDesk).OfType<PictureBox>().FirstOrDefault();
            Check("the button to take it again is there, beside the picture", again is { Visible: true } && pic != null && again.Left > pic.Right && again.Parent == pic.Parent,
                again == null ? "none" : $"{again.Bounds} beside {pic?.Bounds}");
            if (again != null && note != null)
            {
                var row = again.Parent!;
                Check("...the words fit above it, and it inside the row", note.Bottom <= again.Top && again.Right <= row.ClientSize.Width + 1 && again.Bottom <= row.ClientSize.Height + 1
                    && TextRenderer.MeasureText(note.Text, note.Font, new Size(note.Width, int.MaxValue), TextFormatFlags.WordBreak).Height <= note.Height
                    && TextRenderer.MeasureText(again.Text, again.Font).Width <= again.Width,
                    $"note {note.Bounds}, button {again.Bounds}, row {row.ClientSize}");
            }
            Shoot(dDesk, outDir, "request-dialog-desktop-only");

            // Take it again: while the bar is up, the request window must be out of the way, and the bar must be
            // the only thing of ours to see; then a new picture, which is what gets sent.
            double seenOpacity = -1; PictureBar? seenBar = null; string barWords = ""; bool barFits = false;
            var before = dDesk.Screenshot;
            var poll = new System.Windows.Forms.Timer { Interval = 100 };
            poll.Tick += (_, _) =>
            {
                var bar = Application.OpenForms.OfType<PictureBar>().FirstOrDefault();
                if (bar == null || !bar.Visible) return;
                poll.Stop();
                seenOpacity = dDesk.Opacity; seenBar = bar;
                var w = Leaves(bar).OfType<Label>().First();
                barWords = w.Text;
                barFits = TextRenderer.MeasureText(w.Text, w.Font, new Size(w.Width, int.MaxValue), TextFormatFlags.WordBreak).Height <= w.Height
                          && Leaves(bar).All(c => c.Right <= bar.ClientSize.Width && c.Bottom <= bar.ClientSize.Height);
                ShootScreen(bar.Bounds, outDir, "picture-bar");
                Leaves(bar).OfType<Button>().First(b => b.Text == "Take the picture").PerformClick();
            };
            poll.Start();
            again?.PerformClick();
            Settle();
            Check("pressing it puts the request window out of the way and shows the bar", seenBar != null && seenOpacity == 0,
                seenBar == null ? "no bar" : $"request window opacity {seenOpacity}");
            if (seenBar != null)
            {
                var wa = Screen.FromControl(dDesk).WorkingArea;
                Check("...the bar sits at the top of the screen, on top of everything, out of the taskbar",
                    seenBar.TopMost && !seenBar.ShowInTaskbar && seenBar.Top - wa.Top < 40 && Math.Abs(seenBar.Left + seenBar.Width / 2 - (wa.Left + wa.Width / 2)) < 4,
                    $"{seenBar.Bounds} on {wa}");
                Check("...and says what to do, all of it on show", barWords.StartsWith("Bring up what you want us to see") && barFits, barWords);
            }
            Check("taking it brings the request window back, with the NEW picture to send",
                dDesk.Opacity == 1 && dDesk.Visible && dDesk.Screenshot != null && !ReferenceEquals(dDesk.Screenshot, before),
                $"opacity {dDesk.Opacity}, {(dDesk.Screenshot == null ? 0 : dDesk.Screenshot.Length / 1024)} KB");
            var noteAfter = Leaves(dDesk).OfType<Label>().FirstOrDefault(l => l.Text.StartsWith("Taken just now", StringComparison.Ordinal) || l.Text.StartsWith("Only your desktop", StringComparison.Ordinal));
            Check("...and the words beside it describe the new picture (windows are open here)", noteAfter?.Text.StartsWith("Taken just now") == ScreenSnap.AnyProgramShowing(), noteAfter?.Text ?? "");

            // Cancel on the bar keeps the picture there was.
            var kept = dDesk.Screenshot;
            var poll2 = new System.Windows.Forms.Timer { Interval = 100 };
            poll2.Tick += (_, _) =>
            {
                var bar = Application.OpenForms.OfType<PictureBar>().FirstOrDefault();
                if (bar == null || !bar.Visible) return;
                poll2.Stop();
                Leaves(bar).OfType<Button>().First(b => b.Text == "Cancel").PerformClick();
            };
            poll2.Start();
            Leaves(dDesk).OfType<Button>().First(b => b.Text == "Take a new picture").PerformClick();
            Settle();
            Check("Cancel on the bar keeps the picture there was, and brings the window back", ReferenceEquals(dDesk.Screenshot, kept) && dDesk.Opacity == 1);

            var tick = Leaves(dDesk).OfType<CheckBox>().First();
            tick.Checked = false; Settle();
            Check("unticked, nothing is sent and there is no picture to retake", dDesk.Screenshot == null && !Leaves(dDesk).OfType<Button>().First(b => b.Text == "Take a new picture").Visible);
            dDesk.Dispose();
            desktopOnly.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine("== an older tray app's one box, tidied by the hub (SupportRequest.Words) ==");
        var (s1, d1) = SupportRequest.Words("Outlook will not open", "");
        Check("a short subject stays as it is, with no description", s1 == "Outlook will not open" && d1 == "", $"{s1} / {d1}");
        var essay = "Every morning since the update the whole machine freezes for about ten minutes when I open the accounts package";
        var (s2, d2w) = SupportRequest.Words(essay, "");
        Check("one long box: the subject is cut at 80 and SAYS so", s2.Length <= SupportRequest.SubjectMax && s2.Length > 60 && s2.EndsWith("…"), s2);
        Check("...and nothing is lost — the whole of it is the description", d2w == essay);
        var (s3, d3) = SupportRequest.Words("Printer\r\nIt jams on every page", "");
        Check("several lines: the first is the subject, all of it the description", s3 == "Printer" && d3 == "Printer\nIt jams on every page", $"{s3} / {d3.Replace("\n", "⏎")}");
        var (s4, d4) = SupportRequest.Words("Printer jams", "On every page");
        Check("a subject and a description from the new tray app pass through untouched", s4 == "Printer jams" && d4 == "On every page");

        // ---- 3.5.7, the technician's end -----------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("== the incoming call alert: the reason is read first ==");
        var popup = new IncomingCallPopup(techTheme, brand);
        popup.ShowCall(new CallInfo
        {
            CallId = "probe",
            ContactName = "Lenovo",
            MachineName = "DESKTOP-OPHQIDH",
            Issue = "Sage will not open — it says the licence server cannot be found",
            Status = CallStatus.Ringing,
            Created = DateTimeOffset.UtcNow.AddSeconds(-12)
        }, alsoWaiting: 1, near: null);
        Settle();

        var issue = Field<Label>(popup, "_issue");
        // Printed, not asserted. Every wrong answer in building this came from a control whose
        // real geometry was not what the code assumed — a label stuck at its birth width of 100,
        // a box sized in pixels against a font sized in points on a 150% display. The numbers
        // being on screen is what turned each of those from a guess into a measurement.
        if (issue?.Parent is { } alertBody)
        {
            Console.WriteLine($"        layout: body {alertBody.DisplayRectangle}");
            foreach (Control c in alertBody.Controls)
                Console.WriteLine($"        layout:   {c.GetType().Name,-6} {c.Bounds}");
        }
        var who = Field<Label>(popup, "_who");
        var machine = Field<Label>(popup, "_machine");
        Check("the alert carries the reason", issue != null && issue.Text.StartsWith("Sage will not open", StringComparison.Ordinal), issue?.Text ?? "");
        Check("it is the topmost thing in the body", issue != null && who != null && machine != null
              && issue.Top < who.Top && issue.Top < machine.Top,
              issue == null ? "" : $"issue at y={issue.Top}, name at y={who.Top}, machine at y={machine.Top}");
        Check("and the biggest text on it — bigger than the caller's name",
              issue != null && who != null && issue.Font.Size > who.Font.Size,
              issue == null || who == null ? "" : $"{issue.Font.Size:0.#} pt against {who.Font.Size:0.#} pt");
        Check("the caller and their machine are still there to read", who is { Text: "Lenovo" } && machine is { Text: "DESKTOP-OPHQIDH" },
              $"{who?.Text} / {machine?.Text}");

        // Everything has to fit. Five labels and a button row in a fixed 480x320 alert is exactly
        // the arithmetic that silently clips the top item, and the top item is now the reason.
        var body = issue?.Parent;
        if (body != null)
            foreach (var c in body.Controls.Cast<Control>())
                Check($"'{Name(c)}' fits inside the alert",
                      c.Top >= -1 && c.Bottom <= body.ClientSize.Height + 1,
                      $"{c.Top}..{c.Bottom} of {body.ClientSize.Height}");

        // The whole sentence, on two lines. A Label with AutoEllipsis lays out on ONE line and
        // truncates, which turned this into "Sage will not open — it says ..." and threw away
        // the half that says what the technician will be doing. Asserted on the SPECIFIC half,
        // because that is the part that was being lost.
        Check("a normal sentence is shown whole, not truncated onto one line",
              issue != null && issue.Text.Contains("licence server", StringComparison.Ordinal),
              issue?.Text ?? "");

        Shoot(popup, outDir, "incoming-call");

        // And one too long for even two lines is cut at a word and SAYS it was cut. Text that
        // stops mid-word reads as a broken alert rather than as a long sentence.
        popup.ShowCall(new CallInfo
        {
            CallId = "probe-long", ContactName = "Lenovo", MachineName = "DESKTOP-OPHQIDH",
            Issue = "Every morning since the update the whole machine freezes for about ten minutes "
                  + "when I open the accounts package, and then the printer stops working until I "
                  + "restart it, and today it also lost the network drive entirely",
            Status = CallStatus.Ringing, Created = DateTimeOffset.UtcNow
        }, alsoWaiting: 0, near: null);
        Settle();
        Check("a very long description is cut at a word and says so",
              issue != null && issue.Text.EndsWith("…", StringComparison.Ordinal) && issue.Text.Length > 30,
              issue?.Text ?? "");
        Check("and what is shown still fits the room it has",
              issue != null && TextRenderer.MeasureText(issue.Text, issue.Font, new Size(issue.ClientSize.Width, int.MaxValue),
                                                        TextFormatFlags.WordBreak).Height <= issue.Height,
              issue == null ? "" : $"{TextRenderer.MeasureText(issue.Text, issue.Font, new Size(issue.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak).Height} px of {issue.Height}");
        Shoot(popup, outDir, "incoming-call-long");

        // An OLD client app cannot send a description, and the alert must say so rather than
        // going blank — the fallback cannot be deleted just because the current client can no
        // longer produce it.
        popup.ShowCall(new CallInfo
        {
            CallId = "probe-old", ContactName = "Lenovo", MachineName = "OLD-PC",
            Issue = "", Status = CallStatus.Ringing, Created = DateTimeOffset.UtcNow
        }, alsoWaiting: 0, near: null);
        Settle();
        Check("a call with no description still says something useful",
              issue is { Text.Length: > 10 } && issue.Text.Contains("ask what is wrong", StringComparison.OrdinalIgnoreCase),
              issue?.Text ?? "it went blank");
        Shoot(popup, outDir, "incoming-call-no-description");
        popup.HideCall();
        popup.Dispose();

        Console.WriteLine();
        Console.WriteLine("== 3.5.6: the customer's tray menu ==");
        Console.WriteLine("   What a customer wants, the one setting, and then the way out:");
        Console.WriteLine("     Request Support  /  Chat with a Technician  /  Allow unattended access  —  Exit");
        Console.WriteLine("   A NotifyIcon's menu cannot be measured without a running client app and a");
        Console.WriteLine("   real tray, so this is a manual check rather than a harness pretending:");
        Console.WriteLine("     - right-click the tray icon: Request Support, Chat with a Technician,");
        Console.WriteLine("       Allow unattended access, a separator, and Exit. No 'Open' — the");
        Console.WriteLine("       double-click does that;");
        Console.WriteLine("     - Request Support from the tray must open ONE window: the request dialog,");
        Console.WriteLine("       and NOT the tray window behind it (2026-09-20);");
        Console.WriteLine("     - while it is ringing, that first item must read 'Cancel request' — there");
        Console.WriteLine("       may be no window on screen to cancel it from;");
        Console.WriteLine("     - Chat with a Technician must open the CHAT WINDOW with the caret in the");
        Console.WriteLine("       reply box, not the tray window for you to find the chat box inside;");
        Console.WriteLine("     - double-clicking the icon must still open the tray window.");

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        Console.WriteLine($"screenshots in {outDir}");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>Types into the dialog's SUBJECT box (2026-10-09; it was the one description box) the way a person
    /// would — through the control, so the TextChanged the dialog listens to actually fires.</summary>
    private static void SetIssue(RequestDialog dlg, string text)
    {
        var box = Leaves(dlg).OfType<TextBox>().FirstOrDefault(t => !t.Multiline && t.MaxLength == SupportRequest.SubjectMax);
        if (box != null) box.Text = text;
        dlg.DialogResult = DialogResult.None;
        Settle();
    }

    private static string Name(Control c) =>
        string.IsNullOrWhiteSpace(c.Text) ? c.GetType().Name : c.Text.Length > 28 ? c.Text[..28] : c.Text;

    private static IEnumerable<Control> Leaves(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var g in Leaves(c)) yield return g;
        }
    }

    private static void Settle() { for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(25); } }

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

    /// <summary>A form that is TopMost over other programs is worth a picture of the SCREEN, not of itself.</summary>
    private static void ShootScreen(Rectangle r, string outDir, string name)
    {
        try
        {
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            var p = Path.Combine(outDir, name + ".png");
            bmp.Save(p, ImageFormat.Png);
            Console.WriteLine($"        screenshot: {p}");
        }
        catch (Exception ex) { Console.WriteLine($"        (could not screenshot {name}: {ex.GetType().Name})"); }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "RemoteSupport.Client"))) d = d.Parent;
        return d?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static T? Field<T>(object o, string name) where T : class =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o) as T;
}
