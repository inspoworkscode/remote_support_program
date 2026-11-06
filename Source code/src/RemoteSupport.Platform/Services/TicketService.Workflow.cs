using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>A button a person may press right now, and what it needs typed in first.</summary>
/// <param name="Warning">What to read before pressing it — Back's "the invoice has gone out" (2026-10-13). Usually empty.</param>
public sealed record AvailableAction(string Key, string Label, WorkLane Performer, StageField[] Fields,
    bool IsDecision, bool Closes, string Warning = "");

/// <summary>
/// The workflow half of the ticket service (stage 3.7). <see cref="ActAsync"/> is the ONLY thing
/// that moves a stage; <see cref="UpdateAsync"/> refuses a raw status change on a ticket that has
/// a workflow, so the old status dropdown — in a stale tab or an old console — cannot walk around
/// a gate. One enforcement point, as the brief asks: both live in this class.
/// </summary>
public sealed partial class TicketService
{
    /// <summary>
    /// Every button this person may press on this ticket now. Both UIs draw exactly this list; the
    /// service then re-checks everything when one is pressed, so a list drawn a minute ago that is
    /// now wrong is refused rather than obeyed.
    /// </summary>
    /// <param name="back">The step "Back" would undo, from <see cref="BackStepAsync"/> — which has already asked whether
    /// this person may press it. Null offers no Back (2026-10-13), which is what every caller that cannot read the
    /// ticket's history passes.</param>
    public static List<AvailableAction> ActionsFor(Ticket t, TenantRole role, string me, BackStep? back = null)
    {
        var list = new List<AvailableAction>();
        if (!Workflow.Has(t.Category) || t.Stage.Length == 0) return list;
        var stage = Workflow.Stage(t.Stage);
        if (stage == null) return list;
        // Back: after the steps forward, ahead of Abandoned — and the one thing offered on a closed job.
        var backButton = back == null ? null
            : new AvailableAction(GoBackKey, $"Back to {back.ToLabel}", WorkLane.Bench, new[] { StageField.Reason }, false, false, back.Warning);
        if (stage.Lane == WorkLane.Done)
        {
            if (backButton != null) list.Add(backButton);
            return list;
        }

        foreach (var a in RouteSteps(t))
        {
            if (!Workflow.MayPress(a, t, role, me)) continue;
            list.Add(new AvailableAction(a.Key, a.Label, a.Performer, a.Fields, a.IsDecision, a.Closes));
        }
        // Only a workshop machine is here to be collected; a call out's stages share MachineReady with it.
        // It leaves one of two ways: the customer takes it, or a technician takes it to them (2026-09-30) —
        // unless the company has switched delivery off (Job types).
        if (stage.MachineReady && t.Category == TicketCategory.Workshop && t.CollectedUtc == null && Workflow.MayMarkCollected(t, role, me))
        {
            list.Add(new AvailableAction(CollectedKey, "Machine collected", WorkLane.Office, Array.Empty<StageField>(), false, false));
            if (JobTypes.On(t.RouteOff, JobTypes.Delivery))
                list.Add(new AvailableAction(DeliverKey, "Out for delivery", WorkLane.Office, Array.Empty<StageField>(), false, false));
        }
        // Several machines brought in at once (2026-09-30): at the start of the job, whoever may
        // book one in may add another beside it — the office at the counter as much as the bench.
        // Ahead of Abandoned, which is always drawn last.
        if (MayAddMachine(t, role, me))
        {
            int abandon = list.FindIndex(a => a.Key == "abandon");
            list.Insert(abandon < 0 ? list.Count : abandon,
                new AvailableAction(AddMachineKey, "Add another machine", WorkLane.Bench, AddMachineFields, false, false));
        }
        if (backButton != null)
        {
            int abandon = list.FindIndex(a => a.Key == "abandon");
            list.Insert(abandon < 0 ? list.Count : abandon, backButton);
        }
        return list;
    }

    /// <summary>
    /// The steps the route offers from where this ticket is, before asking who may press them: from this stage (or one a
    /// skipped step leaves it to answer), for this category, not switched off in the ticket's own copy of the company's
    /// Job types, and not ruled out by the ticket itself. <see cref="ActionsFor"/>, <see cref="ActAsync"/> and
    /// <see cref="JobTypes.ProblemFor"/> all ask this, so the buttons, the refusal and the route check cannot disagree.
    /// </summary>
    public static IEnumerable<ActionDef> RouteSteps(Ticket t) =>
        Workflow.Actions.Where(a => JobTypes.FromHere(a, t.Stage, t.RouteOff) && a.For(t.Category)
                                    && JobTypes.Allows(a, t.RouteOff) && Offered(a, t));

    /// <summary>Another machine the same client brought in at the same time: its own ticket, booked in beside this one.</summary>
    public const string AddMachineKey = "add-machine";
    /// <summary>Same job or a separate one FIRST (2026-10-02) — it decides what the rest means — then
    /// the fault (or, on the same job, what the machine is for) and the booking's questions.</summary>
    public static readonly StageField[] AddMachineFields =
    {
        StageField.SameJob, StageField.Details, StageField.AssetMake, StageField.AssetModel, StageField.AssetSerial,
        StageField.Accessories, StageField.DevicePassword
    };

    private static bool MayAddMachine(Ticket t, TenantRole role, string me) =>
        t.Category == TicketCategory.Workshop && t.Stage == Workflow.First && JobTypes.On(t.RouteOff, JobTypes.AddMachine)
        && (Permissions.CanWorkBilling(role) || Workflow.MayAct(WorkLane.Bench, t, role, me));

    public const string CollectedKey = "collected";
    /// <summary>Taking a finished machine to the customer instead of their collecting it.</summary>
    public const string DeliverKey = "send-for-delivery";

    /// <summary>
    /// The few steps whose offer depends on more than the stage. A delivery's own work is not
    /// invoiced while the office has an invoice out and unpaid: that one is paid on delivery or stays
    /// with the office, and a second "to be invoiced" would lose track of it.
    /// </summary>
    private static bool Offered(ActionDef a, Ticket t) =>
        (a.Key != "delivery-invoice" || t.StageReturn != "awaiting-payment")
        // Filed already (2026-10-09: linking leaves it open, so it is still at the step that offers this).
        // Moving it to another job is "Not part of it" first — one way in, one way out.
        && (a.Key != Workflow.LinkParent || t.ParentTicketId == null);

    /// <summary>
    /// Whether pressing this step would finish the job — land on a Done stage — other than Abandoned: the steps an
    /// open part of the job stops (3.6.7). One rule for the refusal in <see cref="ActAsync"/> and for the pop-up both
    /// UIs show before anything is sent (2026-10-09), so the two can never disagree about which steps count.
    /// </summary>
    public static bool ClosesJob(ActionDef a, Ticket t) =>
        a.Key != "abandon" && Workflow.Stage(TargetOf(a, t)) is { Lane: WorkLane.Done };

    /// <summary><see cref="ClosesJob(ActionDef, Ticket)"/> by the step's key, for a page holding only the key.</summary>
    public static bool ClosesJob(string actionKey, Ticket t) => Workflow.Action(actionKey) is { } a && ClosesJob(a, t);

