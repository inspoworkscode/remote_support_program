using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>What switching one thing off does: skip a step, take away one choice, stop asking for something, or a whole category.</summary>
public enum JobSwitchKind { Category, Skip, Choice, Rule }

/// <param name="Key">What is STORED in <c>Tenant.JobTypesOff</c> and <c>Ticket.RouteOff</c>. Never rename one.</param>
/// <param name="Group">The card it is drawn on: a category's name, "Quotes" or "Every category".</param>
/// <param name="Off">What happens with it off, in the words the page shows.</param>
public sealed record JobSwitch(string Key, string Label, JobSwitchKind Kind, string Group, string Off);

/// <summary>One line on a card: a switch (<paramref name="Switch"/> its key) or a locked step, with why it cannot go.</summary>
public sealed record JobLine(string Label, string? Switch, string Locked = "")
{
    public bool IsLocked => Switch == null;
}

/// <summary>
/// One card on the Job types page, its lines in route order. <paramref name="Category"/> is the category it is about, or null
/// (Quotes, Every category). <paramref name="CategorySwitch"/> is the switch for the whole category, or null when it is locked
/// on — then <paramref name="AlwaysOn"/> says why.
/// </summary>
public sealed record JobCard(string Title, TicketCategory? Category, string? CategorySwitch, string AlwaysOn, string Intro, JobLine[] Lines);

/// <summary>
/// Job types (2026-10-27, HANDOVER §166, brief <c>docs/NEXT-SESSION-JOBTYPES.md</c>). Clint: a company that does not want every
/// category, or every stage of one, chooses — <i>"without breaking the flow."</i> EVERY rule about it is here.
///
/// <list type="bullet">
/// <item>What is stored is what is OFF (<c>Tenant.JobTypesOff</c>), so a switch added later is on for every company.</item>
/// <item>A ticket keeps the company's switches as they were when it was opened (<c>Ticket.RouteOff</c>): a job finishes
/// the route it started on. Empty — every ticket from before — is everything on.</item>
/// <item>CATEGORIES are asked live (<see cref="CategoryRefusal"/>), steps of the ticket's copy. A job of a category since
/// switched off carries on to its end; nothing new is opened in it, changed into it or converted into it.</item>
/// <item><see cref="Problem"/> is the only judge of whether a set of switches leaves every route able to finish, and finish
/// billed or deliberately not. Locked things are not switches at all, so they cannot be switched off.</item>
/// </list>
/// The route itself is still <see cref="Workflow"/>, defined once; this only takes buttons away and points a few elsewhere.
/// </summary>
public static class JobTypes
{
    // ---- the categories that may be switched off. Remote support and General are locked on: sessions, calls, emails,
    //      website chats and contact forms open them by themselves, and a new ticket with nothing chosen is General. ----
    public const string Workshop = "cat-workshop", CallOut = "cat-callout", WalkIn = "cat-walkin", Recurring = "cat-recurring";

    // ---- Workshop ----
    public const string PhotosIn = "photos-in", AddMachine = "add-machine", Inspection = "inspection", DamageReport = "damage-report",
        InspectionFee = "inspection-fee", WaitCustomer = "wait-customer", Delivery = "delivery", PhotosOut = "photos-out";
    // ---- Quotes (Workshop's Repairable, and General) ----
    public const string Quotes = "quotes", QuoteSuppliers = "quote-suppliers", QuotePayFirst = "quote-pay-first";
    // ---- Call out, Walk in, Remote support ----
    public const string Reschedule = "reschedule", PaidOnSite = "paid-onsite", CallOutToWorkshop = "callout-to-workshop",
        WalkInToWorkshop = "walkin-to-workshop", PaidCounter = "paid-counter", RemoteSla = "remote-sla", RemoteLink = "remote-link";
    // ---- every category ----
    public const string TrackPayment = "track-payment", DniApproval = "dni-approval";

    public const string GroupAll = "Every category", GroupQuotes = "Quotes";

