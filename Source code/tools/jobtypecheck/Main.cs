// =====================================================================================
// jobtypecheck - proves the route check of Job types (HANDOVER 166, brief docs/NEXT-SESSION-JOBTYPES.md):
//
//   Clint: a company chooses its categories and switches off stages "without breaking the flow".
//
// In-process and in memory, against the real Workflow, the real TicketService.RouteSteps and TargetOf, and the real
// JobTypes.Problem. No database: this is the route as a graph.
//
//   1. Every category walked with EVERY combination of the switches it can reach (Call out: 2^14, because it can become
//      a workshop job). None may be refused — the locks exist so no combination breaks — and each is asked twice, once
//      by ProblemFor and once by Problem, so the page's judge and the walk agree.
//   2. A switch not on a category's list changes nothing it reaches (so the lists in 1 leave nothing untried).
//   3. Every switch does something: off alone, a stage goes, or a button goes.
//   4. Each switch does what the brief says: the stages that go, and the ones a skipped step leads to instead.
//   5. The judge CATCHES a broken route: "payment before ordering" off without the Parts fitted change closes a job
//      unbilled; a step sent round in a circle leaves a job that can never finish.
//   6. Clean: unknown keys out, a switched-off category's steps out, a fixed order.
//
//   dotnet run -c Debug --project tools/jobtypecheck/jobtypecheck.csproj
// =====================================================================================

using System.Diagnostics;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

const TicketCategory W = TicketCategory.Workshop, C = TicketCategory.CallOut, K = TicketCategory.WalkIn,
    R = TicketCategory.RemoteSupport, G = TicketCategory.General, Rec = TicketCategory.Recurring;
const TenantRole Boss = TenantRole.Owner | TenantRole.Admin | TenantRole.Billing;

// The switches that can change where a job of each category goes. Workshop's own are reachable from Call out and Walk in
// through "needs workshop work" and "needs to be booked in". Check 2 proves nothing outside a list matters to it.
string[] workshopRoute =
{
    JobTypes.Inspection, JobTypes.DamageReport, JobTypes.InspectionFee, JobTypes.WaitCustomer, JobTypes.Delivery,
    JobTypes.Quotes, JobTypes.QuoteSuppliers, JobTypes.QuotePayFirst, JobTypes.TrackPayment, JobTypes.DniApproval
};
var reach = new Dictionary<TicketCategory, string[]>
{
    [G] = new[] { JobTypes.Quotes, JobTypes.QuoteSuppliers, JobTypes.QuotePayFirst, JobTypes.TrackPayment, JobTypes.DniApproval },
    [R] = new[] { JobTypes.RemoteSla, JobTypes.RemoteLink, JobTypes.TrackPayment, JobTypes.DniApproval },
    [Rec] = new[] { JobTypes.TrackPayment, JobTypes.DniApproval },
    [W] = workshopRoute,
    [C] = workshopRoute.Concat(new[] { JobTypes.Reschedule, JobTypes.PaidOnSite, JobTypes.CallOutToWorkshop, JobTypes.Workshop }).ToArray(),
    [K] = workshopRoute.Concat(new[] { JobTypes.WalkInToWorkshop, JobTypes.PaidCounter, JobTypes.Workshop }).ToArray(),
};
string Off(params string[] keys) => string.Join(",", keys);
Console.WriteLine("jobtypecheck — every route, with every combination of Job types switches");

