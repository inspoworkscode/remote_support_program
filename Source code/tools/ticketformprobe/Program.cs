using System.Reflection;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// ticketformprobe - does the New ticket dialog fit, and does every category's fields actually
// appear on screen?
//
// It exists because of a specific, embarrassing failure. The dialog was one column of absolutely
// positioned controls stepped along with pixel constants, in a window fixed at 640x720. On a 150%
// display those constants were wrong by about ten pixels a row, and by the time the layout reached
// the category panel it was PAST THE BOTTOM OF THE WINDOW. Choosing Workshop appeared to do
// nothing, so "it did not ask for photos" was reported as a missing feature when the feature was
// there and simply off-screen.
//
// Nothing in the test suite could have caught that: photogate proved the RULE, and the rule was
// right. Only geometry catches geometry.
//
// So: open the real dialog, switch it through every category, and check that every control
// that is supposed to be visible is inside the window and not covered by anything else. Screenshots
// on every run, because the numbers say it fits and only a picture says it looks like anything.
//
//   dotnet run -c Release --project tools/ticketformprobe/ticketformprobe.csproj
// ---------------------------------------------------------------------------------------------

internal static class TicketFormProbe
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
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(outDir);

        var brand = Branding.Load();
        var theme = Theme.FromBranding(brand, AppSurface.Technician);
        // A port nothing is listening on: the client search never answers, which is fine and is
        // not what this measures.
        var link = new TechLink("ws://127.0.0.1:59999");
        // Signed in, with a team, as a real sign-in would say: the "people on this job" tick boxes
        // (2026-09-28) are one per person on the team, and with no team there is nothing to draw.
        typeof(TechLink).GetProperty("DisplayName")!.SetValue(link, "Sue Tech");
        typeof(TechLink).GetProperty("Roster")!.SetValue(link, new List<string> { "Ann Tech", "Bob Tech", "Sue Tech" });

        Console.WriteLine("ticketformprobe - does the New ticket dialog fit?");
        Console.WriteLine($"Screen        : {Screen.PrimaryScreen?.WorkingArea}");
        Console.WriteLine($"One line of body text: {TextRenderer.MeasureText("Ag", theme.Body).Height} px");
        Console.WriteLine();

        using var dlg = new NewTicketDialog(theme, link);
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new Point(0, 0);
        dlg.Show();
        Settle();

        var work = Screen.FromControl(dlg).WorkingArea;
        Check("the dialog fits the working area",
            dlg.Height <= work.Height && dlg.Width <= work.Width,
            $"{dlg.Width}x{dlg.Height} in {work.Width}x{work.Height}");

        // Wider than it is tall, or at least not a tall thin ribbon. The old one was 640x720 with
        // 1180 px of content inside it.
        Check("it is a landscape dialog rather than a scrolling ribbon",
            dlg.Width > dlg.Height * 0.9, $"{dlg.Width}x{dlg.Height}");

        var category = Field<ComboBox>(dlg, "_category");
        if (category == null) { Check("found the category picker", false); return Done(); }

        // The fields that must be reachable for each category, by field name on the dialog.
        var always = new[] { "_clientSearch", "_subject", "_description", "_priority", "_people" };
        // Keyed by the STORED value, not by the position in the picker. Since General was added
        // the two are different — the combo is drawn in TicketSummary.CategoryOrder with General
        // first — and a probe that assumed they were the same would switch the dialog to one
        // category and then check another one's fields.
        var perCategory = new (TicketCategoryKind Cat, string Name, string[] Fields)[]
        {
            (TicketCategoryKind.General,       "General",        Array.Empty<string>()),
            (TicketCategoryKind.RemoteSupport, "Remote support", Array.Empty<string>()),
            (TicketCategoryKind.WalkIn,        "Walk in",        Array.Empty<string>()),
            (TicketCategoryKind.Workshop,      "Workshop",       new[] { "_make", "_model", "_serial", "_accessories", "_pwProtected", "_photoLabel", "_photoBtn" }),
            (TicketCategoryKind.CallOut,       "Call out",       new[] { "_siteAddress", "_hasAppt", "_apptDate", "_apptTime" }),
            // A schedule set up once (2026-10-07): its type, how often, and — weekly by default — the days.
            (TicketCategoryKind.Recurring,     "Recurring task", new[] { "_kind", "_repeats", "_weekPanel" }),
        };

        // Every category is offered, once, and the picker opens on the default.
        Check("the picker offers every category", category.Items.Count == perCategory.Length,
            $"{category.Items.Count} of {perCategory.Length}");
        Check("it opens on General, which is the default",
            category.SelectedItem as string == "General", category.SelectedItem as string ?? "nothing");
        Check("General is offered first", category.Items.Count > 0 && (category.Items[0] as string) == "General",
            category.Items.Count > 0 ? category.Items[0] as string : "nothing");

        // The job type as coloured buttons (2026-10-09): the wrong one had been chosen from the dropdown.
        Console.WriteLine();
        Console.WriteLine("== the job type, as coloured buttons ==");
        var kinds = Field<List<RoundButton>>(dlg, "_kinds") ?? new();
        Check("the dropdown is hidden — the buttons are what is seen", !category.Visible);
        Check("a button for every category, in the order offered, each visible",
            kinds.Count == perCategory.Length && kinds.All(k => k.Visible)
            && kinds.Select(k => (string)k.Tag!).SequenceEqual(TicketSummary.CategoryOrder.Select(v => TicketSummary.CategoryNames[v])),
            string.Join(", ", kinds.Select(k => k.Text)));
        Check("each in its own category's colour (TicketSummary.CategoryColors), no two the same",
            kinds.Select((k, i) => k.Tint == ColorTranslator.FromHtml(TicketSummary.CategoryColors[TicketSummary.CategoryOrder[i]].Fill)).All(x => x)
            && kinds.Select(k => k.Tint).Distinct().Count() == kinds.Count);
        Check("General is chosen to begin with (filled), and only General", kinds[0].Chosen && kinds.Skip(1).All(k => !k.Chosen));
        Check("every button's words fit inside it", kinds.All(k => TextRenderer.MeasureText(k.Text, k.Font).Width + 16 <= k.Width), string.Join(", ", kinds.Select(k => $"{k.Text} {TextRenderer.MeasureText(k.Text, k.Font).Width}/{k.Width}")));
        Check("all on ONE row across the top — a second row pushed the window past the screen", kinds.Select(k => k.Top).Distinct().Count() == 1, string.Join(", ", kinds.Select(k => k.Top)));
        var workshopBtn = kinds.First(k => (string)k.Tag! == "Workshop");
        workshopBtn.PerformClick(); Settle();
        Check("pressing Workshop chooses Workshop — the dialog reads it, and only that button is filled",
            Invoke<TicketCategoryKind>(dlg, "Chosen") == TicketCategoryKind.Workshop && workshopBtn.Chosen && kinds.Count(k => k.Chosen) == 1);
        Shot(dlg, outDir, "kinds-workshop");
        kinds[0].PerformClick(); Settle();
        Check("pressing General goes back", Invoke<TicketCategoryKind>(dlg, "Chosen") == TicketCategoryKind.General && kinds[0].Chosen && !workshopBtn.Chosen);

        foreach (var (cat, name, fields) in perCategory)
        {
            Console.WriteLine();
            Console.WriteLine($"== {name} ==");
            category.SelectedIndex = Array.IndexOf(TicketSummary.CategoryOrder, (int)cat);
            Settle();

            // The dialog must agree about WHICH category is showing. This is the fault the
            // ordering could cause and nothing else here would notice: the right fields on
            // screen under the wrong heading, filed under the wrong category on OK.
            var chosen = Invoke<TicketCategoryKind>(dlg, "Chosen");
            Check($"the dialog reads this as {name}", chosen == cat, chosen.ToString());

            foreach (var f in always.Concat(fields))
            {
                var c = Field<Control>(dlg, f);
                if (c == null) { Check($"{f} exists", false); continue; }

                // Visible means visible ALL THE WAY UP. A control on a hidden panel reports
                // Visible = true for itself, which is exactly how the category panel looked fine
                // in code while being invisible on screen.
                bool shown = c.Visible && Chain(c, dlg);
                if (!shown) { Check($"{f} is on screen", false, "hidden, or on a hidden parent"); continue; }

                var r = dlg.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
                bool inside = r.Top >= 0 && r.Left >= 0 && r.Right <= dlg.ClientSize.Width + 1 && r.Bottom <= dlg.ClientSize.Height + 1;
                Check($"{f} is inside the window", inside, inside ? $"{r}" : $"{r} against a client area of {dlg.ClientSize.Width}x{dlg.ClientSize.Height}");
            }

            // Nothing from another category is showing at the same time.
            foreach (var (otherCat, otherName, otherFields) in perCategory)
            {
                if (otherCat == cat || otherFields.Length == 0) continue;
                var stray = otherFields.Select(f => Field<Control>(dlg, f))
                    .Where(c => c != null && c.Visible && Chain(c!, dlg)).ToList();
                Check($"no {otherName.ToLower()} fields are showing", stray.Count == 0, $"{stray.Count} showing");
            }

            Shot(dlg, outDir, name.Replace(' ', '-').ToLower());
        }

        // Several people from the start (2026-09-28): a box each, the person booking it in ticked.
        Console.WriteLine();
        Console.WriteLine("== people on this job ==");
        var people = Field<FlowLayoutPanel>(dlg, "_people");
        var boxes = people?.Controls.OfType<CheckBox>().ToList() ?? new();
        Check("a tick box for everybody on the team", boxes.Select(b => b.Text).SequenceEqual(new[] { "Ann Tech", "Bob Tech", "Sue Tech" }),
            string.Join(", ", boxes.Select(b => b.Text)));
        Check("the person booking it in is ticked, nobody else", boxes.Where(b => b.Checked).Select(b => b.Text).SequenceEqual(new[] { "Sue Tech" }));
        foreach (var b in boxes) b.Checked = b.Text != "Ann Tech";
        Check("ticking two sends both, as the server stores them", Invoke<string>(dlg, "ChosenPeople") == "Bob Tech, Sue Tech", Invoke<string>(dlg, "ChosenPeople"));
        foreach (var b in boxes) b.Checked = false;
        Check("ticking nobody sends nobody", Invoke<string>(dlg, "ChosenPeople") == "");
        foreach (var b in boxes) b.Checked = b.Text == "Sue Tech";

        // The password box appears and closes the gap behind it rather than leaving a hole.
        Console.WriteLine();
        Console.WriteLine("== reflow ==");
        category.SelectedIndex = Array.IndexOf(TicketSummary.CategoryOrder, (int)TicketCategoryKind.Workshop);
        Settle();
        var pw = Field<CheckBox>(dlg, "_pwProtected");
        var pwBox = Field<Control>(dlg, "_devicePassword");
        var photoLabel = Field<Control>(dlg, "_photoLabel");
        if (pw != null && pwBox != null && photoLabel != null)
        {
            int before = photoLabel.Top;
            pw.Checked = true;
            Settle();
            Check("ticking 'password protected' reveals the box", pwBox.Visible);
            Check("...and pushes what follows DOWN rather than overlapping it",
                photoLabel.Top > before, $"{before} → {photoLabel.Top}");
            pw.Checked = false;
            Settle();
            Check("unticking closes the gap again", photoLabel.Top == before, $"{photoLabel.Top} vs {before}");
        }

        // ---- a recurring task (2026-10-07) ----
        Console.WriteLine();
        Console.WriteLine("== recurring task ==");
        RecurringFields(dlg, category);

        // The client-match list is the other reflow, and the one that left the hole in the
        // screenshot Clint sent.
        var matches = Field<ListBox>(dlg, "_matches");
        // Since 2026-10-06 the list sits in a rounded frame, and the FRAME is what the dialog shows and hides.
        var listShown = Field<Control>(dlg, "_matchesFrame") ?? (Control?)matches;
        var state = Field<Control>(dlg, "_clientState");
        if (matches != null && state != null)
        {
            Check("the match list is hidden when there is nothing to match", !listShown!.Visible);
            int stateTop = state.Top;
            matches.Items.Add("Zenith Motors · Bob · 011 555 0100");
            listShown.Visible = true;
            Settle();
            Check("showing matches pushes the rest down rather than overlapping",
                state.Top > stateTop, $"{stateTop} → {state.Top}");
            listShown.Visible = false;
            matches.Items.Clear();
            Settle();
            Check("hiding them takes the space back", state.Top == stateTop, $"{state.Top} vs {stateTop}");
        }

        // ---- does the text fit the controls it is drawn in? ----
        //
        // Everything else here is measured from a font, but button WIDTHS are still numbers. At
        // 200% or with a longer word, "Create ticket" would be drawn wider than the button holding
        // it and quietly lose its end. The height lesson from §30 applies sideways too.
        Console.WriteLine();
        Console.WriteLine("== text inside its control ==");
        foreach (var b in Leaves(dlg).OfType<RoundButton>())
        {
            int needed = TextRenderer.MeasureText(b.Text, b.Font).Width;
            Check($"\"{b.Text}\" fits its button", needed <= b.Width - 12, $"{needed} px of text in {b.Width} px");
        }
        foreach (var l in Leaves(dlg).OfType<Label>().Where(l => l.Visible && !l.AutoSize && l.Text.Length > 0))
        {
            // Single-line labels only: the multi-line ones are supposed to wrap.
            if (l.Height > TextRenderer.MeasureText("Ag", l.Font).Height + 8) continue;
            int needed = TextRenderer.MeasureText(l.Text, l.Font).Width;
            Check($"\"{Short(l.Text)}\" fits its label", needed <= l.Width, $"{needed} px of text in {l.Width} px");
        }

        // ---- a screen far smaller than this one ----
        //
        // The window is clamped to the working area, so on a small laptop it comes out smaller than
        // its contents. What must be true then is that everything is still REACHABLE: the scroller
        // takes over rather than the bottom of the form being lost. This is the case that cannot be
        // checked by looking, because this machine's screen is not small.
        Console.WriteLine();
        Console.WriteLine("== squeezed onto a small screen ==");
        var wasSize = dlg.Size;
        dlg.Size = new Size(900, 600);        // a 1366x768 laptop, roughly, after chrome
        Settle();
        var scroller = Leaves(dlg).OfType<Panel>().FirstOrDefault(p => p.AutoScroll);
        if (scroller == null) Check("the form has a scroller to fall back on", false);
        else
        {
            Check("the form has a scroller to fall back on", true);
            bool scrolls = scroller.VerticalScroll.Visible || scroller.HorizontalScroll.Visible;
            Check("squeezed small, it scrolls rather than hiding its contents", scrolls,
                $"vertical={scroller.VerticalScroll.Visible} horizontal={scroller.HorizontalScroll.Visible}");
            Check("the scrollable area still holds the whole form",
                scroller.DisplayRectangle.Height >= scroller.ClientSize.Height,
                $"{scroller.DisplayRectangle.Height} px of content in {scroller.ClientSize.Height} px");
        }
        // The buttons are docked to the bottom of the window, NOT inside the scroller, so they stay
        // put however small it gets. Losing Create ticket off the bottom is the failure that makes
        // a dialog useless rather than merely ugly.
        var createBtn = Leaves(dlg).OfType<RoundButton>().FirstOrDefault(b => b.Text.StartsWith("Create"));
        if (createBtn != null)
        {
            var r = dlg.RectangleToClient(createBtn.Parent!.RectangleToScreen(createBtn.Bounds));
            Check("Create ticket is still on screen when squeezed",
                r.Bottom <= dlg.ClientSize.Height + 1 && r.Right <= dlg.ClientSize.Width + 1, $"{r}");
        }
        Shot(dlg, outDir, "squeezed");
        dlg.Size = wasSize;
        Settle();

        dlg.Hide();

        // ---- the reason dialog ----
        // Built the same afternoon as the rewrite above and never looked at. It stands between a
        // technician and every unattended session, so a layout fault in it is one nobody can work
        // around.
        Console.WriteLine();
        Console.WriteLine("== why are you connecting? ==");
        using (var ask = new ReasonDialog(theme, "ZEN-SUE", "Sue Jones"))
        {
            ask.StartPosition = FormStartPosition.Manual;
            ask.Location = new Point(0, 0);
            ask.Show();
            Settle();

            var reasonWork = Screen.FromControl(ask).WorkingArea;
            Check("it fits the working area",
                ask.Width <= reasonWork.Width && ask.Height <= reasonWork.Height,
                $"{ask.Width}x{ask.Height}");

            // The complaint is hidden until somebody tries to connect without a reason, and a
            // HIDDEN control has not been laid out yet — it reports whatever bounds it was born
            // with, so measuring it as it stands proves nothing at all. Show it first, and measure
            // the dialog as it will actually look at the only moment this label matters.
            var complaint = Field<Control>(ask, "_complaint");
            if (complaint != null) { complaint.Visible = true; Settle(); }

            foreach (var f in new[] { "_reason", "_complaint" })
            {
                var c = Field<Control>(ask, f);
                if (c == null) { Check($"{f} exists", false); continue; }
                var r = ask.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
                Check($"{f} is inside the window",
                    r.Left >= 0 && r.Top >= 0 && r.Right <= ask.ClientSize.Width + 1 && r.Bottom <= ask.ClientSize.Height + 1,
                    $"{r} in {ask.ClientSize.Width}x{ask.ClientSize.Height}");
            }

            // And nothing it pushed about now sits on top of anything else.
            var reasonBox = Field<Control>(ask, "_reason");
            if (complaint != null && reasonBox != null)
            {
                var a = ask.RectangleToClient(complaint.Parent!.RectangleToScreen(complaint.Bounds));
                var b = ask.RectangleToClient(reasonBox.Parent!.RectangleToScreen(reasonBox.Bounds));
                Check("the complaint does not land on top of the reason box", !a.IntersectsWith(b), $"{a} vs {b}");
            }

            // Every button must be reachable, or the dialog is a dead end in front of a session.
            var roundButtons = Leaves(ask).OfType<RoundButton>().ToList();
            Check("both buttons exist", roundButtons.Count == 2, $"{roundButtons.Count}");
            foreach (var b in roundButtons)
            {
                var r = ask.RectangleToClient(b.Parent!.RectangleToScreen(b.Bounds));
                Check($"the {b.Text} button is inside the window",
                    r.Right <= ask.ClientSize.Width + 1 && r.Bottom <= ask.ClientSize.Height + 1, $"{r}");
            }

            Shot(ask, outDir, "reason");
            ask.Hide();
        }

        RecurringSubmit(theme, link);
        JobTypesFollowed(theme, outDir);

        return Done();
    }

    /// <summary>
    /// Job types (2026-10-27, HANDOVER §168): a company with Walk in switched off and no photos asked at booking. The dialog
    /// offers no Walk in, still reads the category by VALUE through the shorter list, says the photos are optional, and books
    /// a workshop machine in with none — OK sends it rather than stopping on "needs 3 photos".
    /// </summary>
    private static void JobTypesFollowed(Theme theme, string outDir)
    {
        Console.WriteLine();
        Console.WriteLine("== Job types: Walk in switched off, no photos asked at booking ==");
        var link = new TechLink("ws://127.0.0.1:59999");
        typeof(TechLink).GetProperty("DisplayName")!.SetValue(link, "Sue Tech");
        typeof(TechLink).GetProperty("Roster")!.SetValue(link, new List<string> { "Ann Tech", "Sue Tech" });
        // What a StateMsg from the server sets (StateMsg.CategoriesOff, BookingPhotos).
        typeof(TechLink).GetProperty("CategoriesOff")!.SetValue(link, new HashSet<int> { (int)TicketCategoryKind.WalkIn });
        typeof(TechLink).GetProperty("BookingPhotos")!.SetValue(link, 0);

        using var dlg = new NewTicketDialog(theme, link);
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new Point(0, 0);
        dlg.Show();
        Settle();
        var kinds = Field<List<RoundButton>>(dlg, "_kinds") ?? new();
        var names = kinds.Select(b => (string)b.Tag!).ToList();
        Check("no Walk in button, and the other five", !names.Contains("Walk in") && names.Count == TicketSummary.CategoryOrder.Length - 1, string.Join(", ", names));
        Check("General is still the one chosen to start with", Invoke<TicketCategoryKind>(dlg, "Chosen") == TicketCategoryKind.General);
        // Every button, pressed, is read back as ITS category — the list is shorter, so a position is not a value.
        bool allRight = true;
        foreach (var b in kinds)
        {
            b.PerformClick(); Settle();
            var chosen = Invoke<TicketCategoryKind>(dlg, "Chosen");
            if (TicketSummary.CategoryNames[(int)chosen] != (string)b.Tag!) { allRight = false; Console.WriteLine($"        {b.Tag} read back as {chosen}"); }
        }
        Check("every button is read back as its own category, not the one at its old position", allRight);

        kinds.First(b => (string)b.Tag! == "Workshop").PerformClick();
        Settle();
        var label = Field<Label>(dlg, "_photoLabel");
        Check("Workshop's photos say they are optional", label?.Text.Contains("doesn't ask for them") == true, label?.Text ?? "(no label)");
        Field<InputBox>(dlg, "_subject")!.Value = "Laptop will not charge";
        Field<InputBox>(dlg, "_clientSearch")!.Value = "Harbour Café";
        foreach (var b in Field<FlowLayoutPanel>(dlg, "_people")!.Controls.OfType<CheckBox>()) b.Checked = b.Text == "Sue Tech";
        Settle();
        Shot(dlg, outDir, "jobtypes-workshop-no-photos");
        // Only now: were the photo gate still 3, Submit would stop on a message box and the probe would wait for ever.
        if (Field<object>(dlg, "PhotosRequired") is int need && need == 0)
        {
            typeof(NewTicketDialog).GetMethod("Submit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dlg, null);
            Check("a workshop machine is booked in with NO photos", dlg.Result is { } m && m.Category == (int)TicketCategoryKind.Workshop,
                $"{dlg.Result?.Category}");
        }
        else Check("the dialog took the company's photo count (0)", false, $"{Field<object>(dlg, "PhotosRequired")}");
    }

    /// <summary>The server's answer to "which types, which people", handed to the dialog as the link would.</summary>
    private static void GiveKinds(NewTicketDialog dlg)
    {
        typeof(NewTicketDialog).GetMethod("OnRecurringKinds", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(dlg, new object[] { new RecurringKindsMsg
            {
                Kinds = new() { "Maintenance", "Billing", "Readings", "Printer toner" },
                People = new() { "Ann Tech", "Bob Tech", "Olive Office", "Sue Tech" }
            } });
        Settle();
    }

    private static void RecurringFields(NewTicketDialog dlg, ComboBox category)
    {
        GiveKinds(dlg);
        var kind = Field<ComboBox>(dlg, "_kind")!;
        var repeats = Field<ComboBox>(dlg, "_repeats")!;
        var people = Field<FlowLayoutPanel>(dlg, "_people")!;
        var olive = people.Controls.OfType<CheckBox>().FirstOrDefault(b => b.Text == "Olive Office");
        Check("the company's own types arrive, with New type… last",
            kind.Items.Cast<string>().SequenceEqual(new[] { "Maintenance", "Billing", "Readings", "Printer toner", "New type…" }),
            string.Join(", ", kind.Items.Cast<string>()));
        Check("the office administrator gets a box (a billing or readings task is often theirs)", olive != null);

        category.SelectedIndex = Array.IndexOf(TicketSummary.CategoryOrder, (int)TicketCategoryKind.Workshop);
        Settle();
        Check("...but NOT on a workshop job, whose people are the console's roster", olive != null && !olive.Visible);
        category.SelectedIndex = Array.IndexOf(TicketSummary.CategoryOrder, (int)TicketCategoryKind.Recurring);
        Settle();
        Check("...and is there for a recurring task", olive != null && olive.Visible && Chain(olive, dlg));
        // The office's line arrives after the window is sized. Before FitOfficePeople it brought in a
        // scrollbar that cut the right column's Type box off — which "inside the window" above could
        // not see, because the box was inside the window and only hidden by the scroller.
        var scrollerNow = Leaves(dlg).OfType<Panel>().FirstOrDefault(p => p.AutoScroll);
        Check("with the office's names in, the full-size window needs no scrollbar (nothing on the right is cut off)",
            scrollerNow != null && !scrollerNow.VerticalScroll.Visible && !scrollerNow.HorizontalScroll.Visible);
        olive!.Checked = true;
        category.SelectedIndex = Array.IndexOf(TicketSummary.CategoryOrder, (int)TicketCategoryKind.General);
        Settle();
        Check("leaving Recurring task unticks them, so a General job is never sent to the office by a hidden tick",
            !olive.Checked && Invoke<string>(dlg, "ChosenPeople") == "Sue Tech", Invoke<string>(dlg, "ChosenPeople"));
        category.SelectedIndex = Array.IndexOf(TicketSummary.CategoryOrder, (int)TicketCategoryKind.Recurring);
        Settle();

        foreach (var (i, name, shown, hidden) in new[]
        {
            (0, "Daily", "_weekdaysOnly", new[] { "_weekPanel", "_monthDay" }),
            (1, "Weekly", "_weekPanel", new[] { "_weekdaysOnly", "_monthDay" }),
            (2, "Monthly", "_monthDay", new[] { "_weekdaysOnly", "_weekPanel" }),
        })
        {
            repeats.SelectedIndex = i;
            Settle();
            var c = Field<Control>(dlg, shown)!;
            var r = dlg.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
            Check($"{name}: {shown} is on screen and inside the window",
                c.Visible && Chain(c, dlg) && r.Bottom <= dlg.ClientSize.Height + 1 && r.Right <= dlg.ClientSize.Width + 1, $"{r}");
            Check($"{name}: the other two are hidden", hidden.All(h => !Field<Control>(dlg, h)!.Visible));
        }
        var monthDay = Field<ComboBox>(dlg, "_monthDay")!;
        Check("the month offers the 1st to the 31st and the last day", monthDay.Items.Count == 32
            && (string)monthDay.Items[0]! == "The 1st" && (string)monthDay.Items[21]! == "The 22nd" && ((string)monthDay.Items[30]!).StartsWith("The 31st")
            && (string)monthDay.Items[31]! == "The last day of the month");
        Shot(dlg, Path.Combine(AppContext.BaseDirectory, "shots"), "recurring-monthly");
        repeats.SelectedIndex = 1;
        kind.SelectedItem = "New type…";
        Settle();
        var newKind = Field<Control>(dlg, "_newKind")!;
        Check("New type… opens a box to name it", newKind.Visible && Chain(newKind, dlg));
        kind.SelectedIndex = 0;
        Settle();
        Check("...and choosing a type closes it", !newKind.Visible);
        Shot(dlg, Path.Combine(AppContext.BaseDirectory, "shots"), "recurring-weekly");
    }

    /// <summary>What OK sends for a recurring task: a schedule, read by value, with the office person ticked.</summary>
    private static void RecurringSubmit(Theme theme, TechLink link)
    {
        Console.WriteLine();
        Console.WriteLine("== recurring task: what OK sends ==");
        using var dlg = new NewTicketDialog(theme, link);
        dlg.Show();
        Settle();
        GiveKinds(dlg);
        var category = Field<ComboBox>(dlg, "_category")!;
        category.SelectedIndex = Array.IndexOf(TicketSummary.CategoryOrder, (int)TicketCategoryKind.Recurring);
        Settle();
        Field<InputBox>(dlg, "_subject")!.Value = "Month-end invoices";
        Field<ComboBox>(dlg, "_kind")!.SelectedItem = "Billing";
        foreach (var b in Field<FlowLayoutPanel>(dlg, "_people")!.Controls.OfType<CheckBox>()) b.Checked = b.Text is "Olive Office";
        Field<ComboBox>(dlg, "_repeats")!.SelectedIndex = 2;
        Field<ComboBox>(dlg, "_monthDay")!.SelectedIndex = 31;   // the last day
        Settle();
        typeof(NewTicketDialog).GetMethod("Submit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dlg, null);
        var m = dlg.Result;
        Check("no client is needed, and OK sends it", m != null);
        if (m == null) return;
        Check("...as a Recurring task, by value", m.Category == (int)TicketCategoryKind.Recurring, m.Category.ToString());
        Check("...of type Billing, monthly, on the last day (0)", m.RecurringKind == "Billing" && m.Repeats == 2 && m.MonthDay == 0, $"{m.RecurringKind} {m.Repeats} {m.MonthDay}");
        Check("...to the office administrator", m.AssignTo == "Olive Office", m.AssignTo ?? "");

        using var dlg2 = new NewTicketDialog(theme, link);
        dlg2.Show();
        Settle();
        var cat2 = Field<ComboBox>(dlg2, "_category")!;
        cat2.SelectedIndex = Array.IndexOf(TicketSummary.CategoryOrder, (int)TicketCategoryKind.Recurring);
        Settle();
        Field<InputBox>(dlg2, "_subject")!.Value = "Backups";
        foreach (var b in Field<FlowLayoutPanel>(dlg2, "_weekPanel")!.Controls.OfType<CheckBox>()) b.Checked = b.Text is "Monday" or "Thursday";
        typeof(NewTicketDialog).GetMethod("Submit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dlg2, null);
        Check("weekly, Monday and Thursday, sends those two days' bits",
            dlg2.Result is { Repeats: 1 } r2 && r2.Days == ((1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Thursday)), $"{dlg2.Result?.Days}");
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    private static void Settle()
    { for (int i = 0; i < 12; i++) { Application.DoEvents(); Thread.Sleep(25); } }

    /// <summary>Visible all the way up to the form, not just in its own right.</summary>
    private static bool Chain(Control c, Control root)
    {
        for (var p = c.Parent; p != null && p != root; p = p.Parent)
            if (!p.Visible) return false;
        return true;
    }

    private static void Shot(Form f, string dir, string name)
    {
        try
        {
            using var bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            var path = Path.Combine(dir, name + ".png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"        picture: {path}");
        }
        catch (Exception ex) { Console.WriteLine($"        (no picture: {ex.Message})"); }
    }

    private static string Short(string s) => s.Length <= 28 ? s : s[..26] + "…";

    /// <summary>Every control under one, however deeply nested.</summary>
    private static IEnumerable<Control> Leaves(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Leaves(c)) yield return d;
        }
    }

    private static T? Field<T>(object o, string field) where T : class =>
        o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o) as T;

    /// <summary>Calls a private no-argument method on the dialog — used to ask it which category it thinks it is on.</summary>
    private static T Invoke<T>(object o, string method) =>
        (T)o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, null)!;
}