    /// <summary>Every switch, in the order the page draws them: by card, each card in route order.</summary>
    public static readonly JobSwitch[] Switches =
    {
        new(Workshop, "Workshop", JobSwitchKind.Category, "Workshop",
            "No new workshop jobs, and call outs and walk ins cannot be booked in to the workshop."),
        new(PhotosIn, "Photos when booked in", JobSwitchKind.Rule, "Workshop",
            "A machine is booked in without photos. Photos can still be added."),
        new(AddMachine, "Add another machine", JobSwitchKind.Choice, "Workshop", "Not offered at Booked in."),
        new(Inspection, "Inspection findings", JobSwitchKind.Skip, "Workshop",
            "Booked in offers Repairable and Not repairable straight away."),
        new(DamageReport, "Damage report needed", JobSwitchKind.Choice, "Workshop", "Not offered at Not repairable."),
        new(InspectionFee, "Invoice inspection fee", JobSwitchKind.Choice, "Workshop",
            "Not offered at Not repairable, or after a rejected quote."),
        new(WaitCustomer, "Waiting on customer", JobSwitchKind.Choice, "Workshop", "Not offered on the bench."),
        new(Delivery, "Out for delivery", JobSwitchKind.Choice, "Workshop", "Only Machine collected is offered when the machine is ready."),
        new(PhotosOut, "Photos when collected or delivered", JobSwitchKind.Rule, "Workshop",
            "A machine is collected or sent out without photos, and Close does not wait for them."),

        new(Quotes, "Quote needed", JobSwitchKind.Choice, GroupQuotes, "No quotes, in Workshop or General."),
        new(QuoteSuppliers, "Ask suppliers for prices", JobSwitchKind.Skip, GroupQuotes,
            "Quote needed goes straight to Quote sent to client."),
        new(QuotePayFirst, "Payment before ordering parts", JobSwitchKind.Skip, GroupQuotes,
            "Quote accepted goes straight to ordering the parts, and the job is invoiced once they are fitted."),

        new(CallOut, "Call out", JobSwitchKind.Category, "Call out", "No new call outs."),
        new(Reschedule, "Reschedule", JobSwitchKind.Choice, "Call out", "Not offered."),
        new(PaidOnSite, "Invoiced — paid on site", JobSwitchKind.Choice, "Call out", "Not offered."),
        new(CallOutToWorkshop, "Needs workshop work", JobSwitchKind.Choice, "Call out", "Not offered."),

        new(WalkIn, "Walk in", JobSwitchKind.Category, "Walk in", "No new walk ins."),
        new(WalkInToWorkshop, "Needs to be booked in", JobSwitchKind.Choice, "Walk in", "Not offered."),
        new(PaidCounter, "Invoiced — paid at counter", JobSwitchKind.Choice, "Walk in", "Not offered."),

        new(RemoteSla, "Do not invoice (SLA client)", JobSwitchKind.Choice, "Remote support", "Not offered."),
        new(RemoteLink, "Link to parent ticket", JobSwitchKind.Choice, "Remote support",
            "Not offered as a step. The ticket's own Part of box still links it."),

        new(Recurring, "Recurring task", JobSwitchKind.Category, "Recurring task",
            "The schedules stop opening tickets, and none can be set up. They are kept: switched on again, each opens on its next due day."),

        new(TrackPayment, "Track payment after invoicing", JobSwitchKind.Skip, GroupAll,
            "Client invoiced finishes the job instead of waiting for the payment."),
        new(DniApproval, "Supervisor approval for Do not invoice", JobSwitchKind.Skip, GroupAll,
            "Do not invoice goes straight to the office to close. The reason is still asked."),
    };