    /// <summary>Where a step goes from this ticket, its "@" targets resolved — after the ticket's Job types have said where
    /// a skipped step sends it (<see cref="JobTypes.ToOf"/>).</summary>
    public static string TargetOf(ActionDef a, Ticket t) => JobTypes.ToOf(a, t.RouteOff) switch
    {
        "@return" => t.StageReturn.Length > 0 ? t.StageReturn : Workflow.Inspected,
        // Recorded, and the job stays where it is (Reschedule).
        "@stay" => t.Stage,
        // A declined "do not invoice" goes back to where the technician decided — which depends on
        // the route: the workshop's repairable-or-not, the walk in's repair, the remote session,
        // or (General, whose only way here is a rejected quote) the rejected quote.
        "@decision" => t.Category switch
        {
            TicketCategory.WalkIn => Workflow.WalkInRepaired,
            TicketCategory.RemoteSupport => Workflow.RemoteFirst,
            TicketCategory.General => "quote-rejected",
            _ => t.Repairable == false ? Workflow.NotRepairableStage : Workflow.RepairableStage
        },
        // Paid cash or card: on site for a call out, at the counter for a walk in.
        "@paid" => t.Stage == Workflow.PaidCounter ? Workflow.PaidCounterToClose : Workflow.PaidOnSiteToClose,
        // A call out has no machine here to collect: paid is done.
        "@close" => t.CollectedUtc != null || t.Category != TicketCategory.Workshop ? Workflow.Closed : Workflow.AwaitingCollection,
        var to => to
    };

