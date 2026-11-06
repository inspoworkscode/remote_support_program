using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>Whose desk a stage sits on. Stored on the ticket so a queue is a query, not a lookup.</summary>
public enum WorkLane
{
    /// <summary>The technician's — the bench. Every ticket without a workflow is here too.</summary>
    Bench = 0,
    /// <summary>The office administrator's billing queue, shared by all of them.</summary>
    Office = 1,
    /// <summary>Waiting on a supervisor's decision.</summary>
    Supervisor = 2,
    /// <summary>Closed or abandoned.</summary>
    Done = 3
}

/// <summary>What an action needs typed in before it is allowed. <see cref="Details"/> is the body of a
/// step's form — Inspection findings, Work done — and is written into the timeline as that form.</summary>
public enum StageField
{
    Minutes, Amount, Reference, Reason, SupplierEmail, Repairable, Details,
    // The workshop booking's questions, asked when a call out becomes a workshop job (2026-09-27).
    // Optional, as they are at the counter: only the photos are required to book a machine in.
    AssetMake, AssetModel, AssetSerial, Accessories, DevicePassword,
    /// <summary>Add another machine: the same job (on this ticket) or a separate one (its own ticket).</summary>
    SameJob,
    /// <summary>Another open ticket of the same client, to file this one under (Remote support).</summary>
    ParentTicket,
    /// <summary>A call out's new date and time (Reschedule, 2026-10-05).</summary>
    Appointment,
    /// <summary>Comments that may be left empty (Damage report needed, 2026-10-13) — written FIRST in the step, so
    /// they are what the client is emailed and nothing under them is.</summary>
    Comments,
    /// <summary>Files picked for the step — the damage report itself. At least one is required.</summary>
    Files,
    /// <summary>"Email the damage report to the client", ticked by default, with who it goes to.</summary>
    EmailClient
}

public sealed record StageDef(string Key, string Label, WorkLane Lane, TicketStatus Summary, bool MachineReady = false);

/// <summary>
/// One button. <see cref="To"/> is a stage key, or one of two words: <c>@return</c> (back to where
/// a side stage was entered from) and <c>@close</c> (closed if the machine has been collected,
/// otherwise Awaiting collection — the job is finished on paper and the machine has not gone yet).
/// <c>@stay</c> records the step and leaves the job where it is (Reschedule).
/// </summary>
public sealed record ActionDef(string Key, string Label, string[] From, string To, WorkLane Performer,
    StageField[]? Needs = null, bool NeedsWorkDone = false, bool NeedsQuoteLines = false,
    bool? SetsRepairable = null, string Outcome = "", bool IsDecision = false, bool Closes = false,
    TicketCategory[]? Only = null, bool OfficeToo = false)
{
    public StageField[] Fields => Needs ?? Array.Empty<StageField>();
    /// <summary>Whether this button belongs on a ticket of this category. Most follow from the stage
    /// alone; <see cref="Only"/> narrows the few shared stages whose ways out differ by category.</summary>
    public bool For(TicketCategory c) => Only == null || Only.Contains(c);
}

/// <summary>
/// The Workshop route, defined ONCE (stage 3.7, 2026-09-24). Both the portal and the console ask
/// <see cref="TicketService.ActionsFor"/> what a ticket may do next and draw buttons for the answer;
/// neither knows a stage by name. Two copies would drift, and the symptom would be a ticket a
/// technician cannot move and nobody can explain.
///
/// <para>The route is Clint's, in <c>docs/NEXT-SESSION-WORKFLOWS.md</c>, with the map approved on
/// 2026-09-24. Where the brief names a stage and then an action whose only purpose is to reach the
/// next one ("Client invoiced", then "Awaiting payment"), the two are one step here: pressing
/// Client invoiced records the invoice and the job then IS awaiting payment. A stage that exists
/// only to be clicked past is a click nobody needs.</para>
///
/// <para>Workshop and, since 2026-09-27, Call out have a route (<see cref="Has"/>). Every other
/// category keeps the plain Open / In progress / On hold / Resolved / Closed it has always had. The
/// two routes share the invoicing stages; stage keys are global, so each route's own stages have
/// keys of their own.</para>
/// </summary>
public static class Workflow
{
    public const string Closed = "closed", Abandoned = "abandoned", AwaitingCollection = "awaiting-collection";
    /// <summary>
    /// Where a booked-in machine starts. The key is older than its label — it was "Awaiting diagnosis"
    /// until 2026-09-27 — and keys are never renamed, because they are what is stored.
    /// </summary>
    public const string First = "awaiting-diagnosis";
    /// <summary>Retired 2026-09-27: kept so history written under it still has a heading. Nothing moves INTO it.</summary>
    public const string OnBench = "on-bench";
    public const string Inspected = "inspected", RepairableStage = "repairable", NotRepairableStage = "not-repairable", WorkDoneStage = "work-done";