// =========================================================== 1. every combination
Head("1. every combination of each category's switches leaves a route that finishes, billed or deliberately not");
var clock = Stopwatch.StartNew();
long walks = 0;
foreach (var (cat, keys) in reach)
{
    int combos = 1 << keys.Length, refused = 0;
    string first = "";
    for (int mask = 0; mask < combos; mask++)
    {
        var off = Off(keys.Where((_, i) => (mask & (1 << i)) != 0).ToArray());
        var p = JobTypes.ProblemFor(cat, off);
        walks++;
        if (p.Length > 0) { refused++; if (first.Length == 0) first = $"[{off}] {p}"; }
    }
    Check($"{TicketService.CategoryName(cat)}: all {combos:N0} combinations of its {keys.Length} switches finish", refused == 0,
        refused == 0 ? "" : $"{refused} refused, the first: {first}");
}
// The page's judge: every category at once, over the switches that are not a single category's.
{
    var keys = JobTypes.Switches.Select(s => s.Key).Where(k => k is not (JobTypes.Recurring or JobTypes.WalkIn or JobTypes.CallOut)).ToArray();
    int refused = 0; var rnd = new Random(166);
    for (int n = 0; n < 4000; n++)
    {
        var off = Off(keys.Where(_ => rnd.Next(2) == 0).ToArray());
        if (JobTypes.Problem(JobTypes.Clean(off.Split(','))).Length > 0) refused++;
    }
    Check("JobTypes.Problem (what the page and the service ask): 4,000 random sets of every switch, none refused", refused == 0, $"{refused} refused");
}
Console.WriteLine($"        ({walks:N0} walks in {clock.Elapsed.TotalSeconds:0.0} s)");

// =========================================================== 2. nothing outside a category's list matters to it
Head("2. a switch outside a category's list changes nothing that category reaches");
foreach (var (cat, keys) in reach)
{
    var outside = JobTypes.Switches.Select(s => s.Key).Except(keys).ToList();
    var bases = new[] { "", Off(keys) };
    var moved = new List<string>();
    foreach (var b in bases)
    {
        var before = JobTypes.StagesReached(cat, b);
        foreach (var k in outside)
        {
            var after = JobTypes.StagesReached(cat, b.Length == 0 ? k : b + "," + k);
            if (!after.SetEquals(before)) moved.Add(k);
        }
    }
    Check($"{TicketService.CategoryName(cat)}: none of the {outside.Count} others changes its stages",
        moved.Count == 0, string.Join(", ", moved.Distinct()));
}

// =========================================================== 3. every switch does something
Head("3. every switch, off alone, takes something away");
Ticket At(TicketCategory cat, string stage, string off = "", Action<Ticket>? also = null)
{
    var t = new Ticket { Category = cat, Stage = stage, RouteOff = off, AssignedTo = "Tess Tech" };
    also?.Invoke(t);
    return t;
}
HashSet<string> Keys(Ticket t) => TicketService.ActionsFor(t, Boss, "Boss").Select(a => a.Key).ToHashSet();
bool StageGoes(string key) => JobTypes.Categories.Any(c =>
    JobTypes.CategoryOn("", c) && !JobTypes.StagesReached(c, key).SetEquals(JobTypes.StagesReached(c, "")));
// The switches that move no stage: what they take away is a button, or a rule.
var buttonOf = new Dictionary<string, (Ticket At, string Key)>
{
    [JobTypes.AddMachine] = (At(W, Workflow.First), TicketService.AddMachineKey),
    [JobTypes.Reschedule] = (At(C, Workflow.CallOutFirst), Workflow.Reschedule),
    [JobTypes.RemoteLink] = (At(R, Workflow.RemoteFirst), Workflow.LinkParent),
    [JobTypes.DamageReport] = (At(W, Workflow.NotRepairableStage), Workflow.DamageReport),
    // These three land on stages the route reaches another way too (To be invoiced; Paid on site by delivery; the office's
    // close by Do not invoice), so what goes is the button.
    [JobTypes.InspectionFee] = (At(W, Workflow.NotRepairableStage), "inspection-fee"),
    [JobTypes.PaidOnSite] = (At(C, Workflow.CallOutWorkDone), "callout-paid-onsite"),
    [JobTypes.RemoteSla] = (At(R, Workflow.RemoteFirst), "remote-sla"),
};
foreach (var s in JobTypes.Switches)
{
    if (s.Kind == JobSwitchKind.Category)
    {
        var cat = new[] { W, C, K, Rec }.First(c => !JobTypes.CategoryOn(s.Key, c));
        Check($"{s.Label}: the category is refused", JobTypes.CategoryRefusal(s.Key, cat).Length > 0 && JobTypes.CategoryRefusal("", cat) == "");
        continue;
    }
    if (s.Key is JobTypes.PhotosIn or JobTypes.PhotosOut)
    {
        bool rule = s.Key == JobTypes.PhotosIn
            ? JobTypes.PhotosInRequired("") && !JobTypes.PhotosInRequired(s.Key)
            : JobTypes.PhotosOutRequired("") && !JobTypes.PhotosOutRequired(s.Key);
        Check($"{s.Label}: no longer asked", rule);
        continue;
    }
    if (buttonOf.TryGetValue(s.Key, out var b))
    {
        var on = Keys(b.At);
        var t = At(b.At.Category, b.At.Stage, s.Key);
        Check($"{s.Label}: its button goes", on.Contains(b.Key) && !Keys(t).Contains(b.Key));
        continue;
    }
    Check($"{s.Label}: a stage goes from a route", StageGoes(s.Key));
}