    /// <summary>
    /// Press a button. Empty means it happened; otherwise the reason it did not, in words for the
    /// person who pressed it. Nothing is half-applied: a refusal changes nothing at all.
    /// </summary>
    public async Task<string> ActAsync(int tenantId, int ticketId, string by, TenantRole role, string actionKey, StageInput? input = null)
    {
        input ??= new StageInput();
        if (!Permissions.CanWorkTickets(role)) return Permissions.OwnerOnlyRefusal;
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
        if (t == null) return "That ticket is not on file.";
        if (!Workflow.Has(t.Category) || t.Stage.Length == 0) return "This ticket has no workflow.";

        if (actionKey == GoBackKey) return await GoBackAsync(db, t, by, role, input.Reason);
        if (actionKey == CollectedKey) return await MarkCollectedAsync(db, t, by, role);
        if (actionKey == DeliverKey) return await SendForDeliveryAsync(db, t, by, role);
        if (actionKey == AddMachineKey) return await AddMachineAsync(db, t, by, role, input);

        var a = Workflow.Action(actionKey);
        if (a == null || !a.For(t.Category)) return "That is not a step this job has.";
        if (!Offered(a, t))
            return a.Key == Workflow.LinkParent
                ? "This ticket is already part of another job. Take it out of that one first (Not part of it)."
                : "An invoice for this job is still out and unpaid. Take the payment on delivery, or leave it with the office.";
        var here = Workflow.Stage(t.Stage);
        if (here == null || here.Lane == WorkLane.Done) return "This job is already closed.";
        if (!JobTypes.FromHere(a, t.Stage, t.RouteOff))
            return $"This job is at \"{here.Label}\", so \"{a.Label}\" is not a step it can take now.";
        // Switched off in the company's Job types when this job was opened — a stale page or an old console must not walk round it.
        if (!JobTypes.Allows(a, t.RouteOff))
            return $"\"{a.Label}\" is switched off for this job (Job types).";
        // Booked in to the workshop: the company must have the workshop NOW, not only when this job was opened.
        if (Workflow.ToWorkshopKeys.Contains(a.Key)
            && JobTypes.CategoryRefusal(await CompanyOffAsync(db, tenantId), TicketCategory.Workshop) is { Length: > 0 } noWorkshop)
            return noWorkshop;
        if (!Workflow.MayPress(a, t, role, by))
            return a.Performer switch
            {
                WorkLane.Office => "That step belongs to the office administrator, a supervisor or the owner.",
                WorkLane.Supervisor => "That needs a supervisor's or the owner's approval.",
                _ => a.OfficeToo ? "That step belongs to the people on the job, the office or a supervisor."
                    : "That step belongs to the technician the job is assigned to, or a supervisor."
            };

        // ---- what must be true before it may leave ----
        foreach (var f in a.Fields.Except(Workflow.Optional))
        {
            var missing = f switch
            {
                StageField.Minutes => input.Minutes is null or <= 0 ? "Enter the time spent, in minutes." : "",
                StageField.Amount => input.Amount is null or < 0 ? "Enter the amount." : "",
                StageField.Reference => string.IsNullOrWhiteSpace(input.Reference)
                    ? a.Key == "paid-card" ? "Enter the card machine's reference number." : "Enter the reference or invoice number from your accounting package." : "",
                StageField.Reason => string.IsNullOrWhiteSpace(input.Reason) ? "Say why." : "",
                StageField.SupplierEmail => QuoteService.Addresses(input.SupplierEmail) is { Count: > 0 } sent && sent.All(LooksLikeEmail)
                    ? "" : "Choose at least one supplier to ask.",
                StageField.Repairable => input.Repairable == null ? "Say whether the machine is repairable." : "",
                StageField.Details => string.IsNullOrWhiteSpace(input.Details)
                    ? a.Key == "inspection" ? "Say what the inspection found." : "Say what was done." : "",
                StageField.ParentTicket => input.ParentTicketId is null or <= 0 ? "Choose the ticket this is part of." : "",
                StageField.Appointment => input.AppointmentUtc == null ? "Choose the new date and time."
                    : input.AppointmentUtc <= DateTime.UtcNow ? "That time has already passed. Choose a date and time still to come."
                    : input.AppointmentUtc == t.AppointmentUtc ? "The call out is booked for then already. Choose a different date or time." : "",
                _ => ""
            };
            if (missing.Length > 0) return missing;
        }
        // The damage report itself (2026-10-13): at least one file picked for it, by the person pressing it, on this ticket.
        var files = (input.AttachmentIds ?? new()).Distinct().ToList();
        if (a.Fields.Contains(StageField.Files)
            && !await db.TicketAttachments.AnyAsync(x => x.TenantId == tenantId && x.TicketId == t.Id && x.NoteId == null && x.UploadedBy == by && files.Contains(x.Id)))
            return a.Key == Workflow.DamageReport ? "Attach the damage report — a PDF, a Word document or photos of it." : "Attach the files first.";
        if (a.NeedsWorkDone && !await db.TicketNotes.AnyAsync(n => n.TicketId == t.Id && n.IsWorkDone))
            return "Add a Work done entry first — what was done to the machine.";
        if (a.NeedsQuoteLines && !await db.QuoteLines.AnyAsync(q => q.TicketId == t.Id))
            return "Add at least one item to the quote first.";
        // Going to the workshop is booking a machine in, and a booking needs its photos (§35) — unless the company's
        // Job types said not to ask for them when this job was opened.
        int photosIn = 0;
        if (Workflow.ToWorkshopKeys.Contains(a.Key))
        {
            photosIn = await db.TicketPhotos.CountAsync(p => p.TicketId == t.Id && p.Kind == PhotoKind.Before);
            if (JobTypes.PhotosInRequired(t.RouteOff) && photosIn < TicketPhotoService.Required)
                return $"Take {TicketPhotoService.Required} photos of the machine as it goes to the workshop first. " +
                       (photosIn == 0 ? "None have been added yet." : photosIn == 1 ? "One has been added." : $"{photosIn} have been added.");
        }
        // Filing under another job: one of THIS client's, still open, and one level deep — the same
        // rules SetParentAsync keeps, so the two ways of linking cannot disagree.
        Ticket? parent = null;
        if (a.Key == Workflow.LinkParent)
        {
            if (t.ClientId == null)
                return "Link this ticket to a client first — it can only be part of another job for the same client.";
            parent = await db.Tickets.FirstOrDefaultAsync(x => x.Id == input.ParentTicketId && x.TenantId == tenantId);
            if (parent == null || parent.Id == t.Id || parent.ClientId != t.ClientId)
                return "Choose one of this client's open tickets.";
            if (parent.Status is TicketStatus.Closed or TicketStatus.Resolved)
                return $"#{parent.Number} is closed. Choose one that is still open.";
            if (parent.ParentTicketId != null)
                return $"#{parent.Number} is already part of another job. Choose that one instead.";
            if (await db.Tickets.AnyAsync(x => x.TenantId == tenantId && x.ParentTicketId == t.Id))
                return "Other tickets are filed under this one, so it cannot become part of another.";
        }

        // ---- where it goes ----
        string to = TargetOf(a, t);
        if (a.Key == "close" && t.CollectedUtc == null)
            return JobTypes.PhotosOutRequired(t.RouteOff)
                ? "The machine has not been marked collected. Mark it collected, with three photos of it as it leaves, and then close."
                : "The machine has not been marked collected. Mark it collected, and then close.";
        var next = Workflow.Stage(to)!;
        // A job is not finished while part of it is still open (3.6.7). That rule lived in the status
        // update, which a route never uses — so it is asked here too, of every step that closes.
        // Abandoning is the exception: walking away from a job is not claiming it is done.
        if (ClosesJob(a, t))
        {
            var openChildren = await CheckChildrenClosedAsync(tenantId, t, TicketStatus.Closed);
            if (openChildren.Length > 0) return openChildren;
        }

        // ---- what the button records ----
        // Everything it may change, as it was, so "Back" can put it all back (2026-10-13).
        var snapshot = StepSnapshot.Of(t);
        var said = new List<string>();
        if (a.IsDecision)
        {
            // Whoever the job was with is who did the work, even when a supervisor pressed the button —
            // every one of them, when several people were on it, so each can follow it through billing.
            t.WorkedBy = t.AssignedTo.Length > 0 ? t.AssignedTo : by;
            t.Outcome = a.Outcome;
        }
        if (a.SetsRepairable.HasValue) t.Repairable = a.SetsRepairable;
        if (a.Fields.Contains(StageField.Repairable) && input.Repairable.HasValue) t.Repairable = input.Repairable;
        // Written as "Label: value", one per line: they are the filled-in form in the timeline.
        if (a.Fields.Contains(StageField.Details)) said.Add(input.Details!.Trim());
        // Comments may be empty; written first, they are all of the step the client is emailed (ClientUpdateService.Words).
        if (a.Fields.Contains(StageField.Comments) && !string.IsNullOrWhiteSpace(input.Comments)) said.Add(input.Comments.Trim());
        if (a.Fields.Contains(StageField.Minutes))
        {
            said.Add($"Time spent: {Minutes(input.Minutes!.Value)}");
            // A delivery's time is on top of the bench's, not instead of it.
            if (a.Key == "delivered" && t.TimeSpentMinutes is > 0)
            {
                t.TimeSpentMinutes += input.Minutes;
                said.Add($"On the job in all: {Minutes(t.TimeSpentMinutes!.Value)}");
            }
            else t.TimeSpentMinutes = input.Minutes;
        }
        if (a.Fields.Contains(StageField.Amount))
        {
            if (a.Key == "quote-sent") { t.QuoteAmount = input.Amount; said.Add($"Quote amount: {Money(input.Amount!.Value)}"); }
            else if (a.Key is "paid-cash" or "paid-card")
            {
                t.InvoiceAmount = input.Amount;
                t.PaymentMethod = a.Key == "paid-cash" ? "cash" : "card";
                said.Add($"Amount paid{(a.Key == "paid-cash" ? " in cash" : " by card")}: {Money(input.Amount!.Value)}");
            }
            else if (a.Key == "delivery-invoice")
            {
                // The office has not invoiced yet: the delivery goes on the same invoice. It has (and
                // been paid, or not invoiced at all): the delivery is an invoice of its own.
                said.Add($"Amount to invoice for the delivery: {Money(input.Amount!.Value)}");
                if (t.StageReturn == "to-be-invoiced" && t.InvoiceAmount.HasValue)
                {
                    t.InvoiceAmount += input.Amount;
                    said.Add($"Added to the invoice: {Money(t.InvoiceAmount!.Value)} to invoice in all");
                }
                else { t.InvoiceAmount = input.Amount; t.InvoiceNumber = ""; }
            }
            else { t.InvoiceAmount = input.Amount; said.Add($"{(a.IsDecision ? "Amount to invoice" : "Invoice amount")}: {Money(input.Amount!.Value)}"); }
        }
        if (a.Key == "delivered")
        {
            // Handed over: the machine has gone, as surely as if it had been collected.
            t.CollectedUtc = DateTime.UtcNow;
            t.CollectedBy = by;
            t.Delivered = true;
        }
        if (a.Fields.Contains(StageField.Reference))
        {
            var r = input.Reference!.Trim();
            if (a.Key == "quote-sent") { t.QuoteReference = r; said.Add($"Quote reference: {r}"); }
            else if (a.Key == "paid-card") { t.PaymentReference = r; said.Add($"Card machine reference: {r}"); }
            else { t.InvoiceNumber = r; said.Add($"Invoice number: {r}"); }
        }
        if (Workflow.ToWorkshopKeys.Contains(a.Key))
        {
            // The same ticket, now a workshop job — Clint's choice: one number, one story.
            t.Category = TicketCategory.Workshop;
            t.AssetMake = (input.AssetMake ?? "").Trim(); t.AssetModel = (input.AssetModel ?? "").Trim();
            t.AssetSerial = (input.AssetSerial ?? "").Trim(); t.Accessories = (input.Accessories ?? "").Trim();
            t.DevicePasswordEnc = input.DevicePasswordEnc ?? "";
            t.DevicePasswordProtected = t.DevicePasswordEnc.Length > 0;
            var machine = $"{t.AssetMake} {t.AssetModel}".Trim();
            said.Add($"Machine: {(machine.Length > 0 ? machine : "not recorded")}{(t.AssetSerial.Length > 0 ? $" · serial {t.AssetSerial}" : "")}");
            said.Add($"Accessories: {(t.Accessories.Length > 0 ? t.Accessories : "none recorded")}");
            said.Add(t.DevicePasswordProtected ? "Password protected" : "Not password protected");
            said.Add($"Photos in: {photosIn}");
        }
        if (a.Fields.Contains(StageField.SupplierEmail))
        {
            t.QuoteSupplierEmail = string.Join(", ", QuoteService.Addresses(input.SupplierEmail));
            t.QuoteSupplierNames = (input.SupplierNames ?? "").Trim();
            said.Add($"Asked: {(t.QuoteSupplierNames.Length > 0 ? t.QuoteSupplierNames : t.QuoteSupplierEmail)}");
            var machine = QuoteService.MachineOf(t);
            if (!machine.IsEmpty) said.Add($"For: {machine.Describe()}");
        }
        if (a.Fields.Contains(StageField.Appointment))
        {
            // New first, then what it was: the story is read for when they are going, not when they were.
            said.Add($"New appointment: {Appointment(input.AppointmentUtc!.Value)}");
            said.Add($"Was: {(t.AppointmentUtc.HasValue ? Appointment(t.AppointmentUtc.Value) : "not scheduled")}");
            t.AppointmentUtc = input.AppointmentUtc;
        }
        if (a.Fields.Contains(StageField.Reason))
        {
            var why = input.Reason!.Trim();
            if (a.Key == "abandon") t.AbandonedReason = why;
            else if (a.Outcome == "dni") t.DoNotInvoiceReason = why;
            said.Add($"Reason: {why}");
        }
        if (a.Key == "wait-customer") t.StageReturn = t.Stage;
        if (a.Key == "remote-sla") { t.DoNotInvoiceReason = "SLA client"; said.Add("Not invoiced: SLA client"); }
        if (parent != null)
        {
            t.ParentTicketId = parent.Id;
            said.Add($"Part of #{parent.Number} — {parent.Subject}");
            db.TicketNotes.Add(new TicketNote { TenantId = tenantId, TicketId = parent.Id, Author = by, IsSystem = true, Stage = parent.Stage,
                Body = $"#{t.Number} filed under this job." });
        }

        // ---- whose desk it lands on ----
        // The billing queue and a supervisor's decision belong to nobody in particular: every office
        // administrator sees the queue (answer 16). Coming back to the bench, it goes to the technician
        // who did the work — Parts fitted, a rejected quote, a declined "do not invoice" (answer 10).
        string before = t.AssignedTo;
        if (next.Lane is WorkLane.Office or WorkLane.Supervisor) t.AssignedTo = "";
        else if (next.Lane == WorkLane.Bench && here.Lane != WorkLane.Bench && t.WorkedBy.Length > 0) t.AssignedTo = t.WorkedBy;

        var from = t.Stage;
        MoveTo(t, next);
        // Say WHO was taken off, not only where it went (Clint, 2026-10-02): "In the billing queue"
        // alone left people believing the technician was still on it, so a later assignment's
        // "from unassigned" read as a fault.
        var whereNow = t.AssignedTo == before ? ""
            : t.AssignedTo.Length == 0
                ? $"{Assignees.Say(Assignees.Parse(before))} taken off — " + QueuePhrase(next.Lane) + "."
                : $"Back with {Assignees.Say(Assignees.Parse(t.AssignedTo))}.";

        // ONE timeline entry per step (2026-09-27). A step that asked for something is its FORM,
        // filled in — the title is the step, the body what was typed — so the ticket reads as the
        // story Clint described. A step that asked for nothing is a one-line move.
        // Quote needed has no typed fields, but it does have a form: the items the supplier is asked for.
        if (a.Key == "quote-needed")
            foreach (var l in await db.QuoteLines.Where(q => q.TicketId == t.Id).OrderBy(q => q.Id).ToListAsync())
                said.Add("• " + QuoteService.Describe(l));
        bool isForm = a.Fields.Length > 0 || a.Key == "quote-needed";
        var body = isForm
            ? string.Join("\n", said.Append(whereNow).Where(s => s.Length > 0))
            : $"{a.Label} → {next.Label}" + (whereNow.Length > 0 ? ". " + whereNow : "");
        var note = new TicketNote
        {
            // A step that stays where it is moved nothing: no arrow in the story, and the crumb's
            // "came from" is still the step that really brought it here.
            TenantId = tenantId, TicketId = t.Id, Author = by, Body = body, Stage = from, MovedTo = next.Key == from ? "" : next.Key,
            IsSystem = !isForm, FormTitle = isForm ? a.Label : "",
            // What was done to the machine is what gets copied into the invoice (answer 3). Since
            // 2026-09-27 it is written in Solved — to be invoiced; "work-done" is the retired step.
            IsWorkDone = a.Key is "solved-invoice" or "work-done" or "callout-work-done" or "walkin-repaired" or "remote-invoice" or "general-invoice" or "recurring-invoice" or "delivered",
            // A step that moves the job keeps what it found, so going back over it can put it back.
            Before = next.Key == from ? "" : snapshot.Json()
        };
        db.TicketNotes.Add(note);
        // What was typed into a form may ask somebody something, like any comment.
        var touched = a.Fields.Contains(StageField.Details) || a.Fields.Contains(StageField.Comments)
            ? await ApplyMentionsAsync(db, tenantId, t, note) : new List<string>();
        await db.SaveChangesAsync();
        // The files picked for it go with it — the damage report, under its step.
        if (files.Count > 0 && await TicketAttachmentService.ClaimAsync(db, tenantId, t.Id, note.Id, files, by) > 0)
            await db.SaveChangesAsync();
        RaiseMentionsChanged(tenantId, touched);
        // Into the billing queue from outside it: the office is told (Clint, 2026-10-05). Any move
        // in or out of it, or within it, changes how much is waiting for them, so the badge moves too.
        if (here.Lane == WorkLane.Office || next.Lane == WorkLane.Office)
            await RaiseBillingChangedAsync(tenantId, here.Lane != WorkLane.Office && next.Lane == WorkLane.Office
                ? new BillingArrival(t.Id, t.Number, t.Subject, next.Label, by) : null);
        return "";
    }