    /// <summary>
    /// The page, card by card, every line in route order: every switch exactly once (on the card its Group names), and the
    /// steps that cannot be switched off with the reason the page shows. <c>jobtypecheck</c> holds it to that.
    /// </summary>
    public static readonly JobCard[] Cards =
    {
        new("General", TicketCategory.General, null,
            "Always on: emails, website chats and new tickets with nothing chosen land here.",
            "An instruction to somebody, a quote, or something to invoice. No client needed.",
            new JobLine[]
            {
                new("Open", null, "Where every General ticket starts."),
                new("Done", null, "An instruction carried out closes with nothing to bill."),
                new("To be invoiced", null, "The way a General job is billed."),
            }),
        new("Remote support", TicketCategory.RemoteSupport, null,
            "Always on: every remote session opens one of these by itself.",
            "A session's ticket, and how it ends.",
            new JobLine[]
            {
                new("Remote session", null, "Where every session's ticket starts."),
                new("To be invoiced", null, "The way a session is billed."),
                new("Do not invoice", null, "Every job needs a way to end without a bill."),
                new("Do not invoice (SLA client)", RemoteSla),
                new("Link to parent ticket", RemoteLink),
            }),
        new("Workshop", TicketCategory.Workshop, Workshop, "",
            "A machine booked in, worked on at the bench, and collected.",
            new JobLine[]
            {
                new("Booked in", null, "Where every workshop job starts."),
                new("Photos when booked in", PhotosIn),
                new("Add another machine", AddMachine),
                new("Inspection findings", Inspection),
                new("Repairable / Not repairable", null, "This is where the job decides how it ends."),
                new("Solved — to be invoiced", null, "The way a repaired machine is billed."),
                new("Damage report needed", DamageReport),
                new("Invoice inspection fee", InspectionFee),
                new("Do not invoice", null, "Every job needs a way to end without a bill."),
                new("Waiting on customer", WaitCustomer),
                new("Out for delivery", Delivery),
                new("Machine collected", null, "A finished machine has to leave somehow."),
                new("Photos when collected or delivered", PhotosOut),
            }),
        new("Call out", TicketCategory.CallOut, CallOut, "",
            "Somebody goes out to the client and does the work there.",
            new JobLine[]
            {
                new("Call out booked, Work done", null, "Somebody went out and did something: that is the call out."),
                new("Reschedule", Reschedule),
                new("Ticket completed — to be invoiced", null, "The way a call out is billed."),
                new("Invoiced — paid on site", PaidOnSite),
                new("Needs workshop work", CallOutToWorkshop),
            }),
        new("Walk in", TicketCategory.WalkIn, WalkIn, "",
            "Somebody brings a machine to the counter.",
            new JobLine[]
            {
                new("At the counter, Repaired at counter", null, "Fixed while they wait: that is the walk in."),
                new("Needs to be booked in", WalkInToWorkshop),
                new("To be invoiced", null, "The way a walk in is billed."),
                new("Invoiced — paid at counter", PaidCounter),
                new("No invoice", null, "Every job needs a way to end without a bill."),
            }),
        new("Recurring task", TicketCategory.Recurring, Recurring, "",
            "A ticket opened by itself every day, week or month — maintenance, billing, readings.",
            new JobLine[]
            {
                new("Due", null, "Where every recurring ticket starts."),
                new("Done, To be invoiced", null, "How a recurring task finishes."),
            }),
        new(GroupQuotes, null, null, "",
            "For Workshop's Repairable and for General: asking suppliers, telling the client the price, ordering the parts.",
            new JobLine[]
            {
                new("Quote needed", Quotes),
                new("Ask suppliers for prices", QuoteSuppliers),
                new("Payment before ordering parts", QuotePayFirst),
            }),
        new(GroupAll, null, null, "",
            "How the office finishes a job.",
            new JobLine[]
            {
                new("Track payment after invoicing", TrackPayment),
                new("Supervisor approval for Do not invoice", DniApproval),
                new("Abandoned, Back", null, "A job can always be walked away from, or a step undone."),
            }),
    };

    /// <summary>A switch by its key, or null.</summary>
    public static JobSwitch? Switch(string key) => Switches.FirstOrDefault(s => s.Key == key);

    /// <summary>The category each category switch stands for.</summary>
    private static readonly Dictionary<TicketCategory, string> CategorySwitch = new()
    {
        [TicketCategory.Workshop] = Workshop, [TicketCategory.CallOut] = CallOut,
        [TicketCategory.WalkIn] = WalkIn, [TicketCategory.Recurring] = Recurring,
    };

    /// <summary>The step each switch takes away, by the step's key. Steps not here are never switched off.</summary>
    private static readonly Dictionary<string, string> StepSwitch = new()
    {
        ["inspection"] = Inspection,
        [Workflow.DamageReport] = DamageReport,
        ["inspection-fee"] = InspectionFee, ["declined-inspection-fee"] = InspectionFee,
        ["wait-customer"] = WaitCustomer,
        ["quote-needed"] = Quotes,
        ["quote-requested"] = QuoteSuppliers,
        [Workflow.Reschedule] = Reschedule,
        ["callout-paid-onsite"] = PaidOnSite,
        [Workflow.ToWorkshop] = CallOutToWorkshop,
        [Workflow.BookInFromWalkIn] = WalkInToWorkshop,
        ["walkin-paid-counter"] = PaidCounter,
        ["remote-sla"] = RemoteSla,
        [Workflow.LinkParent] = RemoteLink,
    };

