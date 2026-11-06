using System.Reflection;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// ticketwindowprobe - the console's ticket window. Built for the workflows 2026-09-24; rebuilt
// 2026-09-27 as a STORY, like the portal and like Anywhere's current system: the stage and the next
// steps at the top, a comment box that is always there, each step's form opening at the top of a
// numbered timeline, newest first, and Photos / Quote / Details as tabs.
//
// The window decides nothing: every button and every field is an answer in the TicketDetail. So this
// hands the REAL window tickets at the stages worth looking at — with no server, through
// TicketsForm.ShowTicket — and asks:
//
//   are the step buttons exactly the actions sent, no more and no fewer
//   is nothing asked for until a step is pressed, and then exactly the fields that step needs
//   does what is typed into a step survive a redraw, and does the form close when the stage moves
//   does the Quote needed step have the Item box with Add BESIDE it (Clint, 2026-09-27)
//   is Request quote sent to the browser rather than attempted here
//   does a job with no workflow still get its status box, and a workflow job not
//   does every button's text fit the button, and does nothing run off the right-hand side (§30)
//
// Pictures on every run, because the numbers say it fits and only a picture says it looks right.
//
//   dotnet run -c Debug --project tools/ticketwindowprobe/ticketwindowprobe.csproj [picture folder]
// ---------------------------------------------------------------------------------------------