    /// <summary>The Call out route (Clint, 2026-09-27): booked, then Work done, then one of three.</summary>
    public const string CallOutFirst = "callout-booked", CallOutWorkDone = "callout-work-done",
        PaidOnSite = "paid-onsite", PaidOnSiteToClose = "paid-onsite-to-close";
    /// <summary>The step that turns a call out into a workshop job. It changes the CATEGORY, so the service handles it by name.</summary>
    public const string ToWorkshop = "needs-workshop";
    /// <summary>A call out moved to another day (Clint, 2026-10-05). It stays where it is: <c>@stay</c>.</summary>
    public const string Reschedule = "callout-reschedule";

    /// <summary>The Walk in route (Clint, 2026-09-27): repaired at the counter, or booked in to the workshop.</summary>
    public const string WalkInFirst = "walkin-booked", WalkInRepaired = "walkin-repaired",
        PaidCounter = "paid-counter", PaidCounterToClose = "paid-counter-to-close";
    /// <summary>A walk in that needs the workshop — the same change of category as <see cref="ToWorkshop"/>.</summary>
    public const string BookInFromWalkIn = "walkin-book-in";
    /// <summary>Every step that turns a ticket into a workshop job at Booked in.</summary>
    public static readonly string[] ToWorkshopKeys = { ToWorkshop, BookInFromWalkIn };

    /// <summary>The Remote support route: a session's ticket, then how it ends.</summary>
    public const string RemoteFirst = "remote-session";
    /// <summary>Filing a remote support ticket under another open ticket of the same client, which closes it.</summary>
    public const string LinkParent = "remote-link";

    /// <summary>The General route: an instruction, a quote, or something to invoice — no client needed.</summary>
    public const string GeneralFirst = "general-open";

    /// <summary>A recurring task's ticket (2026-10-07): due, then Done or To be invoiced, as General.</summary>
    public const string RecurringFirst = "recurring-due";

    /// <summary>
    /// Delivery (Clint, 2026-09-30): a finished workshop machine taken to the customer instead of
    /// collected, then "pretty much the call out process". Offered wherever Machine collected is; the
    /// billing stage it left from is kept in <c>StageReturn</c> and carries on afterwards.
    /// </summary>
    public const string OutForDelivery = "out-for-delivery", DeliveredStage = "delivered";

    /// <summary>
    /// A quote accepted by a company that does not take payment before ordering (Job types, 2026-10-27): the office orders
    /// the parts, they arrive, they are fitted, and THEN the job is invoiced. Only <c>JobTypes.ToOf</c> sends a job here.
    /// </summary>
    public const string OrderParts = "order-parts", PartsOnOrder = "parts-on-order";

    /// <summary>The fields a step may leave empty. Everything else a step asks for is required.</summary>
    public static readonly StageField[] Optional =
        { StageField.AssetMake, StageField.AssetModel, StageField.AssetSerial, StageField.Accessories, StageField.DevicePassword,
          StageField.Comments, StageField.EmailClient };

    /// <summary>
    /// Not repairable — Damage report needed (Clint, 2026-10-13): comments, the report uploaded, emailed to the client
    /// from there, then To be invoiced with the time and the amount, as the inspection fee is.
    /// </summary>
    public const string DamageReport = "damage-report", DamageReportLabel = "Damage report needed";