    private static readonly HashSet<string> Known = Switches.Select(s => s.Key).ToHashSet();

    // ================================================================ reading the stored list

    private static readonly Dictionary<string, HashSet<string>> Parsed = new();

    /// <summary>The switches off in a stored list. Cached: the same few strings are asked thousands of times a page.</summary>
    public static HashSet<string> Parse(string? off)
    {
        off ??= "";
        lock (Parsed)
        {
            if (Parsed.TryGetValue(off, out var set)) return set;
            if (Parsed.Count > 4096) Parsed.Clear();
            set = off.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(Known.Contains).ToHashSet();
            Parsed[off] = set;
            return set;
        }
    }

    public static bool On(string? off, string key) => !Parse(off).Contains(key);

    /// <summary>
    /// The list to store: known keys only, in a fixed order, and none belonging to a category that is itself off — that
    /// category's steps are not kept switched off behind its back, so switching it on again brings it back whole.
    /// </summary>
    public static string Clean(IEnumerable<string> off)
    {
        var set = off.Select(k => k.Trim()).Where(Known.Contains).ToHashSet();
        var categoriesOff = Switches.Where(s => s.Kind == JobSwitchKind.Category && set.Contains(s.Key)).Select(s => s.Group).ToHashSet();
        return string.Join(",", Switches.Where(s => set.Contains(s.Key) && (s.Kind == JobSwitchKind.Category || !categoriesOff.Contains(s.Group)))
            .Select(s => s.Key));
    }

    // ================================================================ categories (asked of the COMPANY, live)

    public static bool CategoryOn(string? off, TicketCategory c) => !CategorySwitch.TryGetValue(c, out var key) || On(off, key);

    /// <summary>Why a ticket of this category cannot be opened, changed into or converted into, or empty for yes.</summary>
    public static string CategoryRefusal(string? off, TicketCategory c) => CategoryOn(off, c) ? ""
        : $"Your company has switched {TicketService.CategoryName(c)} off (Job types), so " +
          (c == TicketCategory.Recurring ? "no recurring task can be set up. " : "a ticket cannot be made one. ") +
          "A supervisor or the owner can switch it on again.";

    // ================================================================ steps (asked of the TICKET's copy)

    /// <summary>
    /// Whether this route offers this step at all, with these switches. A step leaving a stage the job is already on is
    /// never taken away by anything but its own switch — and a job only reaches a stage its own switches allowed.
    /// </summary>
    public static bool Allows(ActionDef a, string? off)
    {
        if (StepSwitch.TryGetValue(a.Key, out var key) && !On(off, key)) return false;
        // Booked in to the workshop: only while the copy had the workshop. The company's switch NOW is asked by ActAsync.
        if (Workflow.ToWorkshopKeys.Contains(a.Key) && !On(off, Workshop)) return false;
        return true;
    }

    /// <summary>Whether a step is offered from this stage: its own stages, and the ones a skipped step leaves it to answer.</summary>
    public static bool FromHere(ActionDef a, string stage, string? off) =>
        a.From.Contains(stage) || a.From.Contains("*")
        // Inspection findings skipped: Booked in asks Repairable or not itself.
        || (stage == Workflow.First && a.Key is "repairable" or "not-repairable" && !On(off, Inspection))
        // No suppliers asked: the price is sent to the client straight from Quote needed.
        || (stage == "quote-needed" && a.Key == "quote-sent" && !On(off, QuoteSuppliers));