    /// <summary>
    /// Where a job with nobody on it is waiting, in the words the trail uses: when people are taken
    /// off it at a billing step, and when somebody is put back on it there.
    /// </summary>
    internal static string QueuePhrase(WorkLane lane) =>
        lane == WorkLane.Supervisor ? "with the supervisors for approval" : "in the billing queue";

    /// <summary>
    /// The part of a Work done entry that belongs on the customer's invoice: what was done, without
    /// the time and amount written under it. Solved — to be invoiced writes the typed words FIRST and
    /// "Time spent: …" next (time is required there), so everything above that line is the work.
    /// An entry with no such line — the retired Work done step, the hub's TicketWorkDoneMsg — is all work.
    /// </summary>
    public static string WorkDoneText(string body)
    {
        var lines = body.Replace("\r", "").Split('\n');
        int end = Array.FindIndex(lines, l => l.StartsWith("Time spent: "));
        return string.Join("\n", end < 0 ? lines : lines[..end]).Trim();
    }

    /// <summary>
    /// The machine has left. Needs three photos of it going out — Clint: "Ideally this must be done
    /// in front of the customer before any machine leaves the workshop." Moved here from the close
    /// on 2026-09-24, which may be days later when the payment clears; closing now needs this done
    /// (or the job abandoned).
    /// </summary>
    private async Task<string> MarkCollectedAsync(PlatformDb db, Ticket t, string by, TenantRole role)
    {
        var stage = Workflow.Stage(t.Stage);
        if (t.Category != TicketCategory.Workshop) return "Only a workshop machine is collected.";
        if (stage == null || !stage.MachineReady) return "The machine cannot be collected at this stage — the work on it is not finished.";
        if (t.CollectedUtc != null) return "The machine has already been collected.";
        if (!Workflow.MayMarkCollected(t, role, by)) return "Marking a machine collected is for the office administrator or the technician on the job.";
        int n = await db.TicketPhotos.CountAsync(p => p.TicketId == t.Id && p.Kind == PhotoKind.After);
        var (need, machines) = await PhotosOutRequiredAsync(db, t);
        if (n < need) return TakePhotosOut(need, machines, n, machines == 1 ? "as it leaves" : "as they leave");
        t.CollectedUtc = DateTime.UtcNow;
        t.CollectedBy = by;
        t.UpdatedUtc = DateTime.UtcNow;
        var took = n == 0 ? "" : $", with {n} photos of {(machines == 1 ? "it as it" : "them as they")} left";
        db.TicketNotes.Add(new TicketNote { TenantId = t.TenantId, TicketId = t.Id, Author = by, IsSystem = true, Stage = t.Stage,
            Body = (machines == 1 ? "Machine collected" : $"{(machines == 2 ? "Both machines" : $"All {machines} machines")} collected") + took + "." });
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>
    /// A finished machine goes out to the customer with a technician (Clint, 2026-09-30) instead of
    /// being collected. The same people and the same photos as Machine collected — taken at the office,
    /// before it leaves — then it is on the bench with the technician who did the work, until it is
    /// delivered. Where the billing had got to is kept, so it carries on afterwards.
    /// </summary>
    private async Task<string> SendForDeliveryAsync(PlatformDb db, Ticket t, string by, TenantRole role)
    {
        var stage = Workflow.Stage(t.Stage);
        if (t.Category != TicketCategory.Workshop) return "Only a workshop machine is delivered.";
        if (stage == null || !stage.MachineReady) return "The machine cannot go out yet — the work on it is not finished.";
        if (t.CollectedUtc != null) return "The machine has already left.";
        if (!JobTypes.On(t.RouteOff, JobTypes.Delivery)) return "Out for delivery is switched off for this job (Job types). Mark it collected instead.";
        if (!Workflow.MayMarkCollected(t, role, by)) return "Sending a machine out is for the office administrator or the technician on the job.";
        int n = await db.TicketPhotos.CountAsync(p => p.TicketId == t.Id && p.Kind == PhotoKind.After);
        var (need, machines) = await PhotosOutRequiredAsync(db, t);
        if (n < need) return TakePhotosOut(need, machines, n, "before leaving the office");

        // Always a technician (Clint): the one who did the work, or whoever sends it if that is them.
        // Nobody otherwise — the job is then on the bench for a technician to take.
        var snapshot = StepSnapshot.Of(t);   // for going back (2026-10-13)
        string who = t.WorkedBy.Length > 0 ? t.WorkedBy : Permissions.CanChangeStatus(role) ? by : "";
        var from = t.Stage;
        t.StageReturn = from;
        t.AssignedTo = who;
        MoveTo(t, Workflow.Stage(Workflow.OutForDelivery)!);
        db.TicketNotes.Add(new TicketNote
        {
            TenantId = t.TenantId, TicketId = t.Id, Author = by, IsSystem = true, Stage = from, MovedTo = Workflow.OutForDelivery, Before = snapshot.Json(),
            Body = (n == 0 ? "Out for delivery. " : $"Out for delivery, with {n} photos of the machine as it left the office. ")
                   + (who.Length > 0 ? $"With {Assignees.Say(Assignees.Parse(who))}." : "Waiting for a technician to take it.")
        });
        await db.SaveChangesAsync();
        // It has left the billing queue for the bench: one less waiting for the office.
        if (stage.Lane == WorkLane.Office) await RaiseBillingChangedAsync(t.TenantId, null);
        return "";
    }

    /// <summary>
    /// Another machine the same client brought in with this one (Clint, 2026-09-30: "an option to add
    /// machine with it's fault description and ask for the same details as the original"). It is a
    /// ticket of its own — its own fault, photos and outcome, because one may be repaired while
    /// another is abandoned — with the same client, contact, people and priority. Every ticket of one
    /// booking carries the same <see cref="Ticket.BookingId"/>: the first one's id. Nothing waits for
    /// its siblings; paying together and leaving together are the office's to arrange.
    /// </summary>
    private async Task<string> AddMachineAsync(PlatformDb db, Ticket t, string by, TenantRole role, StageInput input)
    {
        if (t.Category != TicketCategory.Workshop) return "Only a workshop booking can have another machine added.";
        if (t.Stage != Workflow.First) return "Another machine is added when the job is booked in, before work on it starts. Book it in as a new ticket instead.";
        if (!JobTypes.On(t.RouteOff, JobTypes.AddMachine)) return "Add another machine is switched off for this job (Job types). Book it in as a new ticket instead.";
        if (!MayAddMachine(t, role, by)) return "Adding a machine is for the office or the people on this job.";
        // Same job: one fault for the whole job, already written, so what is typed is what this
        // machine is FOR — and that may be left empty.
        if (!input.SameJob && string.IsNullOrWhiteSpace(input.Details)) return "Say what is wrong with it.";
        if (_photos == null) return "Photos cannot be checked here.";
        // As this job's own Job types say: the machines of one booking follow one route.
        var photoProblem = await _photos.CheckBookInAsync(t.TenantId, TicketCategory.Workshop, input.PhotoStageId ?? "", by, t.RouteOff);
        if (photoProblem.Length > 0) return photoProblem;
        if (input.SameJob) return await AddMachineToJobAsync(db, t, by, input);

        var fault = input.Details!.Trim();
        var make = (input.AssetMake ?? "").Trim();
        var model = (input.AssetModel ?? "").Trim();
        var machine = $"{make} {model}".Trim();
        var firstLine = fault.Replace("\r", "").Split('\n')[0].Trim();
        if (firstLine.Length > 80) firstLine = firstLine[..77] + "…";
        var subject = machine.Length > 0 ? $"{machine} — {firstLine}" : firstLine;

        int group = t.BookingId ?? t.Id;
        var added = await CreateAsync(t.TenantId, subject, fault, by, TicketSource.Manual, "", "", t.ContactName, t.Priority,
            t.AssignedTo, TicketCategory.Workshop, t.ClientId, x =>
            {
                x.BookingId = group;
                x.RouteOff = t.RouteOff;
                x.SiteAddress = t.SiteAddress;
                x.AssetMake = make; x.AssetModel = model;
                x.AssetSerial = (input.AssetSerial ?? "").Trim(); x.Accessories = (input.Accessories ?? "").Trim();
                x.DevicePasswordEnc = input.DevicePasswordEnc ?? "";
                x.DevicePasswordProtected = x.DevicePasswordEnc.Length > 0;
            });
        int photos = await _photos.AttachStagedAsync(t.TenantId, added.Id, input.PhotoStageId ?? "", by);
        await AddNoteAsync(t.TenantId, added.Id, by,
            $"Booked in with #{t.Number} — {t.Subject.TrimEnd('.')}. {photos} photo{(photos == 1 ? "" : "s")} of the machine as it arrived.", true, automatic: true);

        t.BookingId = group;
        t.UpdatedUtc = DateTime.UtcNow;
        db.TicketNotes.Add(new TicketNote { TenantId = t.TenantId, TicketId = t.Id, Author = by, IsSystem = true, Stage = t.Stage,
            Body = $"#{added.Number} booked in with this one — {subject.TrimEnd('.')}." });
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>
    /// Another machine on THIS job (Clint, 2026-10-02): "when we need to do a data transfer. the client
    /// would bring in their old and new machines which needs to be booked in together under one ticket."
    /// The same booking questions and its own three photos in, as a separate machine has; but no
    /// ticket of its own — one fault, one route, one invoice, and the machines leave together, so
    /// the photos out count every machine (<see cref="PhotosOutRequiredAsync"/>).
    /// </summary>
    private async Task<string> AddMachineToJobAsync(PlatformDb db, Ticket t, string by, StageInput input)
    {
        var m = new TicketMachine
        {
            TenantId = t.TenantId, TicketId = t.Id, AddedBy = by,
            Purpose = (input.Details ?? "").Trim(),
            Make = (input.AssetMake ?? "").Trim(), Model = (input.AssetModel ?? "").Trim(),
            Serial = (input.AssetSerial ?? "").Trim(), Accessories = (input.Accessories ?? "").Trim(),
            PasswordEnc = input.DevicePasswordEnc ?? ""
        };
        m.PasswordProtected = m.PasswordEnc.Length > 0;
        db.TicketMachines.Add(m);
        await db.SaveChangesAsync();
        int photos = await _photos!.AttachStagedAsync(t.TenantId, t.Id, input.PhotoStageId ?? "", by, m.Id);
        int count = await db.TicketMachines.CountAsync(x => x.TicketId == t.Id) + 1;

        // The booking's own lines, so the story reads the same as the first machine's did.
        var said = new List<string> { $"Machine {count} on this job" };
        if (m.Purpose.Length > 0) said.Add(m.Purpose);
        said.Add($"Machine: {(m.Name.Length > 0 ? m.Name : "not recorded")}{(m.Serial.Length > 0 ? $" · serial {m.Serial}" : "")}");
        said.Add($"Accessories: {(m.Accessories.Length > 0 ? m.Accessories : "none recorded")}");
        said.Add(m.PasswordProtected ? "Password protected" : "Not password protected");
        said.Add($"Photos in: {photos}");
        t.UpdatedUtc = DateTime.UtcNow;
        db.TicketNotes.Add(new TicketNote
        {
            TenantId = t.TenantId, TicketId = t.Id, Author = by, Stage = t.Stage, IsSystem = false,
            FormTitle = "Another machine on this job", Body = string.Join("\n", said)
        });
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>The further machines on this job, in the order they were added. The ticket's own
    /// Asset* columns are machine 1; these are 2, 3….</summary>
    public async Task<List<TicketMachine>> MachinesAsync(int tenantId, int ticketId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TicketMachines.Where(m => m.TenantId == tenantId && m.TicketId == ticketId).OrderBy(m => m.Id).ToListAsync();
    }

    /// <summary>
    /// Photos out needed before the job's machines leave: three of EACH (2026-10-02). They leave
    /// together, so they are counted together; the words say "of each" so nobody takes six of one.
    /// None when the company's Job types said not to ask for them when the job was opened.
    /// </summary>
    private static async Task<(int Need, int Machines)> PhotosOutRequiredAsync(PlatformDb db, Ticket t)
    {
        int machines = 1 + await db.TicketMachines.CountAsync(m => m.TicketId == t.Id);
        return (JobTypes.PhotosOutRequired(t.RouteOff) ? TicketPhotoService.Required * machines : 0, machines);
    }

    /// <summary>The company's Job types as they are NOW — what a new ticket copies, and what a category is asked against.</summary>
    internal static async Task<string> CompanyOffAsync(PlatformDb db, int tenantId) =>
        await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.JobTypesOff).FirstOrDefaultAsync() ?? "";

    /// <summary>Why a ticket of this category cannot be opened, changed into or converted into at this company now, or empty.</summary>
    public async Task<string> CategoryRefusalAsync(int tenantId, TicketCategory c)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return JobTypes.CategoryRefusal(await CompanyOffAsync(db, tenantId), c);
    }

    private static string TakePhotosOut(int need, int machines, int have, string when) =>
        (machines == 1 ? $"Take {need} photos of the machine {when} first. "
                       : $"Take {need} photos — {TicketPhotoService.Required} of each of the {machines} machines — {when} first. ")
        + (have == 0 ? "None have been added yet." : have == 1 ? "One has been added." : $"{have} have been added.");

    /// <summary>The other tickets booked in with this one, oldest first, with where each has got to.</summary>
    public async Task<List<RemoteSupport.Shared.TicketChoiceInfo>> BookedWithAsync(int tenantId, Ticket t)
    {
        if (t.BookingId == null) return new();
        await using var db = await _dbf.CreateDbContextAsync();
        var rows = await db.Tickets.Where(x => x.TenantId == tenantId && x.BookingId == t.BookingId && x.Id != t.Id)
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Number, x.Subject, x.Stage }).ToListAsync();
        return rows.Select(x => new RemoteSupport.Shared.TicketChoiceInfo
            { Id = x.Id, Number = x.Number, Subject = x.Subject, StageLabel = Workflow.Label(x.Stage) }).ToList();
    }

    /// <summary>
    /// The job this ticket is part of, and the tickets that are part of it (3.6.7) — each with where it has got to and
    /// whether it is still open, for the console's ticket window (2026-10-09; the portal reads the rows itself).
    /// </summary>
    public async Task<(RemoteSupport.Shared.TicketChoiceInfo? Parent, List<RemoteSupport.Shared.TicketChoiceInfo> Children)> JobOfAsync(int tenantId, Ticket t)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var rows = await db.Tickets
            .Where(x => x.TenantId == tenantId && (x.ParentTicketId == t.Id || (t.ParentTicketId != null && x.Id == t.ParentTicketId)))
            .OrderBy(x => x.Number).Select(x => new { x.Id, x.Number, x.Subject, x.Stage, x.Status }).ToListAsync();
        var all = rows.Select(x => new RemoteSupport.Shared.TicketChoiceInfo
        {
            Id = x.Id, Number = x.Number, Subject = x.Subject,
            StageLabel = x.Stage.Length > 0 ? Workflow.Label(x.Stage) : x.Status.ToString(),
            Open = x.Status != TicketStatus.Closed && x.Status != TicketStatus.Resolved
        }).ToList();
        return (all.FirstOrDefault(x => x.Id == t.ParentTicketId), all.Where(x => x.Id != t.ParentTicketId).ToList());
    }

    /// <summary>Sets the stage and everything derived from it, in one place.</summary>
    private static void MoveTo(Ticket t, StageDef next)
    {
        t.Stage = next.Key;
        t.Lane = (int)next.Lane;
        t.Status = next.Summary;
        t.ClosedUtc = next.Lane == WorkLane.Done ? DateTime.UtcNow : null;
        t.UpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// A Work done entry: what was actually done to the machine. The people who touch machines may
    /// write one, and at least one is needed before the job leaves the bench.
    /// </summary>
    public async Task<string> AddWorkDoneAsync(int tenantId, int ticketId, string by, TenantRole role, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "Say what was done.";
        if (!Permissions.CanAddWorkDone(role)) return "Work done is written by the technician.";
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
        if (t == null) return "That ticket is not on file.";
        var note = new TicketNote { TenantId = tenantId, TicketId = t.Id, Author = by, Body = body.Trim(), IsWorkDone = true, Stage = t.Stage };
        db.TicketNotes.Add(note);
        var touched = await ApplyMentionsAsync(db, tenantId, t, note);
        t.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        RaiseMentionsChanged(tenantId, touched);
        return "";
    }

    // ---------------------------------------------------------------- the console's view

    /// <summary>
    /// Fills in what the console needs to draw a ticket's workflow: the stage, whose desk, what has
    /// been recorded, the buttons this person may press, and the parts of the page that go with them.
    /// The same answers the portal draws, from the same <see cref="ActionsFor"/> — the console never
    /// works out a route for itself. Scoping is the caller's: only call this for a ticket the person
    /// may see.
    /// </summary>
    /// <param name="reveal">Decrypts a stored machine password (ClientService.Unprotect) — the further
    /// machines' passwords are shown as the ticket's own is. Null leaves them out.</param>
    public async Task FillWorkflowAsync(RemoteSupport.Shared.TicketDetail d, Ticket t, TenantRole role, string me, Func<string, string>? reveal = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        d.People = await db.Technicians.Where(x => x.TenantId == t.TenantId && !x.Disabled)
            .OrderBy(x => x.DisplayName).Select(x => x.DisplayName).ToListAsync();
        d.OnTicket = OnTicket(t.AssignedTo, t.WorkedBy, d.People);
        d.AskedBy = await db.TicketMentions.Where(m => m.TicketId == t.Id && m.Person == me && m.AnsweredUtc == null && !m.Note.FromCustomer)
            .Select(m => m.MentionedBy).Distinct().ToListAsync();
        // A customer's emailed reply this person is flagged for (2026-10-09): told, then seen by opening it.
        d.RepliedBy = await db.TicketMentions.Where(m => m.TicketId == t.Id && m.Person == me && m.AnsweredUtc == null && m.Note.FromCustomer)
            .Select(m => m.MentionedBy).Distinct().ToListAsync();
        d.CanClaim = t.AssignedTo.Length == 0 && t.Lane == (int)WorkLane.Bench && t.Status != TicketStatus.Closed
                     && Permissions.CanClaim(role);
        d.CanAssignToAnyone = Permissions.CanAssignToAnyone(role);
        d.CanJoin = Permissions.CanClaim(role) && t.Lane == (int)WorkLane.Bench && t.Status != TicketStatus.Closed;
        d.CanPassOn = Permissions.CanPassOn(role);
        d.PhotosIn = await db.TicketPhotos.CountAsync(p => p.TicketId == t.Id && p.Kind == PhotoKind.Before);
        d.PhotosOut = await db.TicketPhotos.CountAsync(p => p.TicketId == t.Id && p.Kind == PhotoKind.After);
        // None asked when this job's Job types switched them off: every check that reads these then passes.
        d.PhotosRequired = JobTypes.PhotosInRequired(t.RouteOff) ? TicketPhotoService.Required : 0;
        // The further machines on this job (2026-10-02), each with its own photos in.
        var machines = await db.TicketMachines.Where(m => m.TicketId == t.Id).OrderBy(m => m.Id).ToListAsync();
        d.PhotosOutRequired = JobTypes.PhotosOutRequired(t.RouteOff) ? TicketPhotoService.Required * (1 + machines.Count) : 0;
        foreach (var m in machines)
            d.Machines.Add(new RemoteSupport.Shared.TicketMachineInfo
            {
                Id = m.Id, Purpose = m.Purpose, Make = m.Make, Model = m.Model, Serial = m.Serial, Accessories = m.Accessories,
                PasswordProtected = m.PasswordProtected,
                Password = m.PasswordProtected && reveal != null ? reveal(m.PasswordEnc) : "",
                PhotosIn = await db.TicketPhotos.CountAsync(p => p.TicketId == t.Id && p.Kind == PhotoKind.Before && p.MachineId == m.Id)
            });
        (d.Parent, d.Children) = await JobOfAsync(t.TenantId, t);
        d.Screenshots = await db.TicketPhotos.Where(p => p.TicketId == t.Id && p.Kind == PhotoKind.Screenshot)
            .OrderBy(p => p.Id).Select(p => p.Id).ToListAsync();
        d.ScreenshotTokens = await db.TicketPhotos.Where(p => p.TicketId == t.Id && p.Kind == PhotoKind.Screenshot)
            .OrderBy(p => p.Id).Select(p => p.Token).ToListAsync();
        // Each comment's files and photos (2026-10-13), and which steps were gone back over — on every ticket.
        var files = await db.TicketAttachments.Where(a => a.TicketId == t.Id && a.NoteId != null).OrderBy(a => a.Id).ToListAsync();
        var undone = await db.TicketNotes.Where(n => n.TicketId == t.Id && (n.UndoneUtc != null || n.WentBack))
            .Select(n => new { n.Id, n.UndoneUtc, n.UndoneBy, n.WentBack }).ToListAsync();
        foreach (var n in d.Notes)
        {
            n.Attachments = files.Where(a => a.NoteId == n.Id).Select(a => new RemoteSupport.Shared.AttachmentInfo
                { Id = a.Id, Token = a.Token, Name = a.OriginalName, Size = TicketAttachmentService.Size(a.Bytes), IsImage = a.IsImage }).ToList();
            // ClientUpdateService.Sendable with its files counted: a comment of photos alone, and the damage report.
            if (n.Attachments.Count > 0 && !n.IsSystem && !n.FromCustomer && !n.IsWorkDone && (n.FormTitle.Length == 0 || n.FormTitle == Workflow.DamageReportLabel)) n.CanEmail = true;
            if (undone.FirstOrDefault(u => u.Id == n.Id) is { } u)
            {
                n.WentBack = u.WentBack;
                if (u.UndoneUtc is DateTime when) n.Undone = $"Gone back over by {u.UndoneBy}, {when.Local():d MMM HH:mm} — what it recorded no longer counts.";
            }
        }
        if (!Workflow.Has(t.Category) || t.Stage.Length == 0) return;

        var stage = Workflow.Stage(t.Stage);
        d.Stage = t.Stage;
        d.StageLabel = stage?.Label ?? t.Stage;
        // The crumb's middle: the stage the step INTO this one came from.
        var arrived = await db.TicketNotes.Where(n => n.TicketId == t.Id && n.MovedTo == t.Stage && !n.WentBack && n.UndoneUtc == null)
            .OrderByDescending(n => n.Id).Select(n => n.Stage).FirstOrDefaultAsync();
        d.PreviousStageLabel = arrived is { Length: > 0 } ? Workflow.Label(arrived) : "";
        d.IsClosed = stage?.Lane == WorkLane.Done;
        d.WithWhom = stage?.Lane switch
        {
            WorkLane.Office => "In the billing queue",
            WorkLane.Supervisor => "Waiting for a supervisor",
            WorkLane.Done => "",
            _ => t.AssignedTo.Length == 0 ? "Nobody yet" : $"With {Assignees.Say(Assignees.Parse(t.AssignedTo))}"
        };
        d.Facts = WorkflowFacts(t);
        d.Actions = ActionsFor(t, role, me, await BackStepAsync(t.TenantId, t, role, me)).Select(a => new RemoteSupport.Shared.StageActionInfo
        {
            Key = a.Key, Label = a.Label, Fields = a.Fields.Select(f => f.ToString()).ToList(),
            IsDecision = a.IsDecision, Closes = a.Closes, ClosesJob = ClosesJob(a.Key, t), IsDanger = a.Key == "abandon",
            Repairable = a.Key switch { "solved-invoice" or "quote-needed" => "yes", "inspection-fee" or Workflow.DamageReport => "no", _ => "both" },
            Warning = a.Warning
        }).ToList();
        d.CanAddWorkDone = Permissions.CanAddWorkDone(role) && stage?.Lane == WorkLane.Bench;
        if (d.Actions.Any(a => a.Key == Workflow.LinkParent)) d.ParentChoices = await ParentChoicesAsync(t.TenantId, t);
        d.BookedWith = await BookedWithAsync(t.TenantId, t);
        d.MayEditLines = QuoteService.MayEditLines(t, role, me);
        d.QuoteLines = await db.QuoteLines.Where(l => l.TicketId == t.Id).OrderBy(l => l.Id)
            .Select(l => new RemoteSupport.Shared.QuoteLineInfo { Id = l.Id, ItemName = l.ItemName, PartNumber = l.PartNumber, SerialNumber = l.SerialNumber, Quantity = l.Quantity })
            .ToListAsync();
        if (d.QuoteLines.Count > 0)
        {
            var lineIds = d.QuoteLines.Select(l => l.Id).ToList();
            var counts = await db.TicketPhotos.Where(p => p.TicketId == t.Id && p.QuoteLineId != null && lineIds.Contains(p.QuoteLineId!.Value))
                .GroupBy(p => p.QuoteLineId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
            foreach (var l in d.QuoteLines)
            {
                l.PhotoCount = counts.FirstOrDefault(c => c.Key == l.Id)?.N ?? 0;
                l.HasPhoto = l.PhotoCount > 0;
            }
        }
        var machine = QuoteService.MachineOf(t);
        d.QuoteMachine = new RemoteSupport.Shared.QuoteMachineInfo
        {
            Type = machine.Type, Make = machine.Make, Model = machine.Model, Serial = machine.Serial,
            FromBooking = !t.QuoteMachineSaved && t.Category == TicketCategory.Workshop && !machine.IsEmpty
        };
        d.MachineTypes = QuoteService.MachineTypes.ToList();
        d.ShowQuote = d.QuoteLines.Count > 0 || t.Outcome == "quote"
                      || t.Stage is "quote-needed" or "quote-requested" or "quote-sent" or "quote-rejected";
    }

    /// <summary>
    /// The tickets this one may be filed under: the same client's, still open, not already part of
    /// another job, and not itself. Empty when the ticket has no client — the page then says to link
    /// one first. Newest first, since the job it belongs to is usually the one just opened.
    /// </summary>
    public async Task<List<RemoteSupport.Shared.TicketChoiceInfo>> ParentChoicesAsync(int tenantId, Ticket t)
    {
        if (t.ClientId == null) return new();
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Tickets
            .Where(x => x.TenantId == tenantId && x.ClientId == t.ClientId && x.Id != t.Id && x.ParentTicketId == null
                     && x.Status != TicketStatus.Closed && x.Status != TicketStatus.Resolved)
            .OrderByDescending(x => x.CreatedUtc).Take(100)
            .Select(x => new RemoteSupport.Shared.TicketChoiceInfo { Id = x.Id, Number = x.Number, Subject = x.Subject })
            .ToListAsync();
    }

    /// <summary>
    /// Which client a ticket is for — at any stage, by anybody who may see it (Clint, 2026-09-27:
    /// "there should always be the option to link to clients at any stage"). Null unlinks. A person
    /// chosen with it becomes the ticket's contact. Empty means done.
    /// </summary>
    /// <param name="contactId">The person too, when known (2026-10-05: an emailed ticket's sender,
    /// added as a contact from the ticket). Null leaves the ticket's contact as it is unless the
    /// client changes, when a contact at the old client no longer belongs.</param>
    public async Task<string> SetClientAsync(int tenantId, int ticketId, int? clientId, string? contactName, string by, int? contactId = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
        if (t == null) return "That ticket is not on file.";
        string name = "";
        if (clientId.HasValue)
        {
            var c = await db.Clients.FirstOrDefaultAsync(x => x.Id == clientId.Value && x.TenantId == tenantId);
            if (c == null) return "That client is not on file.";
            name = c.Name;
        }
        if (contactId.HasValue && !await db.Contacts.AnyAsync(x => x.Id == contactId.Value && x.TenantId == tenantId && x.ClientId == clientId))
            return "That contact is not at that client.";
        if (t.ClientId == clientId && (contactName == null || contactName == t.ContactName) && (contactId == null || contactId == t.ContactId)) return "";
        if (contactId.HasValue) t.ContactId = contactId;
        else if (t.ClientId != clientId) t.ContactId = null;
        t.ClientId = clientId;
        if (contactName != null) t.ContactName = contactName.Trim();
        t.UpdatedUtc = DateTime.UtcNow;
        db.TicketNotes.Add(new TicketNote { TenantId = tenantId, TicketId = t.Id, Author = by, IsSystem = true, Stage = t.Stage,
            Body = clientId.HasValue
                ? $"Linked to {name}{(t.ContactName.Length > 0 && contactName != null ? $" — {t.ContactName}" : "")}."
                : "No longer linked to a client." });
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>What the route has recorded so far, as label and value. Only what exists.</summary>
    public static List<RemoteSupport.Shared.FactInfo> WorkflowFacts(Ticket t)
    {
        var f = new List<RemoteSupport.Shared.FactInfo>();
        void Add(string l, string v) => f.Add(new() { Label = l, Value = v });
        // When it is due (§165), first: it decides what is done next.
        if (t.DueDate is DateTime due) Add("Due", TicketDue.Say(due) + (TicketDue.IsOverdue(t) ? " — overdue" : ""));
        if (t.Repairable.HasValue) Add("Repairable", t.Repairable.Value ? "Yes" : "No");
        if (t.WorkedBy.Length > 0) Add("Work done by", t.WorkedBy);
        if (t.TimeSpentMinutes.HasValue) Add("Time spent", Minutes(t.TimeSpentMinutes.Value));
        if (t.QuoteSupplierEmail.Length > 0) Add("Quote asked of", t.QuoteSupplierNames.Length > 0 ? t.QuoteSupplierNames : t.QuoteSupplierEmail);
        if (t.QuoteAmount.HasValue) Add("Quote", $"{Money(t.QuoteAmount.Value)} · {t.QuoteReference}");
        if (t.InvoiceAmount.HasValue && t.PaymentMethod.Length > 0)
            Add(t.Category == TicketCategory.WalkIn ? "Paid at counter" : t.Delivered ? "Paid on delivery" : "Paid on site", Money(t.InvoiceAmount.Value) + (t.PaymentMethod == "cash" ? " · cash" : " · card machine")
                + (t.PaymentReference.Length > 0 ? " · " + t.PaymentReference : ""));
        else if (t.InvoiceAmount.HasValue) Add(t.InvoiceNumber.Length > 0 ? "Invoiced" : "To invoice",
            Money(t.InvoiceAmount.Value) + (t.InvoiceNumber.Length > 0 ? " · " + t.InvoiceNumber : ""));
        if (t.DoNotInvoiceReason.Length > 0) Add("Not invoiced because", t.DoNotInvoiceReason);
        if (t.CollectedUtc.HasValue) Add(t.Delivered ? "Delivered" : "Collected", $"{t.CollectedUtc.Value.Local():yyyy-MM-dd HH:mm} · {t.CollectedBy}");
        if (t.AbandonedReason.Length > 0) Add("Abandoned", t.AbandonedReason);
        return f;
    }

    // ---------------------------------------------------------------- reading what people type

    /// <summary>"90", "1:30", "1h30", "1.5h" — what people actually type for time.</summary>
    public static int? ParseMinutes(string? s)
    {
        s = (s ?? "").Trim().ToLowerInvariant().Replace(" ", "");
        if (s.Length == 0) return null;
        if (int.TryParse(s, out var m)) return m;
        var parts = s.Replace("hours", ":").Replace("hour", ":").Replace("hrs", ":").Replace("hr", ":").Replace("h", ":")
            .Replace("minutes", "").Replace("mins", "").Replace("min", "").Replace("m", "").Split(':');
        if (parts.Length == 1 && int.TryParse(parts[0], out var justMinutes)) return justMinutes;
        if (parts.Length == 2 && decimal.TryParse(parts[0], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var h))
            return (int)Math.Round(h * 60) + (int.TryParse(parts[1], out var mm) ? mm : 0);
        return null;
    }

    /// <summary>
    /// "R 1 250,50", "1,250.50", "650" — a currency sign and spaces are ignored; a lone comma is the
    /// decimal point, as South Africa writes it; with both, the comma is thousands.
    /// </summary>
    public static decimal? ParseMoney(string? s)
    {
        var t = new string((s ?? "").Where(c => char.IsDigit(c) || c is ',' or '.' or '-').ToArray());
        if (t.Length == 0) return null;
        if (t.Contains(',') && t.Contains('.')) t = t.Replace(",", "");
        else if (t.Contains(',')) t = t.Replace(',', '.');
        return decimal.TryParse(t, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static bool LooksLikeEmail(string? s)
    {
        s = (s ?? "").Trim();
        int at = s.IndexOf('@');
        return at > 0 && at < s.Length - 3 && s.IndexOf('.', at) > at + 1 && !s.Contains(' ');
    }

    public static string Money(decimal d) => d.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);

    public static string Minutes(int m) => m < 60 ? $"{m} min" : $"{m / 60} h {m % 60:00} min";

    /// <summary>An appointment as the ticket's Details show it, in the company's own time zone.</summary>
    public static string Appointment(DateTime utc) => utc.Local().ToString("dddd d MMMM yyyy 'at' HH:mm");
}