    public static readonly StageDef[] Stages =
    {
        // ---- the bench, in the order Clint set on 2026-09-27: Inspection findings, then Repairable
        //      or not, and Work done is written INTO Solved — to be invoiced ----
        new(First, "Booked in", WorkLane.Bench, TicketStatus.Open),
        new(Inspected, "Inspected", WorkLane.Bench, TicketStatus.InProgress),
        new(RepairableStage, "Repairable", WorkLane.Bench, TicketStatus.InProgress),
        new(NotRepairableStage, "Not repairable", WorkLane.Bench, TicketStatus.InProgress),
        // Retired the same day: Work done became part of Solved — to be invoiced. Kept so a job left
        // here still has a label and still has its endings; nothing moves INTO it.
        new(WorkDoneStage, "Work done", WorkLane.Bench, TicketStatus.InProgress),
        new(OnBench, "On the bench", WorkLane.Bench, TicketStatus.InProgress),
        new("waiting-customer", "Waiting on customer", WorkLane.Bench, TicketStatus.OnHold),
        new("quote-rejected", "Quote rejected — choose an outcome", WorkLane.Bench, TicketStatus.InProgress),
        new("parts-received", "Parts received — to be fitted", WorkLane.Bench, TicketStatus.InProgress),

        // ---- invoicing ----
        new("to-be-invoiced", "To be invoiced", WorkLane.Office, TicketStatus.InProgress, MachineReady: true),
        new("awaiting-payment", "Invoiced — awaiting payment", WorkLane.Office, TicketStatus.OnHold, MachineReady: true),

        // ---- quotes ----
        new("quote-needed", "Quote needed", WorkLane.Office, TicketStatus.InProgress),
        new("quote-requested", "Quote requested — awaiting supplier", WorkLane.Office, TicketStatus.OnHold),
        new("quote-sent", "Quote sent — awaiting client", WorkLane.Office, TicketStatus.OnHold),
        new("invoice-client", "Quote accepted — invoice client", WorkLane.Office, TicketStatus.InProgress),
        new("quote-awaiting-payment", "Invoiced — awaiting payment before ordering", WorkLane.Office, TicketStatus.OnHold),
        new("order-placed", "Paid — parts ordered from supplier", WorkLane.Office, TicketStatus.OnHold),
        new("parts-fitted", "Parts fitted — complete the job", WorkLane.Office, TicketStatus.InProgress, MachineReady: true),
        // No payment before ordering (Job types): the same road without the invoice and the wait for payment.
        new(OrderParts, "Quote accepted — order the parts", WorkLane.Office, TicketStatus.InProgress),
        new(PartsOnOrder, "Parts ordered from supplier", WorkLane.Office, TicketStatus.OnHold),

        // ---- do not invoice ----
        new("dni-approval", "Do not invoice — awaiting supervisor approval", WorkLane.Supervisor, TicketStatus.InProgress),
        new("dni-to-close", "Do not invoice — ticket to be closed", WorkLane.Office, TicketStatus.InProgress, MachineReady: true),

        // ---- call out (Clint, 2026-09-27) ----
        new(CallOutFirst, "Call out booked", WorkLane.Bench, TicketStatus.Open),
        new(CallOutWorkDone, "Work done", WorkLane.Bench, TicketStatus.InProgress),
        new(PaidOnSite, "Invoiced — paid on site", WorkLane.Bench, TicketStatus.InProgress),
        new(PaidOnSiteToClose, "Paid on site — to be closed", WorkLane.Office, TicketStatus.InProgress),

        // ---- walk in (Clint, 2026-09-27) ----
        new(WalkInFirst, "At the counter", WorkLane.Bench, TicketStatus.Open),
        new(WalkInRepaired, "Repaired at counter", WorkLane.Bench, TicketStatus.InProgress),
        new(PaidCounter, "Invoiced — paid at counter", WorkLane.Bench, TicketStatus.InProgress),
        new(PaidCounterToClose, "Paid at counter — to be closed", WorkLane.Office, TicketStatus.InProgress),

        // ---- remote support ----
        new(RemoteFirst, "Remote session", WorkLane.Bench, TicketStatus.Open),

        // ---- general ----
        new(GeneralFirst, "Open", WorkLane.Bench, TicketStatus.Open),

        // ---- recurring task (Clint, 2026-10-07) ----
        new(RecurringFirst, "Due", WorkLane.Bench, TicketStatus.Open),

        // ---- delivery (Clint, 2026-09-30) ----
        new(OutForDelivery, "Out for delivery", WorkLane.Bench, TicketStatus.InProgress),
        new(DeliveredStage, "Delivered", WorkLane.Bench, TicketStatus.InProgress),

        // ---- the ends ----
        new(AwaitingCollection, "Awaiting collection", WorkLane.Office, TicketStatus.OnHold, MachineReady: true),
        new(Closed, "Closed", WorkLane.Done, TicketStatus.Closed),
        new(Abandoned, "Abandoned", WorkLane.Done, TicketStatus.Closed),
    };

