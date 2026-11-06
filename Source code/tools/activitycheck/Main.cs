// =====================================================================================
// activitycheck - proves Latest activity (2026-10-26, HANDOVER 164):
//
//   "if one clicks on latest activity, it will show any updates on all tickets as they are posted or when stages
//    change. The latest activity button should be for all or tickets, with the option to only check latest activity
//    on one's own tickets ... It should show newest to oldest, but should also be able to be sorted by client,
//    technician, and all tickets."  And: "i assign a ticket to Ryan, it shows up in his recent activity?"
//
// The rules in-process against the real TicketActivityService and TicketService — what is listed and what is left out,
// who sees what (the ticket list's own boundary), All and Mine, by client and by who posted, "assigned it to you",
// what is new and the count, paging; then a platform of its own: the portal's page, its tab and badge, the address,
// the live stream's count, the ticket page's anchors, and the console's request, count and "seen" over a real socket.
//
//   dotnet run -c Debug --project tools/activitycheck/activitycheck.csproj
//   dotnet run -c Debug --project tools/activitycheck/activitycheck.csproj -- --seed <folder>   (leaves its company for a browser)
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

var root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "CLAUDE.md"))) root = Path.GetDirectoryName(root);
if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
// --seed <folder>: the in-process half only, its company kept in that folder for a browser walk.
bool seed = args.Length == 2 && args[0] == "--seed";
var dataDir = seed ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "rs-activitycheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
const string Pw = "latest-activity-2026";
Console.WriteLine($"activitycheck — Latest activity; a platform on port {port}");