// =========================================================== 4. what each switch does
Head("4. each switch does what the brief says");
IReadOnlySet<string> Reached(TicketCategory c, string off) => JobTypes.StagesReached(c, off);
var all = Reached(W, "");
Check("everything on: Workshop reaches inspection, quotes, payment, approval, delivery and waiting",
    new[] { Workflow.Inspected, "quote-requested", "invoice-client", "awaiting-payment", "dni-approval", Workflow.OutForDelivery, "waiting-customer" }.All(all.Contains));
Check("...and never the road for companies that do not take payment first", !all.Contains(Workflow.OrderParts) && !all.Contains(Workflow.PartsOnOrder));

var wi = At(W, Workflow.First, JobTypes.Inspection);
Check("Inspection findings off: Booked in offers Repairable and Not repairable itself",
    Keys(wi).IsSupersetOf(new[] { "repairable", "not-repairable" }) && !Keys(wi).Contains("inspection") && !Reached(W, JobTypes.Inspection).Contains(Workflow.Inspected));
Check("...and a job opened before it was switched off still has Inspection findings", Keys(At(W, Workflow.First)).Contains("inspection"));

var q = Reached(G, JobTypes.QuoteSuppliers);
Check("Ask suppliers off: Quote needed goes straight to Quote sent", !q.Contains("quote-requested") && q.Contains("quote-sent")
    && Keys(At(G, "quote-needed", JobTypes.QuoteSuppliers)).Contains("quote-sent"));

var pf = Reached(W, JobTypes.QuotePayFirst);
Check("Payment before ordering off: no invoice or wait for payment before ordering",
    !pf.Contains("invoice-client") && !pf.Contains("quote-awaiting-payment") && !pf.Contains("order-placed"));
Check("...the parts are ordered, received and fitted, and THEN the job is invoiced",
    pf.Contains(Workflow.OrderParts) && pf.Contains(Workflow.PartsOnOrder) && pf.Contains("parts-received") && !pf.Contains("parts-fitted")
    && Where("parts-fitted", At(W, "parts-received", JobTypes.QuotePayFirst)) == "to-be-invoiced");

var tp = Reached(G, JobTypes.TrackPayment);
Check("Track payment off: Client invoiced finishes a General job", !tp.Contains("awaiting-payment")
    && Where("client-invoiced", At(G, "to-be-invoiced", JobTypes.TrackPayment)) == Workflow.Closed);
Check("...and leaves a workshop machine still here Awaiting collection",
    Where("client-invoiced", At(W, "to-be-invoiced", JobTypes.TrackPayment)) == Workflow.AwaitingCollection);

Check("Supervisor approval off: Do not invoice goes straight to the office to close, in every category",
    new[] { (W, Workflow.RepairableStage, "do-not-invoice"), (K, Workflow.WalkInRepaired, "walkin-no-invoice"), (R, Workflow.RemoteFirst, "remote-dni") }
        .All(x => Where(x.Item3, At(x.Item1, x.Item2, JobTypes.DniApproval)) == "dni-to-close")
    && JobTypes.Categories.All(c => !Reached(c, JobTypes.DniApproval).Contains("dni-approval")));

Check("Out for delivery off: only Machine collected", !Keys(At(W, "to-be-invoiced", JobTypes.Delivery)).Contains(TicketService.DeliverKey)
    && Keys(At(W, "to-be-invoiced", JobTypes.Delivery)).Contains(TicketService.CollectedKey) && Keys(At(W, "to-be-invoiced")).Contains(TicketService.DeliverKey));