    private static readonly string[] AfterRejection = { "quote-rejected" };
    /// <summary>Where a repairable job chooses how it ends. Work done (retired) is here so a job left there is never stuck.</summary>
    private static readonly string[] Decided = { RepairableStage, WorkDoneStage };
    /// <summary>Where a job can be put on hold for the customer, and come back to.</summary>
    private static readonly string[] Holdable = { First, Inspected, RepairableStage, WorkDoneStage, OnBench };

    public static readonly ActionDef[] Actions =
    {
        // ---- general (Clint, 2026-09-27): "three options, general ticket, quote needed and To be
        //      invoiced". A general ticket is an instruction to somebody internally; the person it is
        //      for presses Done and it closes — nothing to bill, so the office need not see it (his
        //      choice). Quote needed is the workshop's own button, from here too, and To be invoiced
        //      is below. First here so it is drawn first. ----
        new("general-done", "Done", new[] { GeneralFirst }, Closed, WorkLane.Bench, Closes: true),

        // ---- the bench: each press is one step, and a step with a form puts it in the timeline ----
        new("inspection", "Inspection findings", new[] { First }, Inspected, WorkLane.Bench,
            new[] { StageField.Details }),
        // "On the bench" (retired) offers the same choice, so a job already there is never stuck.
        new("repairable", "Repairable", new[] { Inspected, OnBench }, RepairableStage, WorkLane.Bench, SetsRepairable: true),
        new("not-repairable", "Not repairable", new[] { Inspected, OnBench }, NotRepairableStage, WorkLane.Bench, SetsRepairable: false),

        // ---- how it ends: straight from Repairable or Not repairable ----
        // Clint, 2026-09-27: "If once clicks on solved to invoiced, this is where the work done should
        // come in with the time spent and amount to invoice." Details FIRST: it is what the invoice copies.
        new("solved-invoice", "Solved — to be invoiced", Decided, "to-be-invoiced", WorkLane.Bench,
            new[] { StageField.Details, StageField.Minutes, StageField.Amount }, Outcome: "solved", IsDecision: true),
        new("quote-needed", "Quote needed", Decided.Append(GeneralFirst).ToArray(), "quote-needed", WorkLane.Bench,
            NeedsQuoteLines: true, Outcome: "quote", IsDecision: true),
        new("general-invoice", "To be invoiced", new[] { GeneralFirst }, "to-be-invoiced", WorkLane.Bench,
            new[] { StageField.Details, StageField.Minutes, StageField.Amount }, Outcome: "solved", IsDecision: true),
        // ---- recurring task (Clint, 2026-10-07, answer 2): Done closes it and To be invoiced is offered
        //      too, as on General. Their own keys, because the office may press them too: a billing or
        //      readings task is often the office administrator's own, and the bench's rule would leave
        //      them unable to finish the task they were given. ----
        new("recurring-done", "Done", new[] { RecurringFirst }, Closed, WorkLane.Bench, Closes: true, OfficeToo: true),
        new("recurring-invoice", "To be invoiced", new[] { RecurringFirst }, "to-be-invoiced", WorkLane.Bench,
            new[] { StageField.Details, StageField.Minutes, StageField.Amount }, Outcome: "solved", IsDecision: true, OfficeToo: true),
        new("inspection-fee", "Invoice inspection fee", new[] { NotRepairableStage }, "to-be-invoiced", WorkLane.Bench,
            new[] { StageField.Minutes, StageField.Amount }, Outcome: "inspection", IsDecision: true),
        // Clint, 2026-10-13: "a Not Repairable, Damage report needed. This should then have an option for comments, and
        // an option to upload the damage report, which can be mailed directly from there to the client. It then goes to
        // to be invoiced etc." Comments FIRST — they are what the email carries; the time and amount are the invoice's.
        new(DamageReport, DamageReportLabel, new[] { NotRepairableStage }, "to-be-invoiced", WorkLane.Bench,
            new[] { StageField.Comments, StageField.Files, StageField.Minutes, StageField.Amount, StageField.EmailClient },
            Outcome: "damage-report", IsDecision: true),
        new("do-not-invoice", "Do not invoice", new[] { RepairableStage, WorkDoneStage, NotRepairableStage }, "dni-approval", WorkLane.Bench,
            new[] { StageField.Reason }, Outcome: "dni", IsDecision: true),

        // Offered LAST at every bench stage: the buttons are drawn in this order, and the step that
        // moves the job on should come first.
        new("wait-customer", "Waiting on customer", Holdable, "waiting-customer", WorkLane.Bench),
        new("resume", "Back to work", new[] { "waiting-customer" }, "@return", WorkLane.Bench),

        // A rejected quote comes back to the technician with only two ways out — Clint, answer 8.
        // Repairable is left as it was: the machine could be fixed, the customer said no.
        // A General quote turned down has no machine to charge an inspection for: Do not invoice only.
        new("declined-inspection-fee", "Invoice inspection fee", AfterRejection, "to-be-invoiced", WorkLane.Bench,
            new[] { StageField.Minutes, StageField.Amount }, Outcome: "inspection", IsDecision: true, Only: new[] { TicketCategory.Workshop }),
        new("declined-do-not-invoice", "Do not invoice", AfterRejection, "dni-approval", WorkLane.Bench,
            new[] { StageField.Reason }, Outcome: "dni", IsDecision: true),

        // ---- invoicing ----
        new("client-invoiced", "Client invoiced", new[] { "to-be-invoiced" }, "awaiting-payment", WorkLane.Office,
            new[] { StageField.Amount, StageField.Reference }),
        new("payment-received", "Payment received — close ticket", new[] { "awaiting-payment" }, "@close", WorkLane.Office, Closes: true),

        // ---- quotes ----
        new("quote-requested", "Quote requested", new[] { "quote-needed" }, "quote-requested", WorkLane.Office,
            new[] { StageField.SupplierEmail }, NeedsQuoteLines: true),
        new("quote-sent", "Quote sent to client", new[] { "quote-requested" }, "quote-sent", WorkLane.Office,
            new[] { StageField.Amount, StageField.Reference }),
        new("quote-accepted", "Quote accepted", new[] { "quote-sent" }, "invoice-client", WorkLane.Office),
        new("quote-rejected", "Quote rejected", new[] { "quote-sent" }, "quote-rejected", WorkLane.Office),
        new("re-quote", "Re-quote", new[] { "quote-sent" }, "quote-needed", WorkLane.Office),
        new("quote-invoiced", "Client invoiced", new[] { "invoice-client" }, "quote-awaiting-payment", WorkLane.Office,
            new[] { StageField.Amount, StageField.Reference }),
        new("order-placed", "Payment received, order placed with supplier", new[] { "quote-awaiting-payment" }, "order-placed", WorkLane.Office),
        new(OrderParts, "Order placed with supplier", new[] { OrderParts }, PartsOnOrder, WorkLane.Office),
        new("parts-received", "Parts received, customer notified", new[] { "order-placed", PartsOnOrder }, "parts-received", WorkLane.Office),
        new("parts-fitted", "Parts fitted", new[] { "parts-received" }, "parts-fitted", WorkLane.Bench),
        new("complete", "Ticket complete — close", new[] { "parts-fitted" }, "@close", WorkLane.Office, Closes: true),

        // ---- do not invoice ----
        new("approve-dni", "Approve: do not invoice", new[] { "dni-approval" }, "dni-to-close", WorkLane.Supervisor),
        // Back to where the technician decided: Repairable, or Not repairable.
        new("decline-dni", "Decline: back to the technician", new[] { "dni-approval" }, "@decision", WorkLane.Supervisor,
            new[] { StageField.Reason }),
        new("close-dni", "Close ticket", new[] { "dni-to-close" }, "@close", WorkLane.Office, Closes: true),

        // ---- call out (Clint, 2026-09-27): "once the ticket is created, the next stage should be
        //      work done. After work done, it should have the options, Ticket Completed to be
        //      invoiced, Invoiced Paid Onsite, or Needs Workshop Work." ----
        // Work done asks the time as well as the work, so every call out has its time whichever
        // way it ends (Clint's choice); "to be invoiced" then asks only the amount.
        new("callout-work-done", "Work done", new[] { CallOutFirst }, CallOutWorkDone, WorkLane.Bench,
            new[] { StageField.Details, StageField.Minutes }),
        // Clint, 2026-10-05: "Call outs need the option to reschedule" — a new date and time, and why
        // (his choice). Before anybody has been out, so at Call out booked only, and it stays there.
        // The office too: it is usually the customer phoning in who moves it, and the office who answers.
        new(Reschedule, "Reschedule", new[] { CallOutFirst }, "@stay", WorkLane.Bench,
            new[] { StageField.Appointment, StageField.Reason }, OfficeToo: true),
        // The workshop's invoicing route from here on: To be invoiced → Client invoiced → Awaiting
        // payment → Payment received. A call out has no machine to collect, so it then closes.
        new("callout-invoice", "Ticket completed — to be invoiced", new[] { CallOutWorkDone }, "to-be-invoiced", WorkLane.Bench,
            new[] { StageField.Amount }, Outcome: "solved", IsDecision: true),
        new("callout-paid-onsite", "Invoiced — paid on site", new[] { CallOutWorkDone }, PaidOnSite, WorkLane.Bench,
            Outcome: "paid-onsite", IsDecision: true),
        // "If Paid Cash is selected, it must ask for the amount. If Paid Card Machine is selected, it
        // must ask for the amount and Card Machine Reference Number." Then the office closes it.
        // The walk in's "Invoiced — paid at counter" uses the same two, landing on its own stage (@paid).
        new("paid-cash", "Paid cash", new[] { PaidOnSite, PaidCounter }, "@paid", WorkLane.Bench,
            new[] { StageField.Amount }),
        new("paid-card", "Paid card machine", new[] { PaidOnSite, PaidCounter }, "@paid", WorkLane.Bench,
            new[] { StageField.Amount, StageField.Reference }),
        new("close-paid-onsite", "Close ticket", new[] { PaidOnSiteToClose, PaidCounterToClose }, Closed, WorkLane.Office, Closes: true),
        // "If Needs Workshop Work is selected, it must follow the workshop workflow." The SAME ticket
        // becomes a Workshop job at Booked in, keeping its number and its story, after the questions
        // a workshop booking asks — and, like one, three photos of the machine as it arrives.
        new(ToWorkshop, "Needs workshop work", new[] { CallOutWorkDone }, First, WorkLane.Bench,
            new[] { StageField.AssetMake, StageField.AssetModel, StageField.AssetSerial, StageField.Accessories, StageField.DevicePassword }),

        // ---- walk in (Clint, 2026-09-27): "Once the details are captured, there should be two
        //      options, Repaired at Counter or Needs to be booked in. If repaired at counter is
        //      selected, Work done and Time spent need to appear. After that, it's To be Invoiced,
        //      Invoiced Paid at Counter, or No Invoice." ----
        new("walkin-repaired", "Repaired at counter", new[] { WalkInFirst }, WalkInRepaired, WorkLane.Bench,
            new[] { StageField.Details, StageField.Minutes }),
        // "Needs to be booked in, follows the Workshop Route" — the same change as a call out's.
        new(BookInFromWalkIn, "Needs to be booked in", new[] { WalkInFirst }, First, WorkLane.Bench,
            new[] { StageField.AssetMake, StageField.AssetModel, StageField.AssetSerial, StageField.Accessories, StageField.DevicePassword }),
        new("walkin-invoice", "To be invoiced", new[] { WalkInRepaired }, "to-be-invoiced", WorkLane.Bench,
            new[] { StageField.Amount }, Outcome: "solved", IsDecision: true),
        new("walkin-paid-counter", "Invoiced — paid at counter", new[] { WalkInRepaired }, PaidCounter, WorkLane.Bench,
            Outcome: "paid-counter", IsDecision: true),
        // "No Invoice, asks for reason why just like the workshop workflow" — and, like it, waits
        // for a supervisor's approval.
        new("walkin-no-invoice", "No invoice", new[] { WalkInRepaired }, "dni-approval", WorkLane.Bench,
            new[] { StageField.Reason }, Outcome: "dni", IsDecision: true),

        // ---- remote support (Clint, 2026-09-27): "After the session is completed, it should have To
        //      be Invoiced, Do not Invoice (SLA Client), Do not Invoice or link to Parent Ticket". ----
        // The time is on the ticket already, from its sessions; what was done is what the invoice needs.
        new("remote-invoice", "To be invoiced", new[] { RemoteFirst }, "to-be-invoiced", WorkLane.Bench,
            new[] { StageField.Details, StageField.Amount }, Outcome: "solved", IsDecision: true),
        // The contract covers it: nothing to explain, nobody to ask — straight to the office to close.
        new("remote-sla", "Do not invoice (SLA client)", new[] { RemoteFirst }, "dni-to-close", WorkLane.Bench,
            Outcome: "sla", IsDecision: true),
        new("remote-dni", "Do not invoice", new[] { RemoteFirst }, "dni-approval", WorkLane.Bench,
            new[] { StageField.Reason }, Outcome: "dni", IsDecision: true),
        // Part of another open job of the same client: filed under it, and — since 2026-10-09 — LEFT OPEN where it
        // is, to be finished and closed on its own; the job cannot close until it is. It used to close here
        // (2026-09-27), which is how a parent came to be "closed with its child still open, closing both":
        // the child had closed the moment it was linked. Clint chose the other way when that was explained.
        new(LinkParent, "Link to parent ticket", new[] { RemoteFirst }, "@stay", WorkLane.Bench,
            new[] { StageField.ParentTicket }),

        // ---- delivery (Clint, 2026-09-30): "normally delivery is because a printer needs to be
        //      installed or something else, so work done needs to be there" — and it can be paid
        //      before delivery or on it. Out for delivery itself is ActionsFor's, beside Machine
        //      collected, because it needs the photos out and belongs to whoever may mark collected. ----
        new("delivered", "Delivered — work done", new[] { OutForDelivery }, DeliveredStage, WorkLane.Bench,
            new[] { StageField.Details, StageField.Minutes }),
        new("not-delivered", "Not delivered — back to the office", new[] { OutForDelivery }, "@return", WorkLane.Bench,
            new[] { StageField.Reason }),
        // The delivery's own work, to invoice: added to the invoice if the office has not sent it yet,
        // a new one if it has. Not offered while an invoice is out and unpaid (TicketService.Offered).
        new("delivery-invoice", "To be invoiced", new[] { DeliveredStage }, "to-be-invoiced", WorkLane.Bench,
            new[] { StageField.Amount }),
        // Then Paid cash or Paid card machine, and the office closes it — the call out's own steps.
        new("delivery-paid", "Invoiced — paid on delivery", new[] { DeliveredStage }, PaidOnSite, WorkLane.Bench),
        // Paid before delivery, or still with the office: back to wherever the billing was.
        new("delivery-done", "Nothing more to invoice", new[] { DeliveredStage }, "@return", WorkLane.Bench),

        // ---- the ends ----
        new("close", "Close ticket", new[] { AwaitingCollection }, Closed, WorkLane.Office, Closes: true),
        // From anywhere still open. No timer — Clint, answer 20: three months' grace at Anywhere,
        // other companies differ.
        new("abandon", "Abandoned", new[] { "*" }, Abandoned, WorkLane.Office, new[] { StageField.Reason }, Closes: true),
    };