    /// <summary>
    /// Where a step goes with these switches, BEFORE its "@" words are resolved (<c>TicketService.TargetOf</c> does that).
    /// Every skipped step that is not simply a missing button is here.
    /// </summary>
    public static string ToOf(ActionDef a, string? off)
    {
        if (a.To == "dni-approval" && !On(off, DniApproval)) return "dni-to-close";
        if (a.Key == "client-invoiced" && !On(off, TrackPayment)) return "@close";
        if (!On(off, QuotePayFirst))
        {
            if (a.Key == "quote-accepted") return Workflow.OrderParts;
            // Nothing has been invoiced on this road yet: once the parts are in, the job is billed like any other.
            if (a.Key == "parts-fitted") return "to-be-invoiced";
        }
        return a.To;
    }

    public static bool PhotosInRequired(string? off) => On(off, PhotosIn);
    public static bool PhotosOutRequired(string? off) => On(off, PhotosOut);

    // ================================================================ the judge

    /// <summary>
    /// Whether these switches leave every category that is on able to finish. Empty means yes; otherwise which category
    /// and which step, in words. Asked before saving, by the page and by the service.
    /// </summary>
    public static string Problem(string? off)
    {
        foreach (var c in Categories)
        {
            if (!CategoryOn(off, c)) continue;
            var p = ProblemFor(c, off);
            if (p.Length > 0) return p;
        }
        return "";
    }

    /// <summary>The categories with a route, in the order the page and the judge go through them.</summary>
    public static readonly TicketCategory[] Categories =
    {
        TicketCategory.General, TicketCategory.RemoteSupport, TicketCategory.Workshop,
        TicketCategory.CallOut, TicketCategory.WalkIn, TicketCategory.Recurring
    };

    /// <summary>One position a job can be in, as far as the route cares: what <c>TargetOf</c> and the gates read.</summary>
    private sealed record Spot(TicketCategory Cat, string Stage, string Return, bool? Repairable, bool Collected, bool Settled);

    /// <summary>
    /// Walks one category's route from its first stage, through every step the switches leave — the real steps
    /// (<c>TicketService.RouteSteps</c>) to the real places (<c>TicketService.TargetOf</c>, or <paramref name="target"/>
    /// for a harness proving a broken route is caught). Abandoned and Back are left out: they finish nothing.
    /// It refuses when a job could get somewhere it can never finish from, or reach Closed without being billed or
    /// deliberately not billed.
    /// </summary>
    public static string ProblemFor(TicketCategory category, string? off, Func<ActionDef, Ticket, string>? target = null) =>
        Walk(category, off, target).Problem;

    /// <summary>Every stage a job of this category can reach with these switches (Abandoned and Back aside) — what the
    /// page draws a route from, and what a harness compares to say what a switch changed.</summary>
    public static IReadOnlySet<string> StagesReached(TicketCategory category, string? off) => Walk(category, off, null).Reached;

    /// <summary>
    /// The way a job of this category USUALLY goes with these switches, stage by stage, for the line under each card:
    /// at each stage the first step the route offers (Workflow.Actions is in that order on purpose — the step that moves
    /// the job on is drawn first), never Abandoned, waiting, a step that stays where it is, or one back. A finished
    /// workshop machine is collected when nothing else is left. <paramref name="through"/> is a step taken whenever it is
    /// offered — "quote-needed" for the Quotes card's line. Built from the same RouteSteps and TargetOf as everything else.
    /// </summary>
    public static List<string> Path(TicketCategory category, string? off, string? through = null)
    {
        off ??= "";
        var t = new Ticket { Category = category, Stage = Workflow.FirstFor(category), RouteOff = off };
        var path = new List<string> { Workflow.Label(t.Stage) };
        for (int n = 0; n < 40 && Workflow.Stage(t.Stage) is { } stage && stage.Lane != WorkLane.Done; n++)
        {
            var steps = TicketService.RouteSteps(t)
                .Where(a => a.Key is not ("abandon" or "wait-customer") && a.To != "@stay" && (a.Key != "close" || t.CollectedUtc != null))
                .ToList();
            var a = steps.FirstOrDefault(x => x.Key == through) ?? steps.FirstOrDefault();
            if (a == null)
            {
                if (stage.MachineReady && t.Category == TicketCategory.Workshop && t.CollectedUtc == null) { t.CollectedUtc = DateTime.UnixEpoch; continue; }
                break;
            }
            var to = TicketService.TargetOf(a, t);
            if (a.SetsRepairable is bool r) t.Repairable = r;
            if (Workflow.ToWorkshopKeys.Contains(a.Key)) t.Category = TicketCategory.Workshop;
            t.Stage = to;
            var label = Workflow.Label(to);
            if (path[^1] != label) path.Add(label);
        }
        return path;
    }