Check("Quote needed off: no quotes in Workshop or General", !Reached(W, JobTypes.Quotes).Contains("quote-needed") && !Reached(G, JobTypes.Quotes).Contains("quote-needed"));
Check("Waiting on customer off: never waiting", !Reached(W, JobTypes.WaitCustomer).Contains("waiting-customer"));
Check("Invoice inspection fee off: not offered at Not repairable or after a rejected quote",
    !Keys(At(W, Workflow.NotRepairableStage, JobTypes.InspectionFee)).Contains("inspection-fee")
    && !Keys(At(W, "quote-rejected", JobTypes.InspectionFee)).Contains("declined-inspection-fee")
    && Keys(At(W, "quote-rejected")).Contains("declined-inspection-fee"));
Check("Paid on site off (and no workshop): a call out never reaches Paid on site",
    !Reached(C, Off(JobTypes.PaidOnSite, JobTypes.CallOutToWorkshop)).Contains(Workflow.PaidOnSite) && Reached(C, "").Contains(Workflow.PaidOnSite));
Check("Paid at counter off: a walk in never reaches it", !Reached(K, JobTypes.PaidCounter).Contains(Workflow.PaidCounter));
Check("SLA off: Remote support has no SLA button", !Keys(At(R, Workflow.RemoteFirst, JobTypes.RemoteSla)).Contains("remote-sla"));
Check("Workshop switched off: a call out cannot become one, nor a walk in", !Keys(At(C, Workflow.CallOutWorkDone, JobTypes.Workshop)).Contains(Workflow.ToWorkshop)
    && !Keys(At(K, Workflow.WalkInFirst, JobTypes.Workshop)).Contains(Workflow.BookInFromWalkIn)
    && !Reached(C, JobTypes.Workshop).Contains(Workflow.First));
Check("Remote support and General can never be refused", JobTypes.CategoryRefusal(Off(JobTypes.Switches.Select(s => s.Key).ToArray()), R) == ""
    && JobTypes.CategoryRefusal(Off(JobTypes.Switches.Select(s => s.Key).ToArray()), G) == "");

string Where(string key, Ticket t) => TicketService.TargetOf(Workflow.Action(key)!, t);

// =========================================================== 5. the judge catches a broken route
Head("5. the judge catches a route that could not finish properly");
string Unfixed(ActionDef a, Ticket t) => a.Key == "parts-fitted" ? "parts-fitted" : TicketService.TargetOf(a, t);
var unbilled = JobTypes.ProblemFor(W, JobTypes.QuotePayFirst, Unfixed);
Check("payment before ordering off WITHOUT sending Parts fitted to be invoiced: refused — closed unbilled",
    unbilled.Contains("without ever being invoiced"), unbilled);
Check("...and the same for General", JobTypes.ProblemFor(G, JobTypes.QuotePayFirst, Unfixed).Contains("without ever being invoiced"));
string Circle(ActionDef a, Ticket t) => a.Key == "client-invoiced" ? "to-be-invoiced" : TicketService.TargetOf(a, t);
var stuck = JobTypes.ProblemFor(G, "", Circle);
Check("a step sent round in a circle: refused — a job that can never finish", stuck.Contains("would have no way to finish") && stuck.Contains("To be invoiced"), stuck);
string Nowhere(ActionDef a, Ticket t) => a.Key == "general-done" ? "no-such-stage" : TicketService.TargetOf(a, t);
Check("a step to a stage that does not exist: refused", JobTypes.ProblemFor(G, "", Nowhere).Contains("does not exist"));

// =========================================================== 6. Clean
Head("6. what is stored");
Check("unknown keys are dropped", JobTypes.Clean(new[] { "inspection", "not-a-switch", " delivery " }) == "inspection,delivery",
    JobTypes.Clean(new[] { "inspection", "not-a-switch", " delivery " }));