    public static StageDef? Stage(string key) => Stages.FirstOrDefault(s => s.Key == key);
    public static ActionDef? Action(string key) => Actions.FirstOrDefault(a => a.Key == key);
    public static string Label(string key) => Stage(key)?.Label ?? "";

    /// <summary>Whether a category has a route at all. Keyed on the VALUE, never display order (§42).</summary>
    /// Every category has one since 2026-09-27.
    public static bool Has(TicketCategory c) => c is TicketCategory.Workshop or TicketCategory.CallOut
        or TicketCategory.WalkIn or TicketCategory.RemoteSupport or TicketCategory.General or TicketCategory.Recurring;

    /// <summary>Where a new ticket of a category with a route starts.</summary>
    public static string FirstFor(TicketCategory c) => c switch
    {
        TicketCategory.CallOut => CallOutFirst,
        TicketCategory.WalkIn => WalkInFirst,
        TicketCategory.RemoteSupport => RemoteFirst,
        TicketCategory.General => GeneralFirst,
        TicketCategory.Recurring => RecurringFirst,
        _ => First
    };

    /// <summary>
    /// Where a ticket with no stage yet belongs, from the plain status it had before workflows —
    /// Clint, answer 21: open workshop jobs start at the beginning, or — once work has started — at
    /// Inspected, where the next question is "Repairable or not?".
    /// </summary>
    public static string FromStatus(TicketStatus s) => s switch
    {
        TicketStatus.Open => First,
        TicketStatus.Closed => Closed,
        _ => Inspected
    };