    private static (string Problem, HashSet<string> Reached) Walk(TicketCategory category, string? off, Func<ActionDef, Ticket, string>? target)
    {
        target ??= TicketService.TargetOf;
        off ??= "";
        var start = new Spot(category, Workflow.FirstFor(category), "", null, false, false);
        var seen = new HashSet<Spot> { start };
        var edges = new Dictionary<Spot, List<Spot>>();
        var queue = new Queue<Spot>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var s = queue.Dequeue();
            var next = new List<Spot>();
            edges[s] = next;
            if (Workflow.Stage(s.Stage) is not { } stage)
                return ($"{Name(category)}: a job would reach a stage that does not exist ({s.Stage}).", Reached());
            if (stage.Lane == WorkLane.Done)
            {
                if (s.Stage == Workflow.Closed && !s.Settled)
                    return ($"{Name(category)}: a job could be closed without ever being invoiced or marked not to invoice.", Reached());
                continue;
            }
            var t = new Ticket
            {
                Category = s.Cat, Stage = s.Stage, StageReturn = s.Return, Repairable = s.Repairable,
                CollectedUtc = s.Collected ? DateTime.UnixEpoch : null, RouteOff = off
            };
            foreach (var a in TicketService.RouteSteps(t))
            {
                if (a.Key == "abandon" || a.To == "@stay") continue;
                if (a.Key == "close" && !s.Collected) continue;   // ActAsync refuses it until the machine has gone
                var to = target(a, t);
                next.Add(new Spot(
                    Workflow.ToWorkshopKeys.Contains(a.Key) ? TicketCategory.Workshop : s.Cat,
                    to,
                    a.Key == "wait-customer" ? s.Stage : s.Return,
                    a.SetsRepairable ?? s.Repairable,
                    s.Collected || a.Key == "delivered",
                    s.Settled || Settles(a)));
            }
            // The two ways a finished workshop machine leaves, which are not in Workflow.Actions (ActionsFor adds them).
            if (stage.MachineReady && s.Cat == TicketCategory.Workshop && !s.Collected)
            {
                next.Add(s with { Collected = true });
                if (On(off, Delivery)) next.Add(s with { Stage = Workflow.OutForDelivery, Return = s.Stage });
            }
            foreach (var n in next)
                if (seen.Add(n)) queue.Enqueue(n);
        }

        // Every place a job can reach must be able to reach Closed: walked backwards from Closed.
        var back = new Dictionary<Spot, List<Spot>>();
        foreach (var (from, tos) in edges)
            foreach (var to in tos)
                (back.TryGetValue(to, out var l) ? l : back[to] = new()).Add(from);
        var finishes = new HashSet<Spot>(seen.Where(s => s.Stage == Workflow.Closed));
        var walk = new Queue<Spot>(finishes);
        while (walk.Count > 0)
            foreach (var from in back.TryGetValue(walk.Dequeue(), out var l) ? l : new())
                if (finishes.Add(from)) walk.Enqueue(from);
        var stuck = seen.Where(s => !finishes.Contains(s) && Workflow.Stage(s.Stage)?.Lane != WorkLane.Done)
            .OrderBy(s => s.Stage, StringComparer.Ordinal).FirstOrDefault();
        return (stuck == null ? "" : $"{Name(category)}: a job at \"{Workflow.Label(stuck.Stage)}\" would have no way to finish.", Reached());

        HashSet<string> Reached() => seen.Select(x => x.Stage).ToHashSet();
        static string Name(TicketCategory c) => TicketService.CategoryName(c);
    }

    /// <summary>
    /// A step that settles the money: an amount to invoice or paid (not a quote's price), or a deliberate "no bill" —
    /// do not invoice, an SLA client, or General's and a recurring task's Done, which Clint chose to close unbilled.
    /// </summary>
    private static bool Settles(ActionDef a) =>
        (a.Fields.Contains(StageField.Amount) && a.Key != "quote-sent")
        || a.Outcome is "dni" or "sla"
        || a.Key is "general-done" or "recurring-done";
}