Process? server = null;
try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:PublicUrl"] = baseUrl, ["Platform:Region"] = "ZA" }).Build();
    var dp = DataProtectionProvider.Create("activitycheck");
    var tenants = new TenantService(factory, cfg, dataDir, dp);
    var (tenant, _) = await tenants.CreateAsync("Activity Co", "activity-co", 7);
    int tid = tenant!.Id;
    // Job types chosen (everything on), as for any company already set up: an owner's first dashboard visit would
    // otherwise be sent to the Job types page once (HANDOVER §167), and this harness is about the dashboard.
    await tenants.SetJobTypesAsync(tid, TenantRole.Owner, "Set up", Array.Empty<string>());
    var (other, _) = await tenants.CreateAsync("Other Co", "other-co", 7);
    const TenantRole O = TenantRole.Owner | TenantRole.Admin, S = TenantRole.Admin, T = TenantRole.Technician, B = TenantRole.Billing;
    await tenants.AddTechnicianAsync(tid, "olga@activity.test", Pw, "Olga Owner", O);
    await tenants.AddTechnicianAsync(tid, "sue@activity.test", Pw, "Sue Super", S);
    await tenants.AddTechnicianAsync(tid, "ryan@activity.test", Pw, "Ryan Tech", T);
    await tenants.AddTechnicianAsync(tid, "tim@activity.test", Pw, "Tim Tech", T);
    await tenants.AddTechnicianAsync(tid, "bea@activity.test", Pw, "Bea Office", B);
    await tenants.AddTechnicianAsync(tid, "otto@activity.test", Pw, "Otto Overseer", TenantRole.Owner);
    await tenants.AddTechnicianAsync(other!.Id, "x@other.test", Pw, "Xavier Other", O);
    var clients = new ClientService(factory, dp);
    var (acme, _) = await clients.CreateAsync(tid, "Acme Ltd", "Anna Acme", "", "anna@acme.test", "", "");
    var (zulu, _) = await clients.CreateAsync(tid, "Zulu Farms", "Zak Zulu", "", "", "", "");
    var tickets = new TicketService(factory);
    var activity = new TicketActivityService(factory, clients);
    async Task<int> IdOf(string name) { await using var db = new PlatformDb(options); return await db.Technicians.Where(t => t.DisplayName == name).Select(t => t.Id).FirstAsync(); }
    int olga = await IdOf("Olga Owner"), sue = await IdOf("Sue Super"), ryan = await IdOf("Ryan Tech"), tim = await IdOf("Tim Tech"),
        bea = await IdOf("Bea Office"), otto = await IdOf("Otto Overseer");
    Task<TicketActivityService.Page> List(int who, TenantRole role, string me, TicketActivityService.Filter? f = null) =>
        activity.ListAsync(tid, who, role, me, f ?? new());
    static string Keys(TicketActivityService.Page p) => string.Join(" ", p.Entries.Select(e => e.Key));

    // Raised by EVERY save that adds an entry or a ticket — the one place, so no writer can be missed.
    int saved = 0;
    PlatformDb.TicketActivitySaved += t => { if (t == tid) Interlocked.Increment(ref saved); };

    // =========================================================== the list
    Head("the list: every entry, newest first, with the new tickets");
    var a = await tickets.CreateAsync(tid, "Acme printer", "It jams.", "Sue Super", TicketSource.Manual, assignedTo: "Tim Tech", clientId: acme!.Id);
    var b = await tickets.CreateAsync(tid, "Zulu server", "It is slow.", "Sue Super", TicketSource.Manual, assignedTo: "Ryan Tech", clientId: zulu!.Id);
    var pool = await tickets.CreateAsync(tid, "Nobody's yet", "Somebody pick this up.", "Sue Super", TicketSource.Manual);
    var x = await tickets.CreateAsync(other.Id, "Another company's", "", "Xavier Other", TicketSource.Manual);
    Check("a new ticket is told (the save says so)", saved >= 3, $"{saved}");
    var na = await tickets.AddNoteAsync(tid, a.Id, "Tim Tech", "Replaced the roller.\nTesting it now.", false);
    await Task.Delay(5);
    var nb = await tickets.AddNoteAsync(tid, b.Id, "Ryan Tech", "Disk is nearly full.", false);
    await Task.Delay(5);
    var auto = await tickets.AddNoteAsync(tid, a.Id, "Tim Tech", "Tim Tech connected to ACME-PC.", true, automatic: true);
    await Task.Delay(5);
    var p1 = await List(olga, O, "Olga Owner");
    Check("Olga sees both comments, newest first", Keys(p1).StartsWith($"n{nb!.Id} n{na!.Id}"), Keys(p1));
    Check("...and the three new tickets, after them", Keys(p1).EndsWith($"t{pool.Id} t{b.Id} t{a.Id}"), Keys(p1));
    Check("...never another company's", !p1.Entries.Any(e => e.TicketId == x.Id));
    Check("an automatic line (a session's) is left out", !p1.Entries.Any(e => e.Key == "n" + auto!.Id));
    var withAuto = await List(olga, O, "Olga Owner", new(Automatic: true));
    var autoLine = withAuto.Entries.FirstOrDefault(e => e.Key == "n" + auto!.Id);
    Check("...and shown with Include automatic entries, said as automatic", autoLine?.Kind == ActivityKinds.Automatic, autoLine?.Kind ?? "missing");
    var ea = p1.Entries.First(e => e.Key == "n" + na.Id);
    Check("a line says the ticket, its client, who and what", ea.TicketNumber == a.Number && ea.Subject == "Acme printer" && ea.Client == "Acme Ltd"
        && ea.Who == "Tim Tech" && ea.Kind == ActivityKinds.Comment && ea.Headline == "Comment" && ea.TicketId == a.Id && ea.NoteId == na.Id, $"{ea.Client} / {ea.Who} / {ea.Headline}");
    Check("...and the words, on one line", ea.Text == "Replaced the roller. · Testing it now.", ea.Text);
    var et = p1.Entries.First(e => e.Key == "t" + b.Id);
    Check("a new ticket says so, and who is on it", et.Kind == ActivityKinds.Opened && et.Headline == "New ticket" && et.Who == "Sue Super" && et.Text.StartsWith("General ticket, on it now: Ryan Tech"), et.Text);
    Check("the people for 'Posted by' are the company's, not another's", p1.People.Contains("Tim Tech") && !p1.People.Contains("Xavier Other"));

    Head("who sees what: the ticket list's own boundary");
    var pt = await List(tim, T, "Tim Tech", new(Own: true));
    Check("a technician sees their own ticket's entries and the unassigned pool", pt.Entries.Any(e => e.TicketId == a.Id) && pt.Entries.Any(e => e.TicketId == pool.Id));
    Check("...and NOTHING of a colleague's ticket", !pt.Entries.Any(e => e.TicketId == b.Id), Keys(pt));
    var pb = await List(bea, B, "Bea Office");
    Check("the office administrator sees every ticket", pb.Entries.Any(e => e.TicketId == a.Id) && pb.Entries.Any(e => e.TicketId == b.Id));
    var po = await List(otto, TenantRole.Owner, "Otto Overseer");
    Check("an owner-only login (it oversees) sees every ticket", po.Entries.Any(e => e.TicketId == b.Id));

    Head("my own posts, and who posted it");
    var mine1 = await tickets.AddNoteAsync(tid, b.Id, "Olga Owner", "Any news on this?", false);
    p1 = await List(olga, O, "Olga Owner");
    Check("what I posted myself is left out — I know what I wrote", !p1.Entries.Any(e => e.Key == "n" + mine1!.Id));
    Check("...shown with Include my own", (await List(olga, O, "Olga Owner", new(Own: true))).Entries.Any(e => e.Key == "n" + mine1!.Id));
    var byMe = await List(olga, O, "Olga Owner", new(Poster: "Olga Owner"));
    Check("Posted by: me is asking for my own", byMe.Entries.Count > 0 && byMe.Entries.All(e => e.Who == "Olga Owner"), Keys(byMe));
    var byRyan = await List(olga, O, "Olga Owner", new(Poster: "Ryan Tech"));
    Check("Posted by: Ryan is only Ryan's", byRyan.Entries.Count == 1 && byRyan.Entries[0].Key == "n" + nb.Id, Keys(byRyan));
    var bySue = await List(olga, O, "Olga Owner", new(Poster: "Sue Super"));
    Check("...and the tickets Sue opened are hers", bySue.Entries.Count == 3 && bySue.Entries.All(e => e.Kind == ActivityKinds.Opened), Keys(bySue));

    Head("by client");
    var byAcme = await List(olga, O, "Olga Owner", new(ClientId: acme.Id));
    Check("one client (the portal's picker, by id)", byAcme.Entries.Count > 0 && byAcme.Entries.All(e => e.TicketId == a.Id), Keys(byAcme));
    var byWords = await List(olga, O, "Olga Owner", new(Client: "acme"));
    Check("by part of a name (the console), the Clients page's matching", Keys(byWords) == Keys(byAcme), Keys(byWords));
    var byContact = await List(olga, O, "Olga Owner", new(Client: "Zak"));
    Check("...a contact's name finds their client too", byContact.Entries.Count > 0 && byContact.Entries.All(e => e.TicketId == b.Id), Keys(byContact));
    var none = await List(olga, O, "Olga Owner", new(Client: "no such client"));
    Check("a name that matches no client says so, never 'every client'", none.NoSuchClient && none.Entries.Count == 0);
    var foreign = await List(olga, O, "Olga Owner", new(ClientId: -1));
    Check("a client that is not this company's shows nothing", foreign.Entries.Count == 0);

    Head("what kind of entry: a step, a customer's reply, a question, Back");
    var c = await tickets.CreateAsync(tid, "Acme call out", "Router.", "Olga Owner", TicketSource.Manual, assignedTo: "Ryan Tech", category: TicketCategory.CallOut, clientId: acme.Id);
    var why = await tickets.ActAsync(tid, c.Id, "Ryan Tech", T, "callout-work-done", new StageInput { Details = "Swapped the router.", Minutes = 45 });
    var steps = await List(olga, O, "Olga Owner");
    var step = steps.Entries.FirstOrDefault(e => e.TicketId == c.Id && e.Kind == ActivityKinds.Step);
    Check("a stage change says from where to where", why == "" && step?.Headline == "Call out booked → Work done", $"{why} {step?.Headline}");
    Check("...with its words", step?.Text.Contains("Swapped the router.") == true, step?.Text ?? "");
    why = await tickets.ActAsync(tid, c.Id, "Ryan Tech", T, TicketService.GoBackKey, new StageInput { Reason = "Pressed by mistake." });
    steps = await List(olga, O, "Olga Owner");
    var back = steps.Entries.FirstOrDefault(e => e.TicketId == c.Id && e.Kind == ActivityKinds.Back);
    Check("going back is said", why == "" && back != null && back.Headline.StartsWith("Went back"), $"{why} {back?.Headline}");
    Check("...and the step gone back over is marked", steps.Entries.Any(e => e.Key == step!.Key && e.Undone));
    var reply = await tickets.AddCustomerReplyAsync(tid, a.Id, "Anna Acme", "Thanks, it works now.", "Email");
    var r1 = (await List(olga, O, "Olga Owner")).Entries.FirstOrDefault(e => e.Key == "n" + reply!.Id);
    Check("a customer's emailed reply says so", r1?.Kind == ActivityKinds.Customer && r1.Headline == "Customer replied" && r1.Who == "Anna Acme", r1?.Headline ?? "missing");

    // =========================================================== Clint's follow-up: assigned to Ryan
    Head("\"i assign a ticket to Ryan, it shows up in his recent activity?\"");
    var d = await tickets.CreateAsync(tid, "Zulu laptop", "Battery.", "Sue Super", TicketSource.Manual, assignedTo: "Tim Tech", clientId: zulu.Id);
    Check("before: Ryan cannot see Tim's ticket", !(await List(ryan, T, "Ryan Tech")).Entries.Any(e => e.TicketId == d.Id));
    await tickets.UpdateAsync(tid, d.Id, "Olga Owner", O, null, null, "Ryan Tech", null);
    var pr = await List(ryan, T, "Ryan Tech");
    var handed = pr.Entries.FirstOrDefault(e => e.TicketId == d.Id && e.Kind == ActivityKinds.Change);
    Check("Olga gives it to Ryan: it is in his Latest activity", handed != null, Keys(pr));
    Check("...said to him as 'assigned it to you', by Olga", handed?.ForYou == "assigned it to you" && handed.Who == "Olga Owner", $"{handed?.ForYou} / {handed?.Who} / {handed?.Text}");
    Check("...and it is in his My tickets too", (await List(ryan, T, "Ryan Tech", new(Mine: true))).Entries.Any(e => e.TicketId == d.Id));
    var dOpened = pr.Entries.FirstOrDefault(e => e.Key == "t" + d.Id);
    Check("...and the ticket's own New ticket line does NOT say it — it was opened for Tim (found walking the page)", dOpened != null && dOpened.ForYou == "", dOpened?.ForYou ?? "missing");
    var tl = (await List(sue, S, "Sue Super")).Entries.FirstOrDefault(e => e.Key == handed!.Key);
    Check("...said to anybody else without 'to you'", tl != null && tl.ForYou == "", tl?.ForYou ?? "missing");
    await tickets.UpdateAsync(tid, d.Id, "Ryan Tech", T, null, null, "Ryan Tech, Tim Tech", null);
    var added = (await List(tim, T, "Tim Tech")).Entries.FirstOrDefault(e => e.TicketId == d.Id && e.Kind == ActivityKinds.Change && e.Who == "Ryan Tech");
    Check("added beside somebody: 'assigned it to you' for the one added", added?.ForYou == "assigned it to you", $"{added?.Text} / {added?.ForYou}");
    var fresh = await tickets.CreateAsync(tid, "Booked straight to Ryan", "", "Sue Super", TicketSource.Manual, assignedTo: "Ryan Tech");
    var opened = (await List(ryan, T, "Ryan Tech")).Entries.FirstOrDefault(e => e.Key == "t" + fresh.Id);
    Check("a new ticket booked straight to him says so too", opened?.ForYou == "assigned it to you", opened?.ForYou ?? "missing");
    Check("the words of a hand-off are read as the ticket writes them",
        TicketActivityService.AssignsTo("reassigned from Tim Tech to Ryan Tech", "Ryan Tech")
        && !TicketActivityService.AssignsTo("reassigned from Ryan Tech to Tim Tech", "Ryan Tech")
        && !TicketActivityService.AssignsTo("took Ryan Tech off the ticket", "Ryan Tech")
        && TicketActivityService.AssignsTo("assigned to Sue Super, Ryan Tech and Tim Tech (was the billing queue)", "Ryan Tech")
        && TicketActivityService.AssignsTo("priority → High, added Ryan Tech and Bea Office to the ticket", "Bea Office")
        && !TicketActivityService.AssignsTo("unassigned (was Ryan Tech)", "Ryan Tech")
        && !TicketActivityService.AssignsTo("reassigned from Sue to Ryan Tech Junior", "Ryan Tech"));
    var asked = await tickets.AddNoteAsync(tid, b.Id, "Sue Super", "@Ryan Tech can you check the backups?", false);
    var q = (await List(ryan, T, "Ryan Tech")).Entries.FirstOrDefault(e => e.Key == "n" + asked!.Id);
    Check("an @mention is 'asked you'", q?.ForYou == "asked you", q?.ForYou ?? "missing");

    // =========================================================== All and Mine
    Head("My tickets: on it, worked on it, opened it, asked on it");
    var mineR = await List(ryan, T, "Ryan Tech", new(Mine: true));
    Check("Ryan's Mine has his tickets", mineR.Entries.Any(e => e.TicketId == b.Id) && mineR.Entries.Any(e => e.TicketId == d.Id));
    Check("...and not the unassigned pool (that is All's)", !mineR.Entries.Any(e => e.TicketId == pool.Id) && (await List(ryan, T, "Ryan Tech")).Entries.Any(e => e.TicketId == pool.Id));
    var mineO = await List(olga, O, "Olga Owner", new(Mine: true));
    Check("Olga's Mine: the call out she opened — not Zulu server, where she only commented, nor the pool", mineO.Entries.Any(e => e.TicketId == c.Id) && !mineO.Entries.Any(e => e.TicketId == b.Id)
        && !mineO.Entries.Any(e => e.TicketId == pool.Id), Keys(mineO));
    var sueAsked = await tickets.AddNoteAsync(tid, pool.Id, "Tim Tech", "@Sue Super is this ours?", false);
    Check("an @mention puts the ticket in the asked person's Mine", (await List(sue, S, "Sue Super", new(Mine: true))).Entries.Any(e => e.TicketId == pool.Id));

    // =========================================================== new, and the count
    Head("what is new, and the count");
    var (n0, m0) = await activity.NewCountAsync(tid, ryan);
    Check("never looked: the last day counts as new", n0 > 0 && !m0, $"{n0}");
    var look = await List(ryan, T, "Ryan Tech", new(MarkSeen: true));
    Check("...every line of it marked new", look.Entries.All(e => e.IsNew));
    var (n1, _) = await activity.NewCountAsync(tid, ryan);
    Check("looking makes the count nought", n1 == 0, $"{n1}");
    var again = await List(ryan, T, "Ryan Tech");
    Check("...and nothing is new on the next look", again.Entries.All(e => !e.IsNew));
    await Task.Delay(20);
    var s1 = await tickets.AddNoteAsync(tid, b.Id, "Sue Super", "Backups are fine.", false);
    var (n2, _) = await activity.NewCountAsync(tid, ryan);
    Check("a colleague posts on his ticket: one new", n2 == 1, $"{n2}");
    Check("...and it is marked new in the list", (await List(ryan, T, "Ryan Tech")).Entries.First(e => e.Key == "n" + s1!.Id).IsNew);
    await tickets.AddNoteAsync(tid, b.Id, "Ryan Tech", "Thanks.", false);
    await tickets.AddNoteAsync(tid, b.Id, "Ryan Tech", "Ryan Tech connected to ZULU-SRV.", true, automatic: true);
    var (n3, _) = await activity.NewCountAsync(tid, ryan);
    Check("his own post and an automatic line do not count", n3 == 1, $"{n3}");
    await tickets.AddNoteAsync(tid, x.Id, "Xavier Other", "Another company's words.", false);
    Check("another company's posts do not count", (await activity.NewCountAsync(tid, ryan)).Count == 1);
    var hid = await tickets.CreateAsync(tid, "Tim's own job", "", "Tim Tech", TicketSource.Manual, assignedTo: "Tim Tech");
    await tickets.AddNoteAsync(tid, hid.Id, "Tim Tech", "Started.", false);
    Check("a ticket he may not see does not count (the same boundary)", (await activity.NewCountAsync(tid, ryan)).Count == 1);
    await List(ryan, T, "Ryan Tech", new(Mine: true, MarkSeen: true));
    var (n4, m4) = await activity.NewCountAsync(tid, ryan);
    Check("choosing My tickets is remembered, and the count follows it", m4 && n4 == 0, $"{m4} {n4}");
    await tickets.AddNoteAsync(tid, pool.Id, "Sue Super", "Yes it is ours.", false);
    Check("...so a post in the pool (All, not Mine) does not count for him now", (await activity.NewCountAsync(tid, ryan)).Count == 0);
    await tickets.AddNoteAsync(tid, d.Id, "Tim Tech", "Ordered a battery.", false);
    Check("...and one on his own ticket does", (await activity.NewCountAsync(tid, ryan)).Count == 1);
    var older = await List(ryan, T, "Ryan Tech", new(Mine: false, BeforeUtc: DateTime.UtcNow, MarkSeen: true));
    Check("'Show older' is not a look: the count and the choice stay", (await activity.NewCountAsync(tid, ryan)) == (1, true));

    Head("telling: once per burst, and the person who looked");
    await Task.Delay(TicketActivityService.Settle + TimeSpan.FromMilliseconds(400));   // what came before has been told
    int changed = 0, seenOf = 0;
    activity.Changed += t => { if (t == tid) Interlocked.Increment(ref changed); };
    activity.SeenChanged += (t, p) => { if (t == tid && p == "Bea Office") Interlocked.Increment(ref seenOf); };
    for (int i = 0; i < 4; i++) await tickets.AddNoteAsync(tid, a.Id, "Tim Tech", $"Burst {i}.", false);
    await Task.Delay(TicketActivityService.Settle + TimeSpan.FromMilliseconds(600));
    Check("four posts at once are told once", changed == 1, $"{changed}");
    await List(bea, B, "Bea Office", new(MarkSeen: true));
    Check("a look tells that person's own count to move", seenOf == 1, $"{seenOf}");

    Head("pages");
    var many = await tickets.CreateAsync(tid, "Busy ticket", "", "Sue Super", TicketSource.Manual, assignedTo: "Tim Tech");
    for (int i = 0; i < 65; i++) { await tickets.AddNoteAsync(tid, many.Id, "Tim Tech", $"Update {i}.", false); }
    var first = await List(sue, S, "Sue Super", new(Poster: "Tim Tech"));
    Check($"one page is {TicketActivityService.PageSize}, with more", first.Entries.Count == TicketActivityService.PageSize && first.More, $"{first.Entries.Count} {first.More}");
    var all = new List<ActivityEntry>(first.Entries);
    var cursor = first;
    while (cursor.More)
    {
        cursor = await List(sue, S, "Sue Super", new(Poster: "Tim Tech", BeforeUtc: cursor.Entries[^1].AtUtc));
        all.AddRange(cursor.Entries);
    }
    int timPosts; await using (var db = new PlatformDb(options)) timPosts = await db.TicketNotes.CountAsync(n => n.TenantId == tid && n.Author == "Tim Tech" && !n.Automatic);
    int timOpened; await using (var db = new PlatformDb(options)) timOpened = await db.Tickets.CountAsync(t => t.TenantId == tid && t.OpenedBy == "Tim Tech");
    Check("Show older to the end: every entry once, none twice", all.Count == timPosts + timOpened && all.Select(e => e.Key).Distinct().Count() == all.Count, $"{all.Count} of {timPosts + timOpened}");
    Check("...newest first all the way down", all.Zip(all.Skip(1)).All(z => z.First.AtUtc >= z.Second.AtUtc));
    Check("a long entry is cut at a word", TicketActivityService.Shorten(string.Join(" ", Enumerable.Repeat("word", 100))).EndsWith("word…"));

    // --seed: leave the made-up company behind for a platform of one's own, to look at in a browser (passwords: Pw above).
    if (seed) { Console.WriteLine($"\nSeeded {dataDir} — sign in as olga@activity.test, ryan@activity.test, tim@activity.test."); return fail == 0 ? 0 : 1; }

    // =========================================================== the platform
    Head("the platform");
    var pinfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var arg in new[] { "run", "-c", "Debug", "--project", proj, "--no-build", "--", "run" }) pinfo.ArgumentList.Add(arg);
    pinfo.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(pinfo.Environment, dataDir);
    pinfo.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
    pinfo.Environment["RSP_Platform__PublicUrl"] = baseUrl;
    pinfo.Environment["RSP_Urls"] = baseUrl;
    pinfo.Environment["ASPNETCORE_URLS"] = baseUrl;
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    var built = await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + built); return 1; }
    server = Process.Start(pinfo)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }

    Head("the portal");
    var ryanWeb = await Portal(port, "ryan@activity.test");
    var timWeb = await Portal(port, "tim@activity.test");
    var olgaWeb = await Portal(port, "olga@activity.test");
    var beaWeb = await Portal(port, "bea@activity.test");
    var tickPage = WebUtility.HtmlDecode(await ryanWeb.GetStringAsync("/portal/tickets"));
    var badge = Regex.Match(tickPage, "id=\"activityBadge\"[^>]*style=\"[^\"]*\"[^>]*>([^<]*)<");
    Check("every page's menu has Latest activity, with what is new", tickPage.Contains("href=\"/portal/activity\">Latest activity</a>") && badge.Success && badge.Groups[1].Value == "1 new",
        badge.Success ? badge.Groups[0].Value : "no badge");
    Check("...for the office administrator too", (await beaWeb.GetStringAsync("/portal/tickets")).Contains("href=\"/portal/activity\">Latest activity</a>"));
    var page = WebUtility.HtmlDecode(await ryanWeb.GetStringAsync("/portal/activity"));
    Check("the page opens on his remembered choice, My tickets", page.Contains("<h1>Latest activity</h1>") && Regex.IsMatch(page, "aria-selected=\"true\" class=\"on\" href=\"/portal/activity\\?mine=1"));
    Check("...a line opens the ticket AT that entry", page.Contains($"href=\"/portal/t/{d.Number}#note-"), Snip(page, "act-item"));
    Check("...and 'assigned it to you' is there", page.Contains("assigned it to you"));
    var after = WebUtility.HtmlDecode(await ryanWeb.GetStringAsync("/portal/tickets"));
    Check("opening it is looking: the badge is gone on the next page", Regex.IsMatch(after, "id=\"activityBadge\"[^>]*style=\"[^\"]*display:none"), Snip(after, "activityBadge"));
    page = WebUtility.HtmlDecode(await ryanWeb.GetStringAsync("/portal/activity?mine=0"));
    Check("All tickets, from the address", Regex.IsMatch(page, "aria-selected=\"true\" class=\"on\" href=\"/portal/activity\\?mine=0") && page.Contains("Nobody&#x27;s yet") | page.Contains("Nobody's yet"));
    var timPage = WebUtility.HtmlDecode(await timWeb.GetStringAsync("/portal/activity?mine=0"));
    Check("Tim's page has no line of Ryan's ticket", !timPage.Contains("Zulu server") && timPage.Contains("Acme printer"));
    int acmeNumber; await using (var db = new PlatformDb(options)) acmeNumber = await db.Clients.Where(cl => cl.Id == acme.Id).Select(cl => cl.Number).FirstAsync();
    var acmePage = WebUtility.HtmlDecode(await olgaWeb.GetStringAsync($"/portal/activity?mine=0&c={acmeNumber}"));
    Check("?c= is one client, by its number", acmePage.Contains("Acme printer") && !acmePage.Contains("Zulu server") && acmePage.Contains("value=\"Acme Ltd\""));
    var noRedirect = new HttpClient(new HttpClientHandler { CookieContainer = olgaWeb.Jar(), AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl) };
    var picked = await noRedirect.GetAsync($"/portal/activity?mine=0&client={acme.Id}");
    var to = picked.Headers.Location?.ToString() ?? "";
    Check("the picker's id is turned into the number at once — no id stays in the address", (int)picked.StatusCode == 302 && to.Contains($"c={acmeNumber}") && !to.Contains("client="), $"{(int)picked.StatusCode} {to}");
    var partial = await olgaWeb.GetStringAsync("/portal/activity?mine=0&handler=List&seen=false");
    Check("the list alone, for the live refresh", partial.Contains("act-item") && !partial.Contains("<html"));
    var ticketPage = await olgaWeb.GetStringAsync($"/portal/t/{a.Number}");
    Check("the ticket's story has the entries' anchors", ticketPage.Contains($"id=\"note-{na.Id}\""));
    var dash = WebUtility.HtmlDecode(await olgaWeb.GetStringAsync("/portal"));
    Check("the dashboard has Latest activity", dash.Contains("href=\"/portal/activity\"") && dash.Contains("data-activity-count"));

    Head("live: the portal's stream, and the console");
    var ryanCon = await Sign(port, "ryan@activity.test", Pw);
    var sueCon = await Sign(port, "sue@activity.test", Pw);
    var first0 = await ryanCon.Next<ActivityCountMsg>(_ => true, 10000);
    Check("a console is told its count at sign-in, and its choice", first0 != null && first0.Mine == false, first0 == null ? "nothing" : $"{first0.Count} mine={first0.Mine}");
    var events = new List<string>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    var streamTask = Task.Run(async () =>
    {
        try
        {
            using var s = await ryanWeb.GetStreamAsync("/api/portal/chat/stream", cts.Token);
            using var rd = new StreamReader(s);
            string? ev = null;
            while (!cts.IsCancellationRequested && await rd.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.StartsWith("event: ")) ev = line[7..];
                else if (line.StartsWith("data: ") && ev == "activity") lock (events) events.Add(line[6..]);
            }
        }
        catch { }
    });
    await Task.Delay(1500);
    sueCon.Ws.Send(new TicketNoteAddMsg { TicketId = b.Id, Body = "Ryan, the customer called again." });
    var live = await ryanCon.Next<ActivityCountMsg>(m => m.Count > 0, 15000);
    Check("Sue posts on Ryan's ticket: his console's count moves by itself", live?.Count == 1, live == null ? "nothing" : $"{live.Count}");
    for (int i = 0; i < 50 && events.Count == 0; i++) await Task.Delay(200);
    string ev1; lock (events) ev1 = events.FirstOrDefault() ?? "";
    Check("...and his open portal page hears it too", ev1.Contains("\"count\":1") && ev1.Contains("\"label\":\"1\""), ev1);
    ryanCon.Ws.Send(new ActivityRequestMsg { Mine = false, MarkSeen = true });
    var got = await ryanCon.Next<ActivityListMsg>(_ => true, 15000);
    Check("the console asks for the list and gets it, newest first", got != null && got.Entries.FirstOrDefault()?.Text == "Ryan, the customer called again." && got.Entries[0].IsNew && got.Entries[0].Who == "Sue Super",
        got == null ? "nothing" : got.Entries.FirstOrDefault()?.Text ?? "empty");
    var zero = await ryanCon.Next<ActivityCountMsg>(m => m.Count == 0 && m != first0, 15000);
    Check("...and looking puts the count back to nought, on the console", zero != null);
    for (int i = 0; i < 30; i++) { lock (events) if (events.Count >= 2) break; await Task.Delay(200); }
    string ev2; lock (events) ev2 = events.Count >= 2 ? events[1] : "";
    Check("...and on his portal page", ev2.Contains("\"count\":0"), ev2);
    sueCon.Ws.Send(new TicketCreateMsg { Subject = "From the console", Category = (int)TicketCategory.General, ClientId = acme.Id, AssignTo = "Ryan Tech", AssignToMe = false });
    var made = await ryanCon.Next<ActivityCountMsg>(m => m.Count > 0, 15000);
    ryanCon.Ws.Send(new ActivityRequestMsg { Mine = false });
    var list2 = await ryanCon.Next<ActivityListMsg>(m => m.Entries.Any(e => e.Subject == "From the console"), 15000);
    var fromCon = list2?.Entries.Where(e => e.Subject == "From the console").ToList() ?? new();
    Check("a ticket opened in the console is ONE line, 'New ticket' — its own 'opened' line is automatic", made != null && fromCon.Count == 1 && fromCon[0].Kind == ActivityKinds.Opened && fromCon[0].ForYou == "assigned it to you",
        string.Join(" | ", fromCon.Select(e => e.Kind + ":" + e.Headline)));
    ryanCon.Ws.Send(new ActivityRequestMsg { Mine = false, Client = "zulu" });
    var zl = await ryanCon.Next<ActivityListMsg>(m => m.Client == "zulu", 15000);
    Check("the console's Client box narrows by name", zl != null && zl.Entries.Count > 0 && zl.Entries.All(e => e.Client == "Zulu Farms"), zl == null ? "nothing" : string.Join(", ", zl.Entries.Select(e => e.Client).Distinct()));
    cts.Cancel();
    await streamTask;
}
finally
{
    try { server?.Kill(entireProcessTree: true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
try { Directory.Delete(dataDir, true); } catch { }
return fail == 0 ? 0 : 1;

static async Task<HttpResponseMessage> PostForm(HttpClient c, string path, Dictionary<string, string> fields)
{
    var page = await (await c.GetAsync(Regex.Replace(path, @"[?&]handler=[^&]*", ""))).Content.ReadAsStringAsync();
    fields["__RequestVerificationToken"] = Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
    return await c.PostAsync(path, new FormUrlEncodedContent(fields));
}
static async Task<Web> Portal(int port, string email)
{
    var jar = new CookieContainer();
    var c = new Web(jar, new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true })
    { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    await PostForm(c, "/login", new() { ["Email"] = email, ["Password"] = "latest-activity-2026" });
    return c;
}
static string? Between(string s, string a, string b)
{
    var i = s.IndexOf(a, StringComparison.Ordinal);
    if (i < 0) return null;
    i += a.Length;
    var j = s.IndexOf(b, i, StringComparison.Ordinal);
    return j < 0 ? null : s[i..j];
}
static string Snip(string html, string near)
{
    var i = html.IndexOf(near, StringComparison.OrdinalIgnoreCase);
    if (i < 0) return "(not found)";
    return Regex.Replace(Regex.Replace(html.Substring(i, Math.Min(260, html.Length - i)), "<[^>]+>", " "), "\\s+", " ").Trim();
}
static async Task<TechConsole> Sign(int port, string email, string password)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password });
    lr.EnsureSuccessStatusCode();
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var cws = new ClientWebSocket();
    await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}"), CancellationToken.None);
    var c = new TechConsole(new WsConnection(cws));
    _ = c.Ws.RunAsync(CancellationToken.None);
    return c;
}
static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
static async Task<bool> WaitForPort(int port, int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); return true; }
        catch { await Task.Delay(400); }
    }
    return false;
}

/// <summary>A signed-in browser, with its cookies at hand for a second client that must not follow redirects.</summary>
file sealed class Web : HttpClient
{
    private readonly CookieContainer _jar;
    public Web(CookieContainer jar, HttpMessageHandler h) : base(h) => _jar = jar;
    public CookieContainer Jar() => _jar;
}

file sealed class TechConsole
{
    public WsConnection Ws { get; }
    private readonly List<Msg> _got = new();
    public TechConsole(WsConnection ws) { Ws = ws; ws.OnMessage += m => { lock (_got) _got.Add(m); }; }
    /// <summary>The first message of this kind that matches and has not been taken before.</summary>
    public async Task<T?> Next<T>(Func<T, bool> where, int ms) where T : Msg
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_got)
            {
                var m = _got.OfType<T>().FirstOrDefault(where);
                if (m != null) { _got.Remove(m); return m; }
            }
            await Task.Delay(100);
        }
        return null;
    }
}

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