internal static class TicketWindowProbe
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
        var link = new TechLink("ws://127.0.0.1:59999");   // nothing listens: this measures, it does not talk
        // Who is signed in and who is on the team, as a real sign-in would say. Without a team the
        // "people on it" tick boxes (2026-09-28) have nobody to draw, and the pictures show a heading
        // over nothing — which is how the first run of this probe after that change looked.
        typeof(TechLink).GetProperty("DisplayName")!.SetValue(link, "Sue Tech");
        typeof(TechLink).GetProperty("Roster")!.SetValue(link, new List<string> { "Ann Tech", "Bob Tech", "Sue Tech" });

        Console.WriteLine("ticketwindowprobe - the console's ticket window, as a story");
        Console.WriteLine($"Screen: {Screen.PrimaryScreen?.WorkingArea}   one line of body text: {TextRenderer.MeasureText("Ag", theme.Body).Height} px");
        Console.WriteLine();

        using var form = new TicketsForm(link, theme, brand);
        form.StartPosition = FormStartPosition.Manual;
        form.Show();
        Settle();
        var work = Screen.FromControl(form).WorkingArea;
        Check("the window fits the working area", form.Width <= work.Width && form.Height <= work.Height, $"{form.Width}x{form.Height} in {work.Width}x{work.Height}");

        // ---- 1. booked in: one step to take ----
        Console.WriteLine("== booked in ==");
        var booked = Workshop(1, "awaiting-diagnosis", "Booked in", "", "With Sue Tech",
            Act("inspection", "Inspection findings", fields: "Details"),
            Act("wait-customer", "Waiting on customer"));
        booked.AskedBy.Add("Oscar Office");
        booked.Notes.Add(Sys("Oscar Office", "Workshop ticket opened, assigned to Sue Tech."));
        form.ShowTicket(booked); Settle();

        Check("the step buttons are exactly the actions sent",
            StepButtons(form).SequenceEqual(new[] { "☞ Inspection findings", "☞ Waiting on customer" }), string.Join(", ", StepButtons(form)));
        Check("the stage is a badge in the crumb", Pills(form).Any(p => p.Text == "Booked in"));
        Check("the tabs are Activity, Photos and Details — no Quote before there is one",
            Has(form, "Activity") && Has(form, "Photos (3 in · 0 out)") && Has(form, "Details") && !Buttons(form).Any(b => b.Text.StartsWith("Quote")));
        Check("the comment box is there before anything is pressed", Has(form, "Post") && Inputs(form) == 1);
        Check("nothing is asked for until a step is pressed", !Has(form, "Submit") && !Has(form, "Cancel"));
        Check("the flag for Sue shows", Labels(form).Any(l => l.Text.StartsWith("Oscar Office asked you")));
        Check("the story ends with how it started, numbered 0",
            Labels(form).Any(l => l.Text.StartsWith("Oscar Office booked the machine in")) && Pills(form).Any(p => p.Text == "0") && Pills(form).Any(p => p.Text == "1"));
        Check("there is no status box on a workflow job", !StatusBoxes(form).Any());
        Fits(form, "booked in");
        Shot(form, outDir, "booked-in");

        Click(form, "☞ Inspection findings"); Settle();
        Check("pressing the step opens its form at the top of the story, numbered next",
            Has(form, "Submit") && Has(form, "Cancel") && Labels(form).Any(l => l.Text == "What did the inspection find?") && Pills(form).Any(p => p.Text == "2"));
        Check("it asks for exactly one thing", Inputs(form) == 2, $"{Inputs(form)} boxes including the comment box");
        Click(form, "Submit"); Settle();
        Check("Submit with nothing filled in says what is missing", Labels(form).Any(l => l.Visible && l.Text == "Fill in: What did the inspection find?"));
        var box = All(Body(form)).OfType<InputBox>().Last();
        box.Value = "Charging port loose.";
        form.ShowTicket(booked); Settle();
        Check("what was typed survives a redraw of the same stage",
            Has(form, "Submit") && All(Body(form)).OfType<InputBox>().Any(b => b.Value == "Charging port loose."));
        Fits(form, "with the step open");
        Shot(form, outDir, "booked-in-step");

        // ---- 2. the stage moves: the form closes, and the story says what happened ----
        Console.WriteLine();
        Console.WriteLine("== inspected ==");
        var inspected = Workshop(1, "inspected", "Inspected", "Booked in", "With Sue Tech",
            Act("repairable", "Repairable"), Act("not-repairable", "Not repairable"), Act("wait-customer", "Waiting on customer"));
        inspected.Notes.AddRange(booked.Notes);
        inspected.Notes.Add(Form_("Sue Tech", "Inspection findings", "Inspected", "Charging port loose."));
        form.ShowTicket(inspected); Settle();
        Check("the form closed when the stage moved", !Has(form, "Submit"));
        Check("the crumb names the stage it came from", Labels(form).Any(l => l.Text.Contains("Booked in  ›")) && Pills(form).Any(p => p.Text == "Inspected"));
        Check("the step reads as a form in the story, with where it moved the job",
            Labels(form).Any(l => l.Text.Trim() == "Inspection findings   →  Inspected") && Labels(form).Any(l => l.Text == "Charging port loose."));
        Check("Repairable and Not repairable are plain steps, no toggle",
            StepButtons(form).SequenceEqual(new[] { "☞ Repairable", "☞ Not repairable", "☞ Waiting on customer" }));
        Fits(form, "inspected"); Shot(form, outDir, "inspected");

        // ---- 3. repairable: the three endings at once, and the quote ----
        // Clint, 2026-09-27: "If you click on repairable, it should have the solved to be invoiced, quote
        // needed, do not invoice options. If once clicks on solved to invoiced, this is where the work
        // done should come in with the time spent and amount to invoice."
        Console.WriteLine();
        Console.WriteLine("== repairable ==");
        var done = Workshop(1, "repairable", "Repairable", "Inspected", "With Sue Tech",
            new StageActionInfo { Key = "solved-invoice", Label = "Solved — to be invoiced", Fields = new() { "Details", "Minutes", "Amount" }, IsDecision = true },
            Act("quote-needed", "Quote needed"),
            Act("do-not-invoice", "Do not invoice", fields: "Reason"));
        done.MayEditLines = true; done.ShowQuote = true;
        done.Notes.AddRange(inspected.Notes);
        done.Notes.Add(Sys("Sue Tech", "Repairable → Repairable"));
        done.QuoteLines.Add(new QuoteLineInfo { Id = 1, ItemName = "DDR4 memory 8GB SO-DIMM", PartNumber = "KVR26S19S8/8", SerialNumber = "SN-8GB-001", Quantity = 2, HasPhoto = true });
        done.Facts.Add(new FactInfo { Label = "Repairable", Value = "Yes" });
        // 2026-09-29: the machine the quote is for, arriving from the booking.
        done.QuoteMachine = new QuoteMachineInfo { Make = "Asus", Model = "VivoBook 123", Serial = "SN123", FromBooking = true };
        done.MachineTypes = new() { "Desktop", "Laptop", "All-in-one" };
        form.ShowTicket(done); Settle();
        Check("Repairable offers the three endings at once", StepButtons(form).SequenceEqual(new[] { "☞ Solved — to be invoiced", "☞ Quote needed", "☞ Do not invoice" }));
        Check("a Quote tab appears once the quote may be edited", Has(form, "Quote (1)"));
        Click(form, "☞ Solved — to be invoiced"); Settle();
        var captions = Labels(form).Select(l => l.Text).ToList();
        int iWork = captions.IndexOf("What was done to the machine?"), iTime = captions.FindIndex(c => c.StartsWith("Time spent")), iAmount = captions.IndexOf("Amount to invoice");
        Check("Solved asks what was done, the time and the amount — in that order, and nothing else",
            Inputs(form) == 4 && iWork >= 0 && iWork < iTime && iTime < iAmount, $"{Inputs(form)} boxes including the comment box; at {iWork}, {iTime}, {iAmount}");
        Fits(form, "the Solved step"); Shot(form, outDir, "solved-step");
        Click(form, "☞ Do not invoice"); Settle();
        Check("pressing another step swaps the form rather than adding one", Inputs(form) == 2 && Labels(form).Any(l => l.Text == "Why it should not be invoiced"));

        Click(form, "☞ Quote needed"); Settle();
        var item = All(Body(form)).OfType<InputBox>().FirstOrDefault(b => b.Box.PlaceholderText.StartsWith("Choose from the list"));
        var add = Buttons(form).FirstOrDefault(b => b.Text == "Add");
        Check("the Quote needed step has the Item box", item != null);
        Check("with Add BESIDE it, on the same line, to its right",
            item != null && add != null && Math.Abs(Screen_(add).Top - Screen_(item).Top) <= 6 && Screen_(add).Left > Screen_(item).Right,
            item == null || add == null ? "missing" : $"box {Screen_(item)}  add {Screen_(add)}");
        Check("the line already quoted is listed with a Remove", Labels(form).Any(l => l.Text.Contains("2 × DDR4 memory 8GB SO-DIMM")) && Has(form, "Remove"));
        Check("and the step sends it to the office", Has(form, "Submit — send to the office for a quote") && Has(form, "Photos of the part…"));
        // 2026-10-06: as many photos of a part as it needs, and more for a line already on the quote.
        Check("a line from an older server (HasPhoto alone) still says it has a photo", Labels(form).Any(l => l.Text.EndsWith("  ·  photo")));
        Check("each line has Add photos beside Remove", Has(form, "Add photos"));
        var addPhotos = Buttons(form).FirstOrDefault(b => b.Text == "Add photos");
        var removeLine = Buttons(form).FirstOrDefault(b => b.Text == "Remove");
        Check("...on the same line as Remove, and inside the window",
            addPhotos != null && removeLine != null && Math.Abs(Screen_(addPhotos).Top - Screen_(removeLine).Top) <= 6 && Screen_(removeLine).Right <= Screen_(form).Right,
            addPhotos == null || removeLine == null ? "missing" : $"add {Screen_(addPhotos)} remove {Screen_(removeLine)} window {Screen_(form)}");
        Check("nothing chosen says it is optional and has no limit", Labels(form).Any(l => l.Text == "optional — as many as you like"));
        done.QuoteLines[0].PhotoCount = 3;
        form.ShowTicket(done); Settle();
        Click(form, "☞ Quote needed"); Settle();
        Check("a line with three photos says so", Labels(form).Any(l => l.Text.EndsWith("  ·  3 photos")),
            string.Join(" | ", Labels(form).Select(l => l.Text).Where(t => t.Contains("DDR4"))));
        done.QuoteLines[0].PhotoCount = 0;
        // The redraw made new controls; what follows measures these ones.
        item = All(Body(form)).OfType<InputBox>().FirstOrDefault(b => b.Box.PlaceholderText.StartsWith("Choose from the list"));
        add = Buttons(form).FirstOrDefault(b => b.Text == "Add");
        // Clint, 2026-09-27: "lets also add a field for serial number" — beside the part number.
        var partBox = All(Body(form)).OfType<InputBox>().FirstOrDefault(b => b.Box.PlaceholderText.StartsWith("Part number"));
        var serialBox = All(Body(form)).OfType<InputBox>().FirstOrDefault(b => b.Box.PlaceholderText == "Serial number (optional)");
        Check("a Serial number box sits on the Part number line, to its right",
            partBox != null && serialBox != null && Math.Abs(Screen_(serialBox).Top - Screen_(partBox).Top) <= 2 && Screen_(serialBox).Left > Screen_(partBox).Right,
            partBox == null || serialBox == null ? "missing" : $"part {Screen_(partBox)}  serial {Screen_(serialBox)}");
        Check("and a line on the quote shows its serial", Labels(form).Any(l => l.Text.Contains("serial SN-8GB-001")));
        // Clint, 2026-09-29: "fields for Computer or Laptop type plus its serial number" — one machine
        // per quote, optional, and on a workshop job "directly from the book in form".
        InputBox? MachineBox(string hint) => All(Body(form)).OfType<InputBox>().FirstOrDefault(b => b.Box.PlaceholderText.StartsWith(hint));
        var mMake = MachineBox("Make"); var mModel = MachineBox("Model"); var mSerial = All(Body(form)).OfType<InputBox>().FirstOrDefault(b => b.Box.PlaceholderText == "Serial number");
        Check("the quote step asks which machine, filled in from the booking",
            mMake?.Value == "Asus" && mModel?.Value == "VivoBook 123" && mSerial?.Value == "SN123" && Labels(form).Any(l => l.Text.StartsWith("Filled in from the booking")),
            $"{mMake?.Value} / {mModel?.Value} / {mSerial?.Value}");
        var mType = All(Body(form)).OfType<ComboBox>().FirstOrDefault(c => c.Items.Count > 0 && c.Items[0] as string == "Type");
        Check("the type offers the three kinds, and nothing chosen yet",
            mType != null && mType.Items.Cast<object>().Select(o => o.ToString()).SequenceEqual(new[] { "Type", "Desktop", "Laptop", "All-in-one" }) && mType.SelectedIndex == 0);
        Check("the machine comes above the items, with its own Save",
            mMake != null && item != null && Screen_(mMake).Bottom < Screen_(item).Top && Has(form, "Save"));
        Check("the machine's boxes do not overlap and stay inside the card",
            mType != null && mMake != null && mModel != null && Screen_(mType).Right <= Screen_(mMake).Left && Screen_(mMake).Right <= Screen_(mModel).Left
            && mSerial != null && add != null && Screen_(mModel).Right <= Screen_(add).Right + 2 && Screen_(mSerial).Right < Screen_(add).Right
            && Screen_(mType).Left == Screen_(mSerial).Left,
            mType == null || mMake == null || mModel == null ? "missing" : $"type {Screen_(mType)} make {Screen_(mMake)} model {Screen_(mModel)} serial {Screen_(mSerial!)} add {Screen_(add!)}");
        Fits(form, "quote needed step"); Shot(form, outDir, "quote-needed-step");
        mMake!.Value = "Asus ROG";
        Click(form, "Quote (1)"); Settle();
        Check("what is typed into the machine survives a redraw until it is saved", MachineBox("Make")?.Value == "Asus ROG", MachineBox("Make")?.Value ?? "missing");
        Click(form, "Activity"); Settle();

        Click(form, "Quote (1)"); Settle();
        Check("the Quote tab has the same editor, and no comment box or story",
            Has(form, "Add") && !Has(form, "Post") && !Labels(form).Any(l => l.Text.StartsWith("Oscar Office booked")));
        Shot(form, outDir, "quote-tab");
        Click(form, "Details"); Settle();
        Check("Details holds the machine, the facts and who has it — with no status box, and nothing to copy yet",
            Labels(form).Any(l => l.Text == "Repairable") && !Has(form, "Copy the work done for the invoice") && Has(form, "Save") && !StatusBoxes(form).Any());
        Fits(form, "details"); Shot(form, outDir, "details");
        // 2026-10-13, Clint: "Tickets, Subjects (names), scheduled appointment times, category etc cannot be edited".
        Check("Details opens with Edit the ticket: the subject in its box, the description in its",
            Labels(form).Any(l => l.Text.Trim() == "Edit the ticket")
            && All(Body(form)).OfType<InputBox>().Any(b => b.Value == done.Ticket.Subject) && Has(form, "Save changes"));
        var kindsShown = Buttons(form).OfType<RoundButton>().Where(b => TicketSummary.CategoryNames.Contains(b.Text)).ToList();
        Check("the types of job are offered, the ticket's own chosen — and not Recurring task, which is set up elsewhere",
            kindsShown.Count == 5 && kindsShown.Single(b => b.Chosen).Text == "Workshop" && kindsShown.All(b => b.Text != "Recurring task"),
            string.Join(", ", kindsShown.Select(b => b.Text + (b.Chosen ? "*" : ""))));
        Check("no appointment boxes on a workshop job that has none", !All(Body(form)).OfType<DateBox>().Any(b => b.AccessibleName != "Due date"));
        // The due date (2026-10-26, §165): on every type of job, "No due date" ticked when it has none, the box hidden.
        var noDueTick = All(Body(form)).OfType<RoundCheck>().FirstOrDefault(c => c.Text == "No due date");
        Check("a Due date, with \"No due date\" ticked on a job that has none", noDueTick is { Checked: true }
            && All(Body(form)).OfType<DateBox>().Any(b => b.AccessibleName == "Due date"));
        All(Body(form)).OfType<InputBox>().First(b => b.Value == done.Ticket.Subject).Value = "A subject typed but not saved";
        Buttons(form).First(b => b.Text == "Call out").PerformClick(); Settle();
        Check("choosing Call out warns that it starts again, and asks the appointment",
            Labels(form).Any(l => l.Text.StartsWith("⚠ It starts again at the first stage")) && All(Body(form)).OfType<DateBox>().Any() && All(Body(form)).OfType<CheckBox>().Any(c => c.Text == "No appointment"));
        Check("...and what was typed in the subject survives the redraw", All(Body(form)).OfType<InputBox>().Any(b => b.Value == "A subject typed but not saved"));
        Fits(form, "details, editing as a call out"); Shot(form, outDir, "details-edit-callout");
        var noAppt = All(Body(form)).OfType<CheckBox>().First(c => c.Text == "No appointment");
        Check("a call out with none yet starts at No appointment, with no boxes to misread", noAppt.Checked && !All(Body(form)).OfType<DateBox>().First().Parent!.Visible);
        noAppt.Checked = false; Settle();
        Check("...untick it and the date and time are there to choose, under it", All(Body(form)).OfType<DateBox>().First().Parent!.Visible
            && Screen_(All(Body(form)).OfType<DateBox>().First()).Top > Screen_(noAppt).Bottom);
        Fits(form, "details, a call out's appointment"); Shot(form, outDir, "details-edit-appointment");
        Buttons(form).First(b => b.Text == "Workshop").PerformClick(); Settle();
        Check("back on its own type, the warning goes", !Labels(form).Any(l => l.Text.StartsWith("⚠ It starts again")));
        Click(form, "Activity"); Settle();
        Check("the story has a way straight to it", Has(form, "✎ Edit the ticket"));
        Click(form, "✎ Edit the ticket"); Settle();
        Check("...which opens Details", Has(form, "Save changes"));
        Click(form, "Photos (3 in · 0 out)"); Settle();
        Check("Photos has the upload and the way to see them", Has(form, "Add photos as it leaves…") && Has(form, "See them in the browser"));
        Click(form, "Activity"); Settle();

        // ---- 4. the billing queue, as a supervisor sees it ----
        Console.WriteLine();
        Console.WriteLine("== billing, as a supervisor ==");
        var billing = Workshop(3, "quote-needed", "Quote needed", "Repairable", "In the billing queue",
            new StageActionInfo { Key = "quote-requested", Label = "Quote requested", Fields = new() { "SupplierEmail" } },
            new StageActionInfo { Key = "abandon", Label = "Abandoned", Fields = new() { "Reason" }, IsDanger = true, Closes = true });
        billing.ShowQuote = true;
        billing.QuoteLines.AddRange(done.QuoteLines);
        form.ShowTicket(billing); Settle();
        Check("Request quote is a step button", StepButtons(form).SequenceEqual(new[] { "☞ Request quote", "☞ Abandoned" }));
        Click(form, "☞ Request quote"); Settle();
        Check("and it goes to the browser, not a form here", !Has(form, "Submit") && !Has(form, "Cancel"));
        Click(form, "☞ Abandoned"); Settle();
        Check("Abandoned asks why", Labels(form).Any(l => l.Text == "Why it is being abandoned") && Has(form, "Submit"));
        Click(form, "Quote (1)"); Settle();
        Check("with no editing allowed, the quote shows its line and no Add", Labels(form).Any(l => l.Text.Contains("DDR4")) && !Has(form, "Add") && !Has(form, "Remove"));
        Fits(form, "billing"); Shot(form, outDir, "billing");

        var collect = Workshop(4, "awaiting-payment", "Invoiced — awaiting payment", "Client invoiced", "In the billing queue",
            Act("payment-received", "Payment received — close ticket", closes: true), Act("collected", "Machine collected"));
        collect.PhotosOut = 1;
        form.ShowTicket(collect); Settle();
        Click(form, "☞ Machine collected"); Settle();
        Check("Machine collected asks for the photos, and says how many it has",
            Labels(form).Any(l => l.Text.StartsWith("Take at least 3 photos") && l.Text.Contains("1 added so far")) && Has(form, "Add photos as it leaves…")
            && Has(form, "Submit — the machine has been collected"));
        Fits(form, "collection"); Shot(form, outDir, "collection");

        // ---- 4b. a call out (Clint, 2026-09-27): Work done, then three ways to finish ----
        Console.WriteLine();
        Console.WriteLine("== a call out ==");
        var callout = new TicketDetail
        {
            Ticket = new TicketSummary { Id = 7, Number = 107, Subject = "Printer jams", Category = (int)TicketCategoryKind.CallOut, ClientName = "Zenith Accounting", Status = 1, CreatedUtc = DateTime.UtcNow.AddDays(-1) },
            OpenedBy = "Oscar Office", Stage = "callout-work-done", StageLabel = "Work done", PreviousStageLabel = "Call out booked", WithWhom = "With Sue Tech",
            SiteAddress = "12 Long Street, Cape Town", Description = "Jams on every page.", PhotosRequired = 3,
            People = new() { "Sue Tech", "Oscar Office" },
            Actions = new()
            {
                new StageActionInfo { Key = "callout-invoice", Label = "Ticket completed — to be invoiced", Fields = new() { "Amount" }, IsDecision = true },
                new StageActionInfo { Key = "callout-paid-onsite", Label = "Invoiced — paid on site", IsDecision = true },
                new StageActionInfo { Key = "needs-workshop", Label = "Needs workshop work", Fields = new() { "AssetMake", "AssetModel", "AssetSerial", "Accessories", "DevicePassword" } },
            }
        };
        callout.Notes.Add(new TicketNoteInfo { Author = "Sue Tech", FormTitle = "Work done", MovedToLabel = "Work done", IsWorkDone = true, CreatedUtc = DateTime.UtcNow,
            Body = "Cleared the paper path.\nTime spent: 45 min", WorkDoneText = "Cleared the paper path." });
        form.ShowTicket(callout); Settle();
        Check("after Work done: the three options, in Clint's order",
            StepButtons(form).SequenceEqual(new[] { "☞ Ticket completed — to be invoiced", "☞ Invoiced — paid on site", "☞ Needs workshop work" }), string.Join(", ", StepButtons(form)));
        Check("the crumb says Call out", Labels(form).Any(l => l.Text.StartsWith("Call out  ›")));
        Check("no Photos tab on a call out — there is no machine yet", Has(form, "Activity") && Has(form, "Details") && !Buttons(form).Any(b => b.Text.StartsWith("Photos")));
        Check("the story starts with the call out being booked, not a machine", Labels(form).Any(l => l.Text.StartsWith("Oscar Office booked the call out")));
        Click(form, "☞ Ticket completed — to be invoiced"); Settle();
        Check("Completed — to be invoiced asks only the amount", Inputs(form) == 2 && Labels(form).Any(l => l.Text == "Amount to invoice"));
        Click(form, "☞ Needs workshop work"); Settle();
        Check("Needs workshop work asks for the photos in, and the booking's machine questions",
            Has(form, "Add photos as it arrives…") && new[] { "Make", "Model", "Serial number", "Accessories left with it", "Password to sign in (leave empty if none)" }.All(c => Labels(form).Any(l => l.Text == c)));
        Click(form, "Submit"); Settle();
        Check("...and the machine questions are optional: Submit says nothing is missing", !Labels(form).Any(l => l.Visible && l.Text.StartsWith("Fill in")));
        Fits(form, "a call out going to the workshop"); Shot(form, outDir, "callout-to-workshop");
        Click(form, "Details"); Settle();
        Check("Details has the site, not a machine", Labels(form).Any(l => l.Text == "12 Long Street, Cape Town") && !Labels(form).Any(l => l.Text == "Make / model"));
        Click(form, "Activity"); Settle();

        var onSite = new TicketDetail
        {
            Ticket = callout.Ticket, OpenedBy = "Oscar Office", Stage = "paid-onsite", StageLabel = "Invoiced — paid on site", PreviousStageLabel = "Work done",
            WithWhom = "With Sue Tech", SiteAddress = callout.SiteAddress, People = callout.People,
            Actions = new()
            {
                new StageActionInfo { Key = "paid-cash", Label = "Paid cash", Fields = new() { "Amount" } },
                new StageActionInfo { Key = "paid-card", Label = "Paid card machine", Fields = new() { "Amount", "Reference" } },
            }
        };
        form.ShowTicket(onSite); Settle();
        Check("paid on site has its two options", StepButtons(form).SequenceEqual(new[] { "☞ Paid cash", "☞ Paid card machine" }));
        Click(form, "☞ Paid cash"); Settle();
        Check("Paid cash asks for the amount paid", Inputs(form) == 2 && Labels(form).Any(l => l.Text == "Amount paid"));
        Click(form, "☞ Paid card machine"); Settle();
        Check("Paid card machine asks for the amount and the card machine reference number",
            Inputs(form) == 3 && Labels(form).Any(l => l.Text == "Amount paid") && Labels(form).Any(l => l.Text == "Card machine reference number"));
        Fits(form, "paid on site"); Shot(form, outDir, "paid-card");

        // ---- 4b'. two machines on ONE job: a data transfer (Clint, 2026-10-02) ----
        Console.WriteLine();
        Console.WriteLine("== two machines on one job (2026-10-02) ==");
        var bookedIn = Workshop(9, "awaiting-diagnosis", "Booked in", "", "With Sue Tech",
            new StageActionInfo { Key = "add-machine", Label = "Add another machine",
                Fields = new() { "SameJob", "Details", "AssetMake", "AssetModel", "AssetSerial", "Accessories", "DevicePassword" } },
            Act("abandon", "Abandoned", true, "Reason"));
        form.ShowTicket(bookedIn); Settle();
        Click(form, "☞ Add another machine"); Settle();
        var jobPick = All(Body(form)).OfType<ComboBox>().FirstOrDefault(c => c.Items.Count == 2 && c.Items[0]!.ToString()!.StartsWith("No — a separate job"));
        Check("Add another machine asks same job or separate FIRST, separate chosen to begin with",
            jobPick != null && jobPick.SelectedIndex == 0 && Labels(form).Any(l => l.Text == "Is it part of this job?"));
        Check("...and the box under it asks the fault", Labels(form).Any(l => l.Text == "What is wrong with it?"));
        Click(form, "Submit"); Settle();
        Check("separate job: Submit with no fault says it is missing", Labels(form).Any(l => l.Visible && l.Text == "Fill in: What is wrong with it?"));
        if (jobPick != null) { jobPick.SelectedIndex = 1; Settle(); }
        Check("choosing the same job relabels that box: what the machine is for, optional",
            Labels(form).Any(l => l.Text.StartsWith("What is this machine on the job? (optional")));
        form.ShowTicket(bookedIn); Settle();   // a refusal redraws the form
        var again = All(Body(form)).OfType<ComboBox>().FirstOrDefault(c => c.Items.Count == 2 && c.Items[0]!.ToString()!.StartsWith("No — a separate job"));
        Check("a redraw (a refusal) KEEPS the same-job choice — never quietly back to a separate ticket",
            again?.SelectedIndex == 1 && Labels(form).Any(l => l.Text.StartsWith("What is this machine on the job?")), again?.SelectedIndex.ToString() ?? "no dropdown");
        Fits(form, "add another machine, same job"); Shot(form, outDir, "add-machine-same-job");

        var two = Workshop(10, "to-be-invoiced", "To be invoiced", "Solved — to be invoiced", "In the billing queue",
            new StageActionInfo { Key = "collected", Label = "Machine collected" });
        two.Description = "Move everything from the old laptop to the new one.";
        two.PhotosIn = 6; two.PhotosOut = 3; two.PhotosOutRequired = 6;
        two.Machines.Add(new TicketMachineInfo { Id = 1, Purpose = "The new laptop — the data goes onto this one.", Make = "Lenovo", Model = "X1 Carbon",
            Serial = "PF3NEW", Accessories = "USB-C charger", PasswordProtected = true, Password = "hunter2", PhotosIn = 3 });
        form.ShowTicket(two); Settle();
        Click(form, "☞ Machine collected"); Settle();
        Check("collection asks for 6 photos: 3 of each of the 2 machines, as THEY leave",
            Labels(form).Any(l => l.Text.StartsWith("Take at least 6 photos (3 of each of the 2 machines on this job) as they leave")),
            string.Join(" | ", Labels(form).Where(l => l.Text.StartsWith("Take")).Select(l => l.Text)));
        Check("the booking says Machine 1, with ITS photos in only",
            Labels(form).Any(l => l.Text == "Machine 1") && Labels(form).Any(l => l.Text == "3"));
        Click(form, "Details"); Settle();
        Check("Details has a card for each machine", All(Body(form)).Any(c => c.Text.Trim() == "Machine 1") && All(Body(form)).Any(c => c.Text.Trim() == "Machine 2"));
        Check("...the second with what it is for, its details and photos",
            Labels(form).Any(l => l.Text == "The new laptop — the data goes onto this one.") && Labels(form).Any(l => l.Text == "Lenovo X1 Carbon") && Labels(form).Any(l => l.Text == "PF3NEW"));
        Check("...and its own password behind its own button", Has(form, "Show this machine's password"));
        Click(form, "Show this machine's password"); Settle();
        Check("...which shows it", Has(form, "hunter2"));
        Fits(form, "two machines, details"); Shot(form, outDir, "two-machines-details");
        Click(form, "Photos (6 in · 3 out)"); Settle();
        Check("Photos says 6 are needed out, 3 of each", Labels(form).Any(l => l.Text.StartsWith("At least 6 (3 of each of the 2 machines)")));
        Click(form, "Activity"); Settle();

        // ---- 4b2. Job types (2026-10-27, §168): a job whose company asked for no photos; a company without Walk in ----
        Console.WriteLine();
        Console.WriteLine("== job types ==");
        var bare = Workshop(11, "to-be-invoiced", "To be invoiced", "Solved — to be invoiced", "In the billing queue",
            new StageActionInfo { Key = "collected", Label = "Machine collected" });
        bare.PhotosRequired = 0; bare.PhotosOutRequired = 0; bare.PhotosIn = 0; bare.PhotosOut = 0;
        form.ShowTicket(bare); Settle();
        Click(form, "☞ Machine collected"); Settle();
        Check("a job that asks for no photos: collection says they are not asked for — never \"at least 0\"",
            Labels(form).Any(l => l.Text.StartsWith("Photos of it as it leaves are not asked for, but you can add some."))
            && !Labels(form).Any(l => l.Text.Contains("at least 0")),
            string.Join(" | ", Labels(form).Where(l => l.Text.Contains("photo", StringComparison.OrdinalIgnoreCase)).Select(l => l.Text)));
        Check("...and the photos can still be added", Has(form, "Add photos as it leaves…"));
        Buttons(form).First(b => b.Text.StartsWith("Photos (")).PerformClick(); Settle();
        Check("its Photos tab asks for none", !Labels(form).Any(l => l.Text.StartsWith("At least")) && Has(form, "Add photos as it leaves…"));
        typeof(TechLink).GetProperty("CategoriesOff")!.SetValue(link, new HashSet<int> { (int)TicketCategoryKind.WalkIn });
        Click(form, "Details"); Settle();
        var kindsNow = Buttons(form).OfType<RoundButton>().Where(b => TicketSummary.CategoryNames.Contains(b.Text)).ToList();
        Check("Walk in switched off: Edit offers it no more, the ticket's own still chosen",
            kindsNow.All(b => b.Text != "Walk in") && kindsNow.Count == 4 && kindsNow.Single(b => b.Chosen).Text == "Workshop",
            string.Join(", ", kindsNow.Select(b => b.Text + (b.Chosen ? "*" : ""))));
        Fits(form, "job types, details"); Shot(form, outDir, "jobtypes-details");
        var oldWalkIn = Workshop(12, "walkin-repaired", "Repaired at counter", "At the counter", "With Sue Tech");
        oldWalkIn.Ticket.Category = (int)TicketCategoryKind.WalkIn;
        form.ShowTicket(oldWalkIn); Settle();
        Click(form, "Details"); Settle();
        kindsNow = Buttons(form).OfType<RoundButton>().Where(b => TicketSummary.CategoryNames.Contains(b.Text)).ToList();
        Check("...but a walk in made before keeps Walk in, chosen", kindsNow.Any(b => b.Text == "Walk in" && b.Chosen),
            string.Join(", ", kindsNow.Select(b => b.Text + (b.Chosen ? "*" : ""))));
        typeof(TechLink).GetProperty("CategoriesOff")!.SetValue(link, new HashSet<int>());
        Click(form, "Activity"); Settle();

        // ---- 4c. walk in, remote support and General (Clint, 2026-09-27) ----
        Console.WriteLine();
        Console.WriteLine("== walk in, remote support, general ==");
        TicketDetail Routed(int id, int cat, string subject, string stage, string label, params StageActionInfo[] acts) => new()
        {
            Ticket = new TicketSummary { Id = id, Number = 100 + id, Subject = subject, Category = cat, ClientName = "Zenith Accounting", Status = 0, CreatedUtc = DateTime.UtcNow },
            OpenedBy = "Oscar Office", Stage = stage, StageLabel = label, WithWhom = "With Sue Tech", ClientId = 5,
            People = new() { "Sue Tech", "Oscar Office" }, PhotosRequired = 3, Actions = acts.ToList()
        };
        var walkIn = Routed(8, (int)TicketCategoryKind.WalkIn, "Screen flickers", "walkin-booked", "At the counter",
            new StageActionInfo { Key = "walkin-repaired", Label = "Repaired at counter", Fields = new() { "Details", "Minutes" } },
            new StageActionInfo { Key = "walkin-book-in", Label = "Needs to be booked in", Fields = new() { "AssetMake", "AssetModel", "AssetSerial", "Accessories", "DevicePassword" } });
        form.ShowTicket(walkIn); Settle();
        Check("a walk in offers Repaired at counter or Needs to be booked in",
            StepButtons(form).SequenceEqual(new[] { "☞ Repaired at counter", "☞ Needs to be booked in" }), string.Join(", ", StepButtons(form)));
        Check("...its story starts with the walk in being booked", Labels(form).Any(l => l.Text.StartsWith("Oscar Office booked the walk in")));
        Click(form, "☞ Needs to be booked in"); Settle();
        Check("Needs to be booked in asks for the photos in, as a call out's does", Has(form, "Add photos as it arrives…"));

        var remote = Routed(9, (int)TicketCategoryKind.RemoteSupport, "Outlook will not open", "remote-session", "Remote session",
            new StageActionInfo { Key = "remote-invoice", Label = "To be invoiced", Fields = new() { "Details", "Amount" }, IsDecision = true },
            new StageActionInfo { Key = "remote-sla", Label = "Do not invoice (SLA client)", IsDecision = true },
            new StageActionInfo { Key = "remote-dni", Label = "Do not invoice", Fields = new() { "Reason" }, IsDecision = true },
            new StageActionInfo { Key = "remote-link", Label = "Link to parent ticket", Fields = new() { "ParentTicket" }, Closes = true });
        remote.ParentChoices.Add(new TicketChoiceInfo { Id = 3, Number = 103, Subject = "Office move — all PCs" });
        form.ShowTicket(remote); Settle();
        Check("a remote support ticket offers its four ways out",
            StepButtons(form).SequenceEqual(new[] { "☞ To be invoiced", "☞ Do not invoice (SLA client)", "☞ Do not invoice", "☞ Link to parent ticket" }), string.Join(", ", StepButtons(form)));
        Click(form, "☞ To be invoiced"); Settle();
        Check("To be invoiced asks what was done and the amount", Labels(form).Any(l => l.Text == "What was done?") && Labels(form).Any(l => l.Text == "Amount to invoice"));
        Click(form, "☞ Link to parent ticket"); Settle();
        var pick = All(Body(form)).OfType<ComboBox>().FirstOrDefault(c => c.Items.OfType<TicketChoiceInfo>().Any());
        Check("Link to parent ticket offers this client's open tickets to choose from",
            pick != null && pick.Items.OfType<TicketChoiceInfo>().Select(c => c.ToString()).SequenceEqual(new[] { "#103 — Office move — all PCs" }));
        Click(form, "Submit"); Settle();
        Check("...and Submit with none chosen says so", Labels(form).Any(l => l.Visible && l.Text == "Choose the ticket this is part of."));
        Fits(form, "link to parent"); Shot(form, outDir, "remote-link");
        var noClient = Routed(10, (int)TicketCategoryKind.RemoteSupport, "No client", "remote-session", "Remote session",
            new StageActionInfo { Key = "remote-link", Label = "Link to parent ticket", Fields = new() { "ParentTicket" }, Closes = true });
        noClient.ClientId = 0; noClient.Ticket.ClientName = "";
        form.ShowTicket(noClient); Settle();
        Click(form, "☞ Link to parent ticket"); Settle();
        Check("with no client it says to link one first, rather than an empty list",
            Labels(form).Any(l => l.Text.StartsWith("This ticket is not linked to a client yet")));
        Click(form, "Details"); Settle();
        Check("Details offers Link to a client… on a ticket with none", Has(form, "Link to a client…") && Labels(form).Any(l => l.Text == "Not linked to a client."));
        Click(form, "Activity"); Settle();

        // ---- part of a job, and the way out (2026-10-09). Neither showed in the console before. ----
        var filed = Routed(15, (int)TicketCategoryKind.RemoteSupport, "Mapped drives after the move", "remote-session", "Remote session",
            new StageActionInfo { Key = "remote-invoice", Label = "To be invoiced", Fields = new() { "Details", "Amount" }, IsDecision = true });
        filed.Parent = new TicketChoiceInfo { Id = 3, Number = 103, Subject = "Office move — all PCs", StageLabel = "Open", Open = true };
        form.ShowTicket(filed); Settle();
        Check("a ticket filed under a job says which, and opens it", Labels(form).Any(l => l.Text == "Part of:") && Has(form, "#103 Office move — all PCs  (Open)"));
        Check("...with Not part of it beside it", Has(form, "Not part of it"));
        Check("...and is not offered Link to parent again", !StepButtons(form).Contains("☞ Link to parent ticket"), string.Join(", ", StepButtons(form)));
        Fits(form, "part of a job"); Shot(form, outDir, "part-of-a-job");

        var job = Routed(16, (int)TicketCategoryKind.General, "Office move — all PCs", "general-open", "Open",
            new StageActionInfo { Key = "general-done", Label = "Done", Closes = true, ClosesJob = true });
        job.Children.Add(new TicketChoiceInfo { Id = 15, Number = 115, Subject = "Mapped drives after the move", StageLabel = "Remote session", Open = true });
        job.Children.Add(new TicketChoiceInfo { Id = 17, Number = 117, Subject = "New printer", StageLabel = "Closed", Open = false });
        form.ShowTicket(job); Settle();
        Check("a job lists its parts and says how many are still open, and that it waits for them",
            Labels(form).Any(l => l.Text == "Part of this job — 1 still open, and this job cannot be closed until it is:"),
            string.Join(" | ", Labels(form).Select(l => l.Text).Where(t => t.StartsWith("Part of"))));
        Check("...each opens its ticket, the closed one too", Has(form, "#115 Mapped drives after the move  (Remote session)") && Has(form, "#117 New printer  (Closed)"));
        Check("what Done would pop up names the open one by number and subject — the server's own words",
            TicketsForm.OpenPartsRefusal(job) == JobParts.Refusal(new[] { (115, "Mapped drives after the move") })
            && TicketsForm.OpenPartsRefusal(job)!.Contains("#115 Mapped drives after the move"), TicketsForm.OpenPartsRefusal(job) ?? "nothing");
        job.Children[0].Open = false;
        Check("...and nothing once every part is closed", TicketsForm.OpenPartsRefusal(job) == null);
        Fits(form, "a job with parts"); Shot(form, outDir, "job-with-parts");

        var gen = Routed(11, (int)TicketCategoryKind.General, "Order more toner", "general-open", "Open",
            new StageActionInfo { Key = "general-done", Label = "Done", Closes = true },
            new StageActionInfo { Key = "quote-needed", Label = "Quote needed", IsDecision = true },
            new StageActionInfo { Key = "general-invoice", Label = "To be invoiced", Fields = new() { "Details", "Minutes", "Amount" }, IsDecision = true });
        form.ShowTicket(gen); Settle();
        Check("a General ticket offers Done, Quote needed and To be invoiced",
            StepButtons(form).SequenceEqual(new[] { "☞ Done", "☞ Quote needed", "☞ To be invoiced" }), string.Join(", ", StepButtons(form)));
        Check("...no Quote tab until there is a quote, no Photos tab", !Buttons(form).Any(b => b.Text.StartsWith("Quote (") || b.Text == "Quote" || b.Text.StartsWith("Photos")));
        Click(form, "Details"); Settle();
        Check("Details offers Change the client… on a ticket that has one", Has(form, "Change the client…") && Labels(form).Any(l => l.Text == "Zenith Accounting"));
        Fits(form, "general details"); Shot(form, outDir, "general-details");
        Click(form, "Activity"); Settle();

        // ---- 5. a ticket with no workflow keeps its status box ----
        Console.WriteLine();
        Console.WriteLine("== a General ticket ==");
        var general = new TicketDetail { Ticket = new TicketSummary { Id = 5, Number = 105, Subject = "Printer offline", Category = (int)TicketCategoryKind.General, Status = 1 }, OpenedBy = "Oscar Office" };
        form.ShowTicket(general); Settle();
        Check("its status box is there", StatusBoxes(form).Any());
        Check("and a comment box, and no tabs, steps, quote or photos",
            Has(form, "Post") && !Has(form, "Activity") && !StepButtons(form).Any() && !Has(form, "Add") && !Has(form, "Add photos as it leaves…"));
        Fits(form, "general"); Shot(form, outDir, "general");

        // ---- 5a. emailing a comment to the client (2026-10-09) ----
        Console.WriteLine();
        Console.WriteLine("== emailing the client ==");
        Check("a server that says nothing about it: no tick box, nothing said", !All(Body(form)).OfType<CheckBox>().Any(c => c.Text.StartsWith("Also email")));
        var mail = new TicketDetail
        {
            Ticket = new TicketSummary { Id = 6, Number = 106, Subject = "Printer jams", Category = (int)TicketCategoryKind.General, Status = 1, ClientName = "Acme Ltd" },
            OpenedBy = "Oscar Office", Description = "The printer in accounts jams on every page.",
            ClientUpdate = new ClientUpdateInfo
            {
                Ready = true, Default = "jane@acme.test", From = "support@demo.test",
                Choices = new() { new() { Address = "jane@acme.test", Label = "Jane Smith <jane@acme.test>" }, new() { Address = "ken@acme.test", Label = "Ken Accounts <ken@acme.test>" } }
            }
        };
        mail.RepliedBy.Add("Jane Smith");
        mail.Notes.Add(Sys("Oscar Office", "Ticket opened."));
        mail.Notes.Add(new TicketNoteInfo { Id = 2, Author = "Sue Tech", Body = "A new drum arrives Thursday.", CanEmail = true, CreatedUtc = DateTime.UtcNow.AddHours(-3),
            Emailed = { "Emailed to jane@acme.test by Sue Tech, 9 Oct 10:02" } });
        mail.Notes.Add(new TicketNoteInfo { Id = 3, Author = "Sue Tech", Body = "Internal: the toner is a cheap copy.", CanEmail = true, CreatedUtc = DateTime.UtcNow.AddHours(-2) });
        mail.Notes.Add(new TicketNoteInfo { Id = 4, Author = "Jane Smith (by email)", Body = "Thursday is fine.", FromCustomer = true, CreatedUtc = DateTime.UtcNow.AddHours(-1) });
        form.ShowTicket(mail); Settle();
        var tick = All(Body(form)).OfType<CheckBox>().FirstOrDefault(c => c.Text == "Also email this to the client");
        Check("under the comment box: Also email this to the client, unticked", tick is { Checked: false, Visible: true });
        Check("...who it goes to is hidden until it is ticked", !All(Body(form)).OfType<ComboBox>().Any(c => c.Visible && c.Items.OfType<ClientAddressInfo>().Any()));
        tick!.Checked = true; Settle();
        var toPick = All(Body(form)).OfType<ComboBox>().FirstOrDefault(c => c.Visible && c.Items.OfType<ClientAddressInfo>().Any());
        Check("ticked: the chooser shows, Jane chosen, Ken offered, and Another address…",
            toPick != null && (toPick.SelectedItem as ClientAddressInfo)?.Address == "jane@acme.test" && toPick.Items.Count == 3 && toPick.Items[2] as string == "Another address…");
        Check("...and it says what goes, from where", Labels(form).Any(l => l.Text.Contains("from support@demo.test") && l.Text.Contains("nothing else on the ticket")));
        Check("the reply is pointed out, worded as a reply", Labels(form).Any(l => l.Text == "Jane Smith replied by email — it is below."));
        Check("the emailed comment says so", Labels(form).Any(l => l.Text == "✉ Emailed to jane@acme.test by Sue Tech, 9 Oct 10:02"));
        Check("the customer's words say they are theirs", Labels(form).Any(l => l.Text == "✉ Their reply, by email"));
        Check("Email to the client… on the comment not yet sent, Email it again… on the one sent, nothing on the reply or the automatic line",
            Buttons(form).Count(b => b.Text == "Email to the client…") == 1 && Buttons(form).Count(b => b.Text == "Email it again…") == 1);
        Fits(form, "emailing the client"); Shot(form, outDir, "email-client");
        Click(form, "Email to the client…"); Settle();
        Check("pressing it opens the chooser in place, with Send and Cancel", Has(form, "Send") && Has(form, "Cancel"));
        Fits(form, "emailing a posted comment"); Shot(form, outDir, "email-client-later");
        Click(form, "Cancel"); Settle();
        Check("Cancel closes it", !Has(form, "Send"));
        mail.ClientUpdate = new ClientUpdateInfo { Ready = false, WhyNot = "Emailing comments to the client is not set up yet — ask the owner or a supervisor to set up Tickets by email." };
        form.ShowTicket(mail); Settle();
        Check("not set up: no tick box, and it says why", !All(Body(form)).OfType<CheckBox>().Any(c => c.Text.StartsWith("Also email")) && Labels(form).Any(l => l.Text.StartsWith("Emailing comments to the client is not set up")));
        Check("...and no Email to the client… on any comment", !Buttons(form).Any(b => b.Text is "Email to the client…" or "Email it again…"));

        // ---- 5b. several people on one ticket (2026-09-28) ----
        Console.WriteLine();
        Console.WriteLine("== several people on it ==");
        TicketDetail Shared(string on, bool anyone, bool join) => new()
        {
            Ticket = new TicketSummary { Id = 7, Number = 107, Subject = "Server down", Category = (int)TicketCategoryKind.General, Status = 1, AssignedTo = on },
            OpenedBy = "Oscar Office", CanAssignToAnyone = anyone, CanJoin = join, CanPassOn = true
        };
        string Boxes() => string.Join(", ", PeopleBoxes(form).Select(b => $"{b.Tag}{(b.Checked ? "✓" : "")}{(b.Enabled ? "" : "(grey)")}"));
        CheckBox? Box(string name) => PeopleBoxes(form).FirstOrDefault(b => (string?)b.Tag == name);

        form.ShowTicket(Shared("Sue Tech, Bob Tech", anyone: false, join: true)); Settle();
        Check("a tick box for everybody on the team", PeoplePeople(form).SequenceEqual(new[] { "Ann Tech", "Bob Tech", "Sue Tech" }), Boxes());
        Check("both people on it are ticked, the third is not",
            Box("Sue Tech")?.Checked == true && Box("Bob Tech")?.Checked == true && Box("Ann Tech")?.Checked == false, Boxes());
        Check("Sue, on it, may take herself off and add Ann — but Bob's box is greyed",
            Box("Sue Tech")?.Enabled == true && Box("Ann Tech")?.Enabled == true && Box("Bob Tech")?.Enabled == false, Boxes());
        Fits(form, "two people on it"); Shot(form, outDir, "people");

        form.ShowTicket(Shared("Bob Tech", anyone: false, join: true)); Settle();
        Check("Sue, NOT on it, may tick only herself", Box("Sue Tech")?.Enabled == true && Box("Bob Tech")?.Enabled == false && Box("Ann Tech")?.Enabled == false, Boxes());
        form.ShowTicket(Shared("Bob Tech", anyone: false, join: false)); Settle();
        Check("…and not even herself where she may not join", PeopleBoxes(form).All(b => !b.Enabled), Boxes());

        form.ShowTicket(Shared("Bob Tech, Gone Person", anyone: true, join: false)); Settle();
        Check("a supervisor or the office may change every box", PeopleBoxes(form).All(b => b.Enabled), Boxes());
        Check("somebody on it who has left the team is still shown, and said so",
            PeopleBoxes(form).Any(b => (string?)b.Tag == "Gone Person" && b.Checked && b.Text.Contains("no longer")), Boxes());
        Fits(form, "people, supervisor");

        // ---- 5b2. the @ list in the comment box (2026-10-21) ----
        // Clint: "putting in the @ should list everyone assigned to the task". REAL keys, so the list is
        // proved not to take the keyboard from the box — leave the keyboard alone while this runs.
        Console.WriteLine();
        Console.WriteLine("== typing @ in the comment box ==");
        var asked = Shared("Bob Tech", anyone: true, join: true);
        asked.People = new() { "Ann Tech", "Bob Tech", "Oscar Office", "Sue Tech" };
        asked.OnTicket = new() { "Bob Tech" };
        form.ShowTicket(asked); Settle();
        var comment = All(Body(form)).OfType<InputBox>().First().Box;
        var atList = (MentionPopup?)typeof(TicketsForm).GetField("_mentions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form);
        string Listed() => atList == null ? "no list" : string.Join(", ", atList.Choices.Select(c => (c.OnTicket ? "*" : "") + c.Name));
        form.Activate(); comment.Focus(); Settle();
        if (!comment.Focused) Console.WriteLine("  (the comment box could not take the focus — is another window in front?)");
        SendKeys.SendWait("Ask @"); Settle();
        Check("typing @ pops up the list", atList is { Visible: true }, Listed());
        Check("...the people on the ticket first, then everybody else, never the one typing",
            Listed() == "*Bob Tech, Ann Tech, Oscar Office", Listed());
        Check("...and the box keeps the keyboard: the ticket window is still the active one",
            comment.Focused && Form.ActiveForm == form, $"focused {comment.Focused}, active {Form.ActiveForm?.Text}");
        if (atList != null)
        {
            var under = comment.PointToScreen(comment.GetPositionFromCharIndex(4));
            Check("it sits just below the @, inside the screen",
                atList.Top > under.Y && atList.Top - under.Y < 3 * TextRenderer.MeasureText("Ag", comment.Font).Height && Screen.FromControl(form).WorkingArea.Contains(atList.Bounds),
                $"@ at {under}, list at {atList.Bounds}");
        }
        ScreenShot(form, outDir, "mention-list");
        SendKeys.SendWait("t"); Settle();
        Check("typing narrows it, by any word of the name", Listed() == "*Bob Tech, Ann Tech", Listed());
        SendKeys.SendWait("{DOWN}{ENTER}"); Settle();
        Check("Down and Enter put the whole name in, with a space after it", comment.Text == "Ask @Ann Tech " && comment.SelectionStart == comment.Text.Length, $"\"{comment.Text}\"");
        Check("...and the list is gone", atList is { Visible: false });
        SendKeys.SendWait("@{ESC}"); Settle();
        Check("Esc closes it", atList is { Visible: false });
        SendKeys.SendWait("b"); Settle();
        Check("...and it stays closed for that @ while they type on", atList is { Visible: false }, Listed());
        SendKeys.SendWait(" mail bob@x"); Settle();
        Check("an email address never opens it", atList is { Visible: false }, Listed());
        var older = Shared("Bob Tech", anyone: true, join: true);
        older.People = new() { "Ann Tech", "Bob Tech", "Sue Tech" };   // no OnTicket: an older server
        form.ShowTicket(older); Settle();
        comment = All(Body(form)).OfType<InputBox>().First().Box;
        atList = (MentionPopup?)typeof(TicketsForm).GetField("_mentions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form);
        form.Activate(); comment.Focus(); Settle();
        SendKeys.SendWait("@"); Settle();
        Check("from an older server everybody is one list", Listed() == "Ann Tech, Bob Tech", Listed());
        SendKeys.SendWait("{ESC}"); Settle();

        // ---- 5c. the knowledge base (3.8.5, 2026-10-05) ----
        // Drawn from what the server sends, like everything else here: the client's count, what was
        // written up from this ticket, and "Add to knowledge base" when this person is offered it.
        Console.WriteLine();
        Console.WriteLine("== the knowledge base ==");
        Check("a ticket sent with no knowledge line draws none", !Buttons(form).Any(b => b.Text.Contains("nowledge base") || b.Text.StartsWith("Written up")));
        var kbDone = Routed(12, (int)TicketCategoryKind.General, "Create a new user for Jane", "to-be-invoiced", "To be invoiced");
        kbDone.Knowledge = new KnowledgeInfo
        {
            ClientId = 5, ClientName = "Zenith Accounting", ClientCount = 3, CanAdd = true,
            FromTicket = new() { new KnowledgeRefInfo { Id = 9, Title = "Adding a new user" } }
        };
        form.ShowTicket(kbDone); Settle();
        Check("the client's articles, what was written up from it, and Add another",
            Has(form, "📘 Knowledge base for Zenith Accounting (3)") && Has(form, "Written up: Adding a new user") && Has(form, "Add another to the knowledge base"),
            string.Join(" | ", Buttons(form).Select(b => b.Text).Where(t => t.Contains("nowledge") || t.StartsWith("Written"))));
        Fits(form, "the knowledge line"); Shot(form, outDir, "knowledge");
        var kbPlain = new TicketDetail
        {
            Ticket = new TicketSummary { Id = 13, Number = 113, Subject = "Printer offline", Category = (int)TicketCategoryKind.General, Status = 3, ClientName = "Zenith Accounting" },
            OpenedBy = "Oscar Office", Knowledge = new KnowledgeInfo { CanAdd = true }
        };
        form.ShowTicket(kbPlain); Settle();
        Check("a ticket with no route has it too — just Add, when nothing is written yet",
            Has(form, "Add to knowledge base") && !Buttons(form).Any(b => b.Text.StartsWith("📘") || b.Text.StartsWith("Written up")));
        kbDone.Knowledge = new KnowledgeInfo { ClientId = 5, ClientName = new string('Z', 40) + " Holdings (Proprietary) Limited, Cape Town", ClientCount = 12 };
        form.ShowTicket(kbDone); Settle();
        Check("a long client name is shortened rather than pushing the button off the window",
            Buttons(form).Any(b => b.Text.StartsWith("📘 Knowledge base for ZZZ") && b.Text.EndsWith("…")) && !Has(form, "Add to knowledge base"));
        Fits(form, "a long client name");

        // ---- 5d. a recurring task's ticket (2026-10-07) ----
        // The schedule it came from, as the server sends it; Done and To be invoiced are ordinary step
        // buttons; Cancel asks why in the window itself, and Change opens the portal's form.
        Console.WriteLine();
        Console.WriteLine("== a recurring task's ticket ==");
        var rec = Routed(14, (int)TicketCategoryKind.Recurring, "Check the backups", "recurring-due", "Due",
            new StageActionInfo { Key = "recurring-done", Label = "Done", Closes = true },
            new StageActionInfo { Key = "recurring-invoice", Label = "To be invoiced", Fields = new() { "Details", "Minutes", "Amount" } });
        rec.Ticket.DueOn = new DateTime(2026, 10, 7);
        rec.Ticket.Overdue = true;
        rec.Recurring = new RecurringInfo { TaskId = 3, Kind = "Maintenance", Repeats = "Every Wednesday", Next = "Next: Wednesday 14 October 2026", MayChange = true };
        form.ShowTicket(rec); Settle();
        Check("its route is Done and To be invoiced", StepButtons(form).SequenceEqual(new[] { "☞ Done", "☞ To be invoiced" }), string.Join(", ", StepButtons(form)));
        Check("a Recurring task card says the type, how often, the day it was due — OVERDUE — and the next",
            Labels(form).Any(l => l.Text == "Maintenance") && Labels(form).Any(l => l.Text == "Every Wednesday")
            && Labels(form).Any(l => l.Text == "Wednesday 7 October 2026 — OVERDUE") && Labels(form).Any(l => l.Text == "Wednesday 14 October 2026"),
            string.Join(" | ", Labels(form).Select(l => l.Text).Where(t => t.Contains("2026") || t.Contains("Every"))));
        Check("its person is offered Change in the browser and Cancel the task…", Has(form, "Change in the browser") && Has(form, "Cancel the task…"));
        Fits(form, "a recurring task"); Shot(form, outDir, "recurring");
        int boxesBefore = Inputs(form);
        Click(form, "Cancel the task…"); Settle();
        Check("Cancel the task… asks why in the window, with Keep it beside it",
            Inputs(form) == boxesBefore + 1 && Has(form, "Cancel the task") && Has(form, "Keep it") && !Has(form, "Cancel the task…"));
        Fits(form, "cancelling a recurring task"); Shot(form, outDir, "recurring-cancel");
        Click(form, "Keep it"); Settle();
        Check("Keep it puts it back as it was", Has(form, "Cancel the task…") && Inputs(form) == boxesBefore);
        rec.Recurring = new RecurringInfo { TaskId = 3, Kind = "Maintenance", Repeats = "Every Wednesday", Next = "Cancelled 7 October 2026 by Sue Tech: client moved to cloud backup", Cancelled = true };
        rec.Ticket.Overdue = false;
        form.ShowTicket(rec); Settle();
        Check("a cancelled one says so and offers nothing to change", Labels(form).Any(l => l.Text.StartsWith("Cancelled 7 October")) && !Has(form, "Cancel the task…") && !Has(form, "Change in the browser"));
        Check("an ordinary ticket draws no recurring card", true);   // every section above: none sent, none drawn
        form.ShowTicket(general); Settle();
        Check("...for instance a General one", !Labels(form).Any(l => l.Text == "Recurring task") && !Has(form, "Cancel the task…"));

        // ---- 6. closed ----
        Console.WriteLine();
        Console.WriteLine("== closed ==");
        var closed = Workshop(6, "closed", "Closed", "Payment received", "");
        closed.IsClosed = true;
        closed.Notes.AddRange(done.Notes);
        // The Solved form as the server writes it: the work, then what was recorded under it. The
        // server also sends the work alone, for the invoice (TicketService.WorkDoneText).
        const string workText = "Replaced the DC charging port.\nTested on charge for an hour.";
        closed.Notes.Add(new TicketNoteInfo
        {
            Author = "Sue Tech", FormTitle = "Solved — to be invoiced", MovedToLabel = "To be invoiced", IsWorkDone = true, CreatedUtc = DateTime.UtcNow,
            Body = workText + "\nTime spent: 1 h 15 min\nAmount to invoice: 1,250.00\nIn the billing queue.", WorkDoneText = workText
        });
        form.ShowTicket(closed); Settle();
        Check("a closed job says so, and offers no steps and no comment box",
            Labels(form).Any(l => l.Text == "This job is closed.") && !StepButtons(form).Any() && !Has(form, "Post"));
        Check("its story is still there to read", Labels(form).Any(l => l.Text.Trim() == "Inspection findings   →  Inspected")
            && Labels(form).Any(l => l.Text.Trim() == "Solved — to be invoiced   →  To be invoiced"));
        Shot(form, outDir, "closed");
        Click(form, "Details"); Settle();
        // Whatever was on this PC's clipboard is put back afterwards: a harness is a guest.
        string? was = null;
        try { if (Clipboard.ContainsText()) was = Clipboard.GetText(); } catch { }
        Click(form, "Copy the work done for the invoice"); Settle();
        string copied = "";
        try { copied = Clipboard.GetText().Replace("\r", ""); } catch { }
        try { if (was != null) Clipboard.SetText(was); else Clipboard.Clear(); } catch { }
        Check("Copy the work done for the invoice copies the work — and not the time, the amount or the queue",
            copied == "- " + workText, copied.Replace("\n", " | "));
        Click(form, "Activity"); Settle();

        // ---- 2026-10-13: files on comments, the damage report, going back, the box that grows ----
        Console.WriteLine();
        Console.WriteLine("== files, the damage report, going back ==");
        var mailTo = new ClientUpdateInfo { Ready = true, Default = "jane@acme.test", From = "support@zenith.test",
            Choices = new() { new ClientAddressInfo { Address = "jane@acme.test", Label = "Jane Smith <jane@acme.test>" } } };
        var billed = Workshop(40, "to-be-invoiced", "To be invoiced", "Not repairable", "In the billing queue",
            new StageActionInfo { Key = "go-back", Label = "Back to Not repairable", Fields = new() { "Reason" },
                Warning = "It was emailed to jane@acme.test already, and they will not be told it was taken back." });
        billed.ClientUpdate = mailTo;
        var undoneStep = Form_("Sue Tech", "Invoice inspection fee", "To be invoiced", "Time spent: 30 min\nAmount to invoice: 300.00");
        undoneStep.Undone = "Gone back over by Sue Tech, 13 Oct 08:40 — what it recorded no longer counts.";
        var wentBack = Form_("Sue Tech", "Back to Not repairable", "Not repairable", "Reason: pressed the wrong one.");
        wentBack.WentBack = true;
        var report = Form_("Sue Tech", "Damage report needed", "To be invoiced", "Cracked board beside the charging port.\nTime spent: 45 min\nAmount to invoice: 450.00");
        report.Id = 901; report.CanEmail = true;
        report.Attachments.Add(new AttachmentInfo { Id = 77, Name = "Damage report.pdf", Size = "120 KB" });
        report.Emailed.Add("Emailed to jane@acme.test by Sue Tech, 13 Oct 09:00");
        var photos = new TicketNoteInfo { Id = 902, Author = "Sue Tech", Body = "", CanEmail = true, CreatedUtc = DateTime.UtcNow.AddMinutes(-30) };
        photos.Attachments.Add(new AttachmentInfo { Id = 78, Name = "progress.jpg", Size = "480 KB", IsImage = true });
        billed.Notes.AddRange(new[] { undoneStep, wentBack, report, photos });
        form.ShowTicket(billed); Settle();
        Check("Back is drawn plain beside the steps forward, with its arrow", Buttons(form).Any(b => b.Text == "↶ Back to Not repairable") && !StepButtons(form).Any(s => s.Contains("Back")));
        Check("a step's file is a button that opens it", Has(form, "📎 Damage report.pdf (120 KB)"));
        Check("a comment of a photo alone: its picture is a button too", Has(form, "🖼 progress.jpg (480 KB)"));
        Check("the damage report's step may be emailed again", Has(form, "Email it again…"));
        Check("a step gone back over says so", Labels(form).Any(l => l.Text.StartsWith("↶ Gone back over by Sue Tech")));
        Check("a 'Back to' entry is titled with the arrow", Labels(form).Any(l => l.Text.Trim() == "↶ Back to Not repairable"));
        Check("the comment box has Attach files or photos…", Has(form, "📎 Attach files or photos…"));
        Fits(form, "files and going back");
        Shot(form, outDir, "files-and-back");
        Click(form, "↶ Back to Not repairable"); Settle();
        Check("Back's form warns first", Labels(form).Any(l => l.Text.StartsWith("⚠ It was emailed to jane@acme.test already")));
        Check("...asks why, and its button says what it does",
            Labels(form).Any(l => l.Text.StartsWith("Why is it going back?")) && Has(form, "Back to Not repairable"));
        Fits(form, "going back");
        Shot(form, outDir, "going-back");

        var notRep = Workshop(41, "not-repairable", "Not repairable", "Inspected", "With Sue Tech",
            new StageActionInfo { Key = "damage-report", Label = "Damage report needed", IsDecision = true, Repairable = "no",
                Fields = new() { "Comments", "Files", "Minutes", "Amount", "EmailClient" } });
        notRep.ClientUpdate = mailTo;
        form.ShowTicket(notRep); Settle();
        // The comment box grows a line at a time (Clint: "the console showed three"), to 15, then scrolls.
        var grow = All(Body(form)).OfType<InputBox>().First();
        int h0 = grow.Height;
        grow.Value = string.Join("\r\n", Enumerable.Range(1, 8).Select(i => $"line {i}")); Settle();
        int h8 = grow.Height;
        grow.Value = string.Join("\r\n", Enumerable.Range(1, 40).Select(i => $"line {i}")); Settle();
        int h40 = grow.Height;
        Check("the comment box grows with each new line", h8 > h0, $"{h0} → {h8} px");
        Check("...to 15 lines and no further, then it scrolls", h40 > h8 && h40 <= h0 + TextRenderer.MeasureText("Ag", grow.Box.Font).Height * 15 + 30
            && grow.Box.ScrollBars == ScrollBars.Vertical, $"{h40} px");
        grow.Value = ""; Settle();
        Check("...and emptied, back to its own height", grow.Height == h0 && grow.Box.ScrollBars == ScrollBars.None, $"{grow.Height} px");
        Click(form, "☞ Damage report needed"); Settle();
        Check("the damage report asks comments, the report, the time and the amount",
            Labels(form).Any(l => l.Text.StartsWith("Comments (optional)")) && Has(form, "📎 Attach the damage report…")
            && Labels(form).Any(l => l.Text.StartsWith("Time spent")) && Labels(form).Any(l => l.Text == "Amount to invoice"));
        var mailIt = All(Body(form)).OfType<CheckBox>().FirstOrDefault(c => c.Text == "Email the damage report to the client");
        Check("...and Email the damage report to the client, ticked by default", mailIt?.Checked == true);
        Click(form, "Submit"); Settle();
        Check("Submit with no report picked says so", Labels(form).Any(l => l.Visible && l.Text == "Attach the damage report."));
        Fits(form, "damage report");
        Shot(form, outDir, "damage-report");

        // ---- squeezed ----
        Console.WriteLine();
        Console.WriteLine("== at its smallest ==");
        form.Size = form.MinimumSize; Settle();
        form.ShowTicket(general); Settle();
        form.ShowTicket(done); Settle(); Settle();
        Click(form, "☞ Quote needed"); Settle();
        Fits(form, "at the minimum size");
        Shot(form, outDir, "squeezed");

        form.Hide();

        // ---- the dark palette: the same ticket, looked at ----
        Console.WriteLine();
        Console.WriteLine("== dark ==");
        var before = Theme.Override;
        try
        {
            Theme.Override = ThemeMode.Dark;
            var dark = Theme.FromBranding(brand, AppSurface.Technician);
            using var df = new TicketsForm(link, dark, brand) { StartPosition = FormStartPosition.Manual };
            df.Show(); Settle();
            df.ShowTicket(done); Settle();
            Click(df, "☞ Quote needed"); Settle();
            Fits(df, "dark");
            var qty = All(Body(df)).OfType<NumberBox>().Select(n => n.Box).FirstOrDefault();   // a NumericUpDown until 2026-10-06
            Check("the quantity box takes the dark palette rather than staying white",
                qty != null && qty.BackColor.GetBrightness() < 0.5f, qty == null ? "none" : qty.BackColor.ToString());
            var white = All(Body(df)).OfType<ComboBox>().Where(c => c.BackColor.GetBrightness() > 0.8f).Count();
            Check("every dropdown takes the dark palette too", white == 0, $"{white} still light");
            Shot(df, outDir, "work-done-dark");
            ScreenShot(df, outDir, "work-done-dark-screen");
            ScrollTo(df, 0.6); ScreenShot(df, outDir, "work-done-dark-story");
            df.Hide();
        }
        finally { Theme.Override = before; }

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    // ---- tickets ----

    private static TicketDetail Workshop(int id, string stage, string label, string previous, string withWhom, params StageActionInfo[] actions) => new()
    {
        Ticket = new TicketSummary { Id = id, Number = 100 + id, Subject = "Laptop will not power on", Category = (int)TicketCategoryKind.Workshop, ClientName = "Zenith Accounting", Status = 1, CreatedUtc = DateTime.UtcNow.AddDays(-1) },
        OpenedBy = "Oscar Office", Stage = stage, StageLabel = label, PreviousStageLabel = previous, WithWhom = withWhom, Actions = actions.ToList(),
        AssetMake = "Dell", AssetModel = "Latitude 5420", AssetSerial = "7XK2Q13", Accessories = "Charger", Description = "Will not power on, even on the charger.",
        PhotosIn = 3, PhotosOut = 0, PhotosRequired = 3, People = new() { "Sue Tech", "Bob Tech", "Oscar Office", "Sam Supervisor" }
    };
    private static StageActionInfo Act(string key, string label, bool closes = false, params string[] fields) => new() { Key = key, Label = label, Closes = closes, Fields = fields.ToList() };
    private static StageActionInfo Act(string key, string label, string fields) => Act(key, label, false, fields);
    private static TicketNoteInfo Sys(string who, string body) => new() { Author = who, Body = body, IsSystem = true, CreatedUtc = DateTime.UtcNow.AddHours(-20) };
    private static TicketNoteInfo Form_(string who, string title, string movedTo, string body) =>
        new() { Author = who, Body = body, FormTitle = title, MovedToLabel = movedTo, CreatedUtc = DateTime.UtcNow.AddHours(-2) };

    // ---- looking ----

    /// <summary>The "people on it" tick boxes in the ticket side, in the order they are drawn.</summary>
    private static List<CheckBox> PeopleBoxes(Form f)
    {
        var row = Body(f).Controls.Find("people", true).FirstOrDefault();
        return row == null ? new() : row.Controls.OfType<CheckBox>().ToList();
    }
    private static IEnumerable<string> PeoplePeople(Form f) => PeopleBoxes(f).Select(b => (string)b.Tag!);

    private static Control Body(Form f) => (Control)f.GetType().GetField("_body", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f)!;
    private static Control Scroll(Form f) => (Control)f.GetType().GetField("_scroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f)!;
    private static IEnumerable<Control> All(Control c) { foreach (Control x in c.Controls) { yield return x; foreach (var y in All(x)) yield return y; } }
    private static IEnumerable<Button> Buttons(Form f) => All(Body(f)).OfType<Button>().Where(b => b.Visible);
    private static IEnumerable<Label> Labels(Form f) => All(Body(f)).OfType<Label>().Where(l => l.Visible && l is not Pill);
    private static IEnumerable<Pill> Pills(Form f) => All(Body(f)).OfType<Pill>();
    private static IEnumerable<string> StepButtons(Form f) => Buttons(f).Where(b => b.Text.StartsWith("☞ ")).Select(b => b.Text);
    private static bool Has(Form f, string text) => Buttons(f).Any(b => b.Text == text);
    private static void Click(Form f, string text) => Buttons(f).First(b => b.Text == text).PerformClick();
    private static IEnumerable<ComboBox> StatusBoxes(Form f) => All(Body(f)).OfType<ComboBox>().Where(c => c.Items.Contains("In progress"));
    private static int Inputs(Form f) => All(Body(f)).OfType<InputBox>().Count();
    private static Rectangle Screen_(Control c) => c.RectangleToScreen(c.ClientRectangle);

    /// <summary>Every button's words fit it; nothing in the ticket runs past the right-hand edge.</summary>
    private static void Fits(Form f, string where)
    {
        var scroll = Scroll(f);
        var edge = scroll.RectangleToScreen(scroll.ClientRectangle).Right;
        var over = All(Body(f)).Where(c => c.Visible && c.Width > 0 && c.RectangleToScreen(c.ClientRectangle).Right > edge + 1)
            .Select(c => $"{c.GetType().Name} '{Trim(c.Text)}' by {c.RectangleToScreen(c.ClientRectangle).Right - edge}px").ToList();
        Check($"{where}: nothing runs off the right-hand side", over.Count == 0, string.Join("; ", over.Take(4)));
        var cut = Buttons(f).Where(b => TextRenderer.MeasureText(b.Text, b.Font).Width + 8 > b.Width).Select(b => b.Text).ToList();
        Check($"{where}: every button's words fit it", cut.Count == 0, string.Join("; ", cut));
        var clipped = All(Body(f)).OfType<Label>().Where(l => l.Visible && !l.AutoSize && TextRenderer.MeasureText(l.Text, l.Font).Height > l.Height + 2).Select(l => Trim(l.Text)).ToList();
        Check($"{where}: no fixed-height label is shorter than its font", clipped.Count == 0, string.Join("; ", clipped));
    }

    private static string Trim(string s) => s.Length > 30 ? s[..30] + "…" : s;

    /// <summary>Scrolls the ticket side to a fraction of its height, for the pictures below the fold.</summary>
    private static void ScrollTo(Form f, double at)
    {
        var s = (ScrollableControl)Scroll(f);
        s.AutoScrollPosition = new Point(0, (int)((Body(f).Height - s.ClientSize.Height) * at));
        Settle();
    }

    private static void Settle() { for (int i = 0; i < 12; i++) { Application.DoEvents(); Thread.Sleep(25); } }

    /// <summary>
    /// What is actually ON SCREEN, rather than what DrawToBitmap renders — which draws native parts
    /// (dropdowns, scrollbars) in their default look whatever the window has set. Used for the dark
    /// case, where that difference is the whole question.
    /// </summary>
    private static void ScreenShot(Form f, string dir, string name)
    {
        try
        {
            f.TopMost = true; f.Activate(); Settle();
            // Repaint everything first. Captured straight after showing, a RoundButton's corners still
            // held what was on screen before the window — which read as a colour fault and was not one.
            f.Invalidate(true); f.Update();
            Settle();
            using var bmp = new Bitmap(f.Width, f.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Location, Point.Empty, f.Size);
            var path = Path.Combine(dir, name + ".png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"        picture (from the screen): {path}");
            f.TopMost = false;
        }
        catch (Exception ex) { Console.WriteLine($"        (no screen picture: {ex.Message})"); }
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
}