    /// <summary>
    /// Whether this person may press buttons that belong to <paramref name="lane"/> on this ticket.
    ///
    /// The bench is the people on the job's — any of them, since 2026-09-28 — and a supervisor's or the owner's, who may step in
    /// anywhere. The billing queue is every office administrator's, and a supervisor's or the
    /// owner's (answer 15). A supervisor's decision is a supervisor's or the owner's. A technician
    /// never closes anything: at the end of the day only the office administrator closes off
    /// tickets — Clint, 2026-09-24 — with the owner and supervisors beside them.
    /// </summary>
    public static bool MayAct(WorkLane lane, Ticket t, TenantRole role, string me) => lane switch
    {
        WorkLane.Bench => Permissions.CanStepInAnywhere(role)
                          || (Permissions.CanChangeStatus(role) && Assignees.Has(t.AssignedTo, me)),
        WorkLane.Office => Permissions.CanWorkBilling(role),
        WorkLane.Supervisor => Permissions.CanApprove(role),
        _ => false
    };

    /// <summary>Whether this person may press this button on this ticket: its lane's people, and — for
    /// the few steps marked <see cref="ActionDef.OfficeToo"/> — the office as well.</summary>
    public static bool MayPress(ActionDef a, Ticket t, TenantRole role, string me) =>
        MayAct(a.Performer, t, role, me) || (a.OfficeToo && Permissions.CanWorkBilling(role));

