// =====================================================================================
// clientremovecheck - proves removing a client (2026-10-27, HANDOVER 172):
//
//   "We do not seem to have the ability to remove clients. One of my staff created a client with the incorrect
//    spelling and the proper client already exists."
//
// Removing is MOVING: everything on the wrong client goes to the right one, then the wrong one is deleted. In-process
// against the real ClientService, on SQLite or (RSP_TEST_DB=postgres) PostgreSQL.
//
//   dotnet run -c Debug --project tools/clientremovecheck/clientremovecheck.csproj
// =====================================================================================

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Tools;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

// --seed <folder>: leave the misspelt client and everything on it in <folder>, for a platform to open (a walk in the browser).
var seedOnly = args.Length == 2 && args[0] == "--seed";
var dataDir = seedOnly ? args[1] : Path.Combine(Path.GetTempPath(), "rs-clientremovecheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
Console.WriteLine("clientremovecheck — removing a client, its work moved to the right one");

var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
var factory = new Factory(options);
var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:Region"] = "ZA" }).Build();
var dp = DataProtectionProvider.Create("clientremovecheck");
var tenants = new TenantService(factory, cfg, dataDir, dp);
var clients = new ClientService(factory, dp);
var tickets = new TicketService(factory, new TicketPhotoService(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketPhotoService>.Instance, dataDir));
int tid = (await tenants.CreateAsync("Remove Co", "remove-co", 5)).tenant!.Id;
int other = (await tenants.CreateAsync("Other Co", "other-co", 5)).tenant!.Id;
await tenants.SetJobTypesAsync(tid, TenantRole.Owner, "Set up", Array.Empty<string>());
await tenants.AddTechnicianAsync(tid, "owner@removeco.test", "remove-client-2026", "Olga Owner", TenantRole.Owner | TenantRole.Admin);
const TenantRole T = TenantRole.Technician, S = TenantRole.Admin, B = TenantRole.Billing, O = TenantRole.Owner;

async Task<T> Add<T>(T row) where T : class { await using var db = new PlatformDb(options); db.Add(row); await db.SaveChangesAsync(); return row; }
async Task<Client> NewClient(int tenant, string name, Action<Client>? more = null)
{ var c = new Client { TenantId = tenant, Name = name }; more?.Invoke(c); return await Add(c); }
async Task<bool> Exists(int id) { await using var db = new PlatformDb(options); return await db.Clients.AnyAsync(c => c.Id == id); }
PlatformDb Db() => new(options);

// ============================================================ who may
Head("who may remove a client");
var empty = await NewClient(tid, "Nothing Here Ltd");
var (done, why) = await clients.RemoveAsync(tid, T, empty.Id, null, "Tess Tech");
Check("a technician is refused...", done == null && why.Contains("owner, a supervisor or an office administrator"), why);
Check("...and the client is still there", await Exists(empty.Id));
Check("the office administrator may (the same people as Import clients)", Permissions.CanRemoveClients(B) && Permissions.CanRemoveClients(S) && Permissions.CanRemoveClients(O) && !Permissions.CanRemoveClients(T));

// ============================================================ nothing on it
Head("a client with nothing on it");
Check("nothing on it says nothing", (await clients.WhatIsOnAsync(tid, empty.Id)).Say() == "");
(done, why) = await clients.RemoveAsync(tid, B, empty.Id, null, "Bea Office");
Check("is simply removed — no client to move to needed", done != null && done.Into == null && why == "" && !await Exists(empty.Id), why);
(done, why) = await clients.RemoveAsync(tid, B, empty.Id, null, "Bea Office");
Check("removing it again says it is not there", done == null && why.Contains("not on file"), why);

// ============================================================ the duplicate and everything on it
Head("the misspelt client and the right one");
var right = await NewClient(tid, "Acme Ltd", c => { c.Phone = "021 999 0000"; c.HealthCheck = false; });
var wrong = await NewClient(tid, "Acme Ltdd", c => { c.Phone = "011 555 1234"; c.Address = "1 Main Road"; c.Notes = "Gate code 4411"; c.HealthCheck = true; });
var foreign = await NewClient(other, "Somebody Else's Client");

var sueRight = await Add(new Contact { TenantId = tid, ClientId = right.Id, Name = "Sue Smith" });
var sueWrong = await Add(new Contact { TenantId = tid, ClientId = wrong.Id, Name = "sue smith ", Email = "sue@acme.test", Phone = "082 111 2222" });
var bob = await Add(new Contact { TenantId = tid, ClientId = wrong.Id, Name = "Bob New", Email = "bob@acme.test" });
var gone = await Add(new Contact { TenantId = tid, ClientId = wrong.Id, Name = "Old Leaver", Archived = true });
var byEmail = await Add(new Contact { TenantId = tid, ClientId = right.Id, Name = "Jo Bloggs", Email = "jo@acme.test" });
var joWrong = await Add(new Contact { TenantId = tid, ClientId = wrong.Id, Name = "Joanne Bloggs", Email = "JO@acme.test" });

var d1 = await Add(new Device { TenantId = tid, DeviceId = "dev-sue", MachineName = "SUE-PC", ClientId = wrong.Id, ContactId = sueWrong.Id });
var d2 = await Add(new Device { TenantId = tid, DeviceId = "dev-front", MachineName = "FRONT-DESK", ClientId = wrong.Id });
var d3 = await Add(new Device { TenantId = tid, DeviceId = "dev-right", MachineName = "RIGHT-PC", ClientId = right.Id });

var t1 = await tickets.CreateAsync(tid, "Sue's printer", "", "Tess Tech", TicketSource.Manual, clientId: wrong.Id);
var t2 = await tickets.CreateAsync(tid, "Front desk slow", "", "Tess Tech", TicketSource.Manual, clientId: wrong.Id);
var t3 = await tickets.CreateAsync(tid, "Already right", "", "Tess Tech", TicketSource.Manual, clientId: right.Id);
var old = DateTime.UtcNow.AddDays(-9);
await using (var db = Db())
{
    var t = await db.Tickets.FirstAsync(x => x.Id == t1.Id); t.ContactId = sueWrong.Id; t.UpdatedUtc = old;
    await db.SaveChangesAsync();
}

var netWrong = await Add(new KbFolder { TenantId = tid, ClientId = wrong.Id, Name = "Network" });
var switches = await Add(new KbFolder { TenantId = tid, ClientId = wrong.Id, ParentId = netWrong.Id, Name = "Switches" });
var printers = await Add(new KbFolder { TenantId = tid, ClientId = wrong.Id, Name = "Printers" });
var netRight = await Add(new KbFolder { TenantId = tid, ClientId = right.Id, Name = "network" });
var art = await Add(new KbArticle { TenantId = tid, ClientId = wrong.Id, FolderId = switches.Id, Title = "Switch password location" });
var deleted = await Add(new KbArticle { TenantId = tid, ClientId = wrong.Id, Title = "An old one", DeletedUtc = DateTime.UtcNow });
var rev = await Add(new KbRevision { TenantId = tid, ArticleId = art.Id, Version = 1, Title = art.Title, ClientId = wrong.Id });
var task = await Add(new RecurringTask { TenantId = tid, ClientId = wrong.Id, Title = "Monthly backup check", Repeats = Repeats.Monthly, Days = 1 });
var chat = await Add(new WebChat { TenantId = tid, ClientId = wrong.Id, ContactId = sueWrong.Id, Name = "Sue" });
var run = await Add(new HealthCheckRun { TenantId = tid, ClientId = wrong.Id, DeviceId = "dev-sue", MachineName = "SUE-PC" });
var foreignTicket = await tickets.CreateAsync(other, "Not theirs", "", "Someone", TicketSource.Manual, clientId: foreign.Id);

if (seedOnly) { Console.WriteLine($"Seeded {dataDir}: owner@removeco.test, Acme Ltdd #{wrong.Number} into Acme Ltd #{right.Number}."); return 0; }
var on = await clients.WhatIsOnAsync(tid, wrong.Id);
Check("what is on it, counted", on.Tickets == 2 && on.Machines == 2 && on.People == 4 && on.Articles == 1 && on.Folders == 3
    && on.Schedules == 1 && on.Chats == 1 && on.HealthChecks == 1, on.Say());
Check("...and said in words", on.Say().StartsWith("2 tickets, 2 machines, 4 people, 1 knowledge base article") && on.Say().EndsWith(" and 1 health check report"), on.Say());

Head("refused before anything moves");
(done, why) = await clients.RemoveAsync(tid, B, wrong.Id, null, "Bea Office");
Check("with things on it and no client to move them to: refused, saying what is on it", done == null && why.Contains("Acme Ltdd has 2 tickets") && why.Contains("Choose the client"), why);
(done, why) = await clients.RemoveAsync(tid, B, wrong.Id, wrong.Id, "Bea Office");
Check("moving it to itself is refused", done == null && why.Contains("not Acme Ltdd itself"), why);
(done, why) = await clients.RemoveAsync(tid, B, wrong.Id, foreign.Id, "Bea Office");
Check("moving it to ANOTHER company's client is refused", done == null && why.Contains("not on file"), why);
(done, why) = await clients.RemoveAsync(other, O, wrong.Id, foreign.Id, "Somebody");
Check("another company cannot remove it", done == null && why.Contains("not on file"), why);
await using (var db = Db())
    Check("...and after all four, nothing has moved", await Exists(wrong.Id)
        && await db.Tickets.CountAsync(t => t.ClientId == wrong.Id) == 2 && await db.Devices.CountAsync(d => d.ClientId == wrong.Id) == 2);

Head("moved and removed");
(done, why) = await clients.RemoveAsync(tid, B, wrong.Id, right.Id, "Bea Office");
Check("Acme Ltdd into Acme Ltd: done", done != null && why == "" && done.Into?.Id == right.Id && done.Name == "Acme Ltdd", why);
Check("Acme Ltdd is gone", !await Exists(wrong.Id));

await using (var db = Db())
{
    var r = await db.Clients.FirstAsync(c => c.Id == right.Id);
    Check("the right client keeps its own phone number", r.Phone == "021 999 0000", r.Phone);
    Check("...its blank address is filled from the wrong one", r.Address == "1 Main Road", r.Address);
    Check("...and the wrong one's notes are kept", r.Notes == "Gate code 4411", r.Notes);
    Check("...and its health check tick is its own (off), not the wrong one's", !r.HealthCheck);

    var people = await db.Contacts.Where(c => c.ClientId == right.Id).OrderBy(c => c.Name).ToListAsync();
    Check("people: Sue on both is ONE Sue, Jo on both (same email) is ONE Jo, Bob and the archived leaver move",
        people.Select(p => p.Name).SequenceEqual(new[] { "Bob New", "Jo Bloggs", "Old Leaver", "Sue Smith" }), string.Join(", ", people.Select(p => p.Name)));
    var sue = people.First(p => p.Name == "Sue Smith");
    Check("...the Sue kept is the right client's, her blanks filled from the other", sue.Id == sueRight.Id && sue.Email == "sue@acme.test" && sue.Phone == "082 111 2222", $"{sue.Id} {sue.Email} {sue.Phone}");
    Check("...the archived leaver stays archived", people.First(p => p.Name == "Old Leaver").Archived);
    Check("...and no person is left on a client that is not there", !await db.Contacts.AnyAsync(c => c.Id == sueWrong.Id || c.Id == joWrong.Id || c.ClientId == wrong.Id));

    var m1 = await db.Devices.FirstAsync(d => d.Id == d1.Id); var m2 = await db.Devices.FirstAsync(d => d.Id == d2.Id);
    Check("machines: Sue's PC is the right client's, and still Sue's — the Sue kept", m1.ClientId == right.Id && m1.ContactId == sueRight.Id, $"{m1.ClientId} {m1.ContactId}");
    Check("...the front desk PC moved too", m2.ClientId == right.Id && m2.ContactId == null);
    Check("...and both are handed to the hub with their new owner", done!.Machines.Count == 2
        && done.Machines.Any(m => m.DeviceId == "dev-sue" && m.Owner.ClientName == "Acme Ltd" && m.Owner.ContactId == sueRight.Id && m.Owner.ContactName == "Sue Smith")
        && done.Machines.Any(m => m.DeviceId == "dev-front" && m.Owner.ContactId == null));
    Check("...a machine already on the right client is not told again", done.Machines.All(m => m.DeviceId != "dev-right"));

    var k1 = await db.Tickets.FirstAsync(t => t.Id == t1.Id); var k2 = await db.Tickets.FirstAsync(t => t.Id == t2.Id);
    Check("tickets: both are the right client's now", k1.ClientId == right.Id && k2.ClientId == right.Id);
    Check("...Sue's ticket names the Sue kept", k1.ContactId == sueRight.Id, $"{k1.ContactId}");
    var line = await db.TicketNotes.Where(n => n.TicketId == t1.Id).OrderBy(n => n.Id).LastAsync();
    Check("...each story says so, by whom", line.Body == "Moved from Acme Ltdd to Acme Ltd — Acme Ltdd was removed." && line.Author == "Bea Office" && line.IsSystem, line.Body);
    Check("...as an automatic line (kept out of Latest activity)", line.Automatic);
    Check("...and the ticket is not marked as updated (the list's order stays)", Math.Abs((k1.UpdatedUtc - old).TotalSeconds) < 1, k1.UpdatedUtc.ToString("o"));
    Check("a ticket already on the right client gets no line", !await db.TicketNotes.AnyAsync(n => n.TicketId == t3.Id && n.Body.Contains("Moved from")));

    Check("knowledge base: the article moved, and the deleted one too (a restore lands right)",
        (await db.KbArticles.FirstAsync(a => a.Id == art.Id)).ClientId == right.Id && (await db.KbArticles.FirstAsync(a => a.Id == deleted.Id)).ClientId == right.Id);
    Check("...its saved versions moved, so putting one back cannot name a client that is gone", (await db.KbRevisions.FirstAsync(x => x.Id == rev.Id)).ClientId == right.Id);
    var nw = await db.KbFolders.FirstAsync(f => f.Id == netWrong.Id);
    Check("...a top folder the right client already has (any case) says where it came from", nw.ClientId == right.Id && nw.Name == "Network (from Acme Ltdd)", nw.Name);
    var sw = await db.KbFolders.FirstAsync(f => f.Id == switches.Id);
    Check("...a folder inside it keeps its name and its place", sw.ClientId == right.Id && sw.Name == "Switches" && sw.ParentId == netWrong.Id);
    Check("...a top folder with a name of its own keeps it", (await db.KbFolders.FirstAsync(f => f.Id == printers.Id)).Name == "Printers");
    Check("...and the right client's own folder is untouched", (await db.KbFolders.FirstAsync(f => f.Id == netRight.Id)).Name == "network");

    Check("the recurring task moved", (await db.RecurringTasks.FirstAsync(x => x.Id == task.Id)).ClientId == right.Id);
    var wc = await db.WebChats.FirstAsync(x => x.Id == chat.Id);
    Check("the website chat moved, naming the Sue kept", wc.ClientId == right.Id && wc.ContactId == sueRight.Id);
    Check("the health check report moved", (await db.HealthCheckRuns.FirstAsync(x => x.Id == run.Id)).ClientId == right.Id);

    Check("another company's client and ticket are untouched", await Exists(foreign.Id)
        && (await db.Tickets.FirstAsync(t => t.Id == foreignTicket.Id)).ClientId == foreign.Id);
    Check("nothing anywhere names the removed client", !await db.Tickets.AnyAsync(t => t.ClientId == wrong.Id) && !await db.Devices.AnyAsync(d => d.ClientId == wrong.Id)
        && !await db.KbArticles.AnyAsync(a => a.ClientId == wrong.Id) && !await db.KbFolders.AnyAsync(f => f.ClientId == wrong.Id)
        && !await db.RecurringTasks.AnyAsync(x => x.ClientId == wrong.Id) && !await db.WebChats.AnyAsync(x => x.ClientId == wrong.Id)
        && !await db.HealthCheckRuns.AnyAsync(x => x.ClientId == wrong.Id) && !await db.KbRevisions.AnyAsync(x => x.ClientId == wrong.Id));
}

Head("afterwards");
Check("the right client now counts it all", (await clients.WhatIsOnAsync(tid, right.Id)).Tickets == 3);
var (again, _) = await clients.CreateAsync(tid, "Acme Ltdd", "", "", "", "", "");
Check("typing the misspelt name again makes a NEW, empty client — nothing comes back from the removed one",
    again != null && again.Id != wrong.Id && !(await clients.WhatIsOnAsync(tid, again.Id)).Any);
Check("the client search finds Acme Ltd once", (await clients.SearchAsync(tid, "acme ltd ")).Count(c => c.Name == "Acme Ltd") == 1);

try { Directory.Delete(dataDir, true); } catch { }
Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed ({HarnessDb.Name})");
return fail == 0 ? 0 : 1;

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