Check("a category's own steps are dropped while it is off — switched on again, it comes back whole",
    // Quotes belong to General as well, and Reschedule to Call out: those stay.
    JobTypes.Clean(new[] { JobTypes.Workshop, JobTypes.Inspection, JobTypes.Reschedule, JobTypes.Quotes })
        == $"{JobTypes.Workshop},{JobTypes.Quotes},{JobTypes.Reschedule}"
    && JobTypes.Clean(new[] { JobTypes.CallOut, JobTypes.Reschedule }) == JobTypes.CallOut,
    JobTypes.Clean(new[] { JobTypes.Workshop, JobTypes.Inspection, JobTypes.Reschedule, JobTypes.Quotes }));
Check("in the page's order, whatever order they came in", JobTypes.Clean(new[] { JobTypes.DniApproval, JobTypes.PhotosIn }) == $"{JobTypes.PhotosIn},{JobTypes.DniApproval}");
Check("every key is unique", JobTypes.Switches.Select(s => s.Key).Distinct().Count() == JobTypes.Switches.Length);
Check("the old, empty list is everything on", JobTypes.Switches.All(s => JobTypes.On("", s.Key)) && JobTypes.Problem("") == "");

// =========================================================== 7. the page's cards and lines (stage B)
Head("7. the page: every switch on one card, and the line a job takes");
var onCards = JobTypes.Cards.SelectMany(c => c.Lines.Where(l => !l.IsLocked).Select(l => (Card: c.Title, Key: l.Switch!))
    .Concat(c.CategorySwitch != null ? new[] { (Card: c.Title, Key: c.CategorySwitch) } : Array.Empty<(string Card, string Key)>())).ToList();
Check("every switch is on exactly one card", JobTypes.Switches.All(s => onCards.Count(x => x.Key == s.Key) == 1) && onCards.Count == JobTypes.Switches.Length,
    string.Join(", ", JobTypes.Switches.Where(s => onCards.Count(x => x.Key == s.Key) != 1).Select(s => s.Key)));
Check("...the card its group names", onCards.All(x => JobTypes.Switch(x.Key)!.Group == x.Card),
    string.Join(", ", onCards.Where(x => JobTypes.Switch(x.Key)!.Group != x.Card).Select(x => x.Key)));
Check("every locked line says why", JobTypes.Cards.SelectMany(c => c.Lines).Where(l => l.IsLocked).All(l => l.Locked.Length > 10));
Check("Remote support and General are the cards always on, and say why",
    JobTypes.Cards.Where(c => c.CategorySwitch == null && c.Category != null).Select(c => c.Category).OrderBy(c => c).SequenceEqual(new TicketCategory?[] { R, G }.OrderBy(c => c))
    && JobTypes.Cards.Where(c => c.CategorySwitch == null && c.Category != null).All(c => c.AlwaysOn.Length > 10));
string Line(TicketCategory c, string off = "", string? through = null) => string.Join(" → ", JobTypes.Path(c, off, through));
var wLine = Line(W);
Check("Workshop, everything on", wLine == "Booked in → Inspected → Repairable → To be invoiced → Invoiced — awaiting payment → Awaiting collection → Closed", wLine);
wLine = Line(W, Off(JobTypes.Inspection, JobTypes.TrackPayment));
Check("Workshop, no inspection and no tracking payment", wLine == "Booked in → Repairable → To be invoiced → Awaiting collection → Closed", wLine);
var cLine = Line(C);
Check("Call out", cLine == "Call out booked → Work done → To be invoiced → Invoiced — awaiting payment → Closed", cLine);
var qLine = Line(G, Off(JobTypes.QuoteSuppliers, JobTypes.QuotePayFirst), "quote-needed");
Check("a quote with no suppliers and no payment first",
    qLine == "Open → Quote needed → Quote sent — awaiting client → Quote accepted — order the parts → Parts ordered from supplier → Parts received — to be fitted → To be invoiced → Invoiced — awaiting payment → Closed", qLine);
Check("General and a recurring task", Line(G) == "Open → Closed" && Line(Rec) == "Due → Closed", $"{Line(G)} / {Line(Rec)}");

Console.WriteLine();
Console.WriteLine($"jobtypecheck: {pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;