    /// <summary>
    /// Marking the machine collected. Once the bench is done with it, by the office administrator
    /// or the technician — whoever is standing at the counter when the customer arrives.
    /// </summary>
    public static bool MayMarkCollected(Ticket t, TenantRole role, string me) =>
        Permissions.CanWorkBilling(role)
        || (Permissions.CanChangeStatus(role) && me.Length > 0 && (Assignees.Has(t.AssignedTo, me) || Assignees.Has(t.WorkedBy, me)));

    public static string FieldLabel(StageField f) => f switch
    {
        StageField.Minutes => "Time spent (minutes)",
        StageField.Amount => "Amount",
        StageField.Reference => "Reference or invoice number",
        StageField.Reason => "Reason",
        StageField.SupplierEmail => "Suppliers",
        StageField.Repairable => "Repairable?",
        StageField.Details => "Details",
        StageField.Appointment => "New date and time",
        StageField.Comments => "Comments",
        StageField.Files => "The damage report",
        StageField.EmailClient => "Email it to the client",
        _ => f.ToString()
    };
}

/// <summary>What was typed in alongside a button. Only the fields the action asks for are read.</summary>
public sealed class StageInput
{
    public int? Minutes { get; set; }
    public decimal? Amount { get; set; }
    public string? Reference { get; set; }
    public string? Reason { get; set; }
    /// <summary>Every supplier asked: one address, or several joined by ", " (they then go in BCC).</summary>
    public string? SupplierEmail { get; set; }
    /// <summary>The same suppliers by name, joined by ", " — for the timeline. Optional.</summary>
    public string? SupplierNames { get; set; }
    /// <summary>The body of a step's form: what was found, what was done.</summary>
    public string? Details { get; set; }
    public bool? Repairable { get; set; }
    // A call out becoming a workshop job: the booking's questions.
    public string? AssetMake { get; set; }
    public string? AssetModel { get; set; }
    public string? AssetSerial { get; set; }
    public string? Accessories { get; set; }
    /// <summary>ALREADY ENCRYPTED by the caller (ClientService.Protect), or empty for no password.
    /// The service never sees the password itself.</summary>
    public string? DevicePasswordEnc { get; set; }
    /// <summary>The ticket to file this one under (Link to parent ticket).</summary>
    public int? ParentTicketId { get; set; }
    /// <summary>Add another machine: where its photos were staged as they were picked.</summary>
    public string? PhotoStageId { get; set; }
    /// <summary>Add another machine: true puts it on THIS ticket, as part of the same job (a data
    /// transfer's old and new machine); false makes it a ticket of its own, booked in beside this one.</summary>
    public bool SameJob { get; set; }
    /// <summary>Reschedule: the new appointment, in UTC. The portal reads what was typed in the
    /// company's own time zone (CompanyClock.ToUtc); the console sends its PC's clock as UTC.</summary>
    public DateTime? AppointmentUtc { get; set; }
    /// <summary>Comments that may be empty (Damage report needed).</summary>
    public string? Comments { get; set; }
    /// <summary>Files picked for the step, by id — loose on the ticket, picked by the person pressing it.</summary>
    public List<long>? AttachmentIds { get; set; }
    /// <summary>Who the damage report is emailed to, or empty for nobody. The CALLER sends it once the step is
    /// recorded (ClientUpdateService); the service only checks there is a report to send.</summary>
    public string? EmailTo { get; set; }
}
