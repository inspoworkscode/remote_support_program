// =====================================================================================
// rolecheck - proves the Owner / Supervisor split of 2026-09-24, and roles as tick boxes
// (2026-09-29, roadmap 3.6.8): a person holds a SET of roles and may do the union.
//
// Clint: "we need to separate owner and supervisor roles. a company could have multiple
// supervisors but only one owner. The owner role must not take away from any of the seats
// unless they select owner/supervisor."
//
// Everything is asked of TenantService, Permissions and the migration - never of a page:
//
//   the migration makes each company's first administrator its Owner / Supervisor, and nobody
//     else; a company with no administrator gets no owner rather than a made-up one
//   an owner-only login takes no seat and cannot open the console; Owner / Supervisor does both
//   owners: several since 2026-10-08 ("if more than one owner is selected, this must be considered
//     a seat taken"), only an owner makes, changes or removes one, and the last is never lost
//   only an owner can change an owner's row
//   a supervisor and the owner answer every permission question the old administrator did
//
//   dotnet run -c Debug --project tools/rolecheck/rolecheck.csproj
// =====================================================================================

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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

var dir = Path.Combine(Path.GetTempPath(), "rolecheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var dbPath = Path.Combine(dir, "platform.db");
Console.WriteLine("rolecheck - owners (at least one), any number of supervisors, and an owner's seat is optional");
Console.WriteLine();

try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={dbPath};Pooling=False").Options;

    // ---- the migration, run against rows written the way the product wrote them before it ----
    Console.WriteLine("== the migration ==");
    bool upgrade = HarnessDb.SqliteUpgradePart("the role migration");
    if (!upgrade)
        await using (var db = new PlatformDb(options))
        {
            // What the migration leaves behind, written through today's model: the same companies and people,
            // each with the role the checks below prove it gives them.
            await HarnessDb.MigrateAsync(db);
            foreach (var (id, slug) in new[] { (1, "a"), (2, "b"), (3, "c") }) await Seed.Tenant(db, id, slug, slug.ToUpperInvariant(), "k" + slug, seats: 5);
            foreach (var (id, tenant, role, off) in new[] {
                (1, 1, TenantRole.Admin, true), (2, 1, TenantRole.OwnerSupervisor, false), (3, 1, TenantRole.Admin, false), (4, 1, TenantRole.Technician, false),
                (5, 2, TenantRole.Billing, false), (6, 2, TenantRole.Technician, false), (7, 3, TenantRole.OwnerSupervisor, false) })
                db.Technicians.Add(new Technician { Id = id, TenantId = tenant, Email = $"p{id}@x.test", DisplayName = $"Person {id}", Role = role, Disabled = off,
                    CreatedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
            await HarnessDb.ResetSequencesAsync(db);
        }
    else
    await using (var db = new PlatformDb(options))
    {
        await db.GetService<IMigrator>().MigrateAsync("20260920135552_TechnicianAppearance");
        await Seed.OldTenant(db, 1, "a", "A", "ka", seats: 3);
        await Seed.OldTenant(db, 2, "b", "B", "kb", seats: 3);
        await Seed.OldTenant(db, 3, "c", "C", "kc", seats: 3);
        // People written by SQL against the table as it stood then: today's model has columns
        // (MailClient, 2026-09-24) that table does not, so writing through the model would fail —
        // and would not be how those rows were written anyway.
        // The role is written as the number the table held THEN (0 technician, 1 administrator,
        // 2 billing) — since 2026-09-29 TenantRole's own numbers are tick boxes and mean something
        // else, and writing (int)TenantRole.Admin here would seed an office administrator.
        async Task T(int id, int tenant, int oldRole, bool off = false) =>
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Technicians (Id, TenantId, Email, DisplayName, Salt, Hash, Role, Appearance, Disabled, CreatedUtc) " +
                "VALUES ({0}, {1}, {2}, {3}, '', '', {4}, '', {5}, '2026-09-01 00:00:00')",
                id, tenant, $"p{id}@x.test", $"Person {id}", oldRole, off ? 1 : 0);
        await T(1, 1, 1, off: true);   // A's creator, since switched off
        await T(2, 1, 1);
        await T(3, 1, 1);
        await T(4, 1, 0);
        await T(5, 2, 2);            // B has no administrator at all
        await T(6, 2, 0);
        await T(7, 3, 1);
        await HarnessDb.MigrateAsync(db);
    }
    if (upgrade)
    await using (var db = new PlatformDb(options))
    {
        var r = await db.Technicians.ToDictionaryAsync(t => t.Id, t => t.Role);
        Check("A: the first ENABLED administrator becomes Owner / Supervisor", r[2] == TenantRole.OwnerSupervisor, r[2].ToString());
        Check("A: the switched-off creator stays a supervisor", r[1] == TenantRole.Admin, r[1].ToString());
        Check("A: every other administrator is a supervisor", r[3] == TenantRole.Admin, r[3].ToString());
        Check("A: technicians are untouched", r[4] == TenantRole.Technician);
        Check("B: the office administrator is still the office administrator", r[5] == TenantRole.Billing, r[5].ToString());
        Check("B: and the technician still a technician", r[6] == TenantRole.Technician, r[6].ToString());
        Check("B: no administrator, so no owner is invented", !r.Where(kv => kv.Key is 5 or 6).Any(kv => kv.Value.Has(TenantRoles.Owners)));
        Check("C: a lone administrator becomes Owner / Supervisor", r[7] == TenantRole.OwnerSupervisor);
        var owners = await db.Technicians.Where(t => (t.Role & TenantRoles.Owners) != 0).Select(t => t.TenantId).ToListAsync();
        Check("exactly one owner per company that had an administrator", owners.Count == 2 && owners.Distinct().Count() == 2, string.Join(",", owners));
    }

    // ---- seats and the console ----
    Console.WriteLine();
    Console.WriteLine("== seats and the console ==");
    Technician P(TenantRole r) => new() { Role = r };
    Check("an owner-only login takes NO seat", !P(TenantRole.Owner).UsesSeat);
    Check("an owner-only login cannot open the console", !P(TenantRole.Owner).CanUseConsole);
    Check("Owner / Supervisor takes a seat", P(TenantRole.OwnerSupervisor).UsesSeat);
    Check("Owner / Supervisor can open the console", P(TenantRole.OwnerSupervisor).CanUseConsole);
    Check("a supervisor takes a seat and opens the console", P(TenantRole.Admin).UsesSeat && P(TenantRole.Admin).CanUseConsole);
    // Since 2026-10-05 an office administrator pays for a seat — and still never reaches the console.
    Check("an office administrator takes a seat and stays out of the console", P(TenantRole.Billing).UsesSeat && !P(TenantRole.Billing).CanUseConsole);
    Check("labels say Supervisor and Office administrator",
        TenantRoles.Label(TenantRole.Admin) == "Supervisor" && TenantRoles.Label(TenantRole.Billing) == "Office administrator");

    // ---- permissions ----
    Console.WriteLine();
    Console.WriteLine("== permissions ==");
    {
        var r = TenantRole.OwnerSupervisor;
        bool same = Permissions.CanManageCompany(r) == Permissions.CanManageCompany(TenantRole.Admin)
            && Permissions.CanSeeAllTickets(r) == Permissions.CanSeeAllTickets(TenantRole.Admin)
            && Permissions.CanChangeStatus(r) == Permissions.CanChangeStatus(TenantRole.Admin)
            && Permissions.CanAssignToAnyone(r) == Permissions.CanAssignToAnyone(TenantRole.Admin)
            && Permissions.CanClaim(r) == Permissions.CanClaim(TenantRole.Admin)
            && Permissions.CanSeeWorkload(r) == Permissions.CanSeeWorkload(TenantRole.Admin);
        Check($"{TenantRoles.Label(r)} may do everything a supervisor may", same);
    }

    // ---- an owner-only login oversees and comments; it does not work tickets (Clint, 2026-10-15) ----
    Console.WriteLine();
    Console.WriteLine("== an owner-only login (2026-10-15) ==");
    var own = TenantRole.Owner;
    Check("it runs the company: people, branding, settings, outside help, machines",
        Permissions.CanManageCompany(own) && Permissions.CanAcceptPrice(own) && Permissions.CanAllowOutsiders(own)
        && Permissions.CanRemoveDevices(own) && Permissions.CanConfirmReinstall(own) && Permissions.CanClearSavedPasswords(own));
    Check("it sees every ticket, the workload, every client and machine, and imports clients",
        Permissions.CanSeeAllTickets(own) && Permissions.CanSeeWorkload(own) && Permissions.CanLinkDevices(own) && Permissions.CanImportClients(own));
    Check("it sets up the item list and the suppliers", Permissions.CanEditItems(own) && Permissions.CanEditSuppliers(own));
    Check("it may COMMENT (and so @mention)", Permissions.CanComment(own));
    Check("it does not work tickets", !Permissions.CanWorkTickets(own));
    Check("...no stage, status or priority, no work done", !Permissions.CanChangeStatus(own) && !Permissions.CanAddWorkDone(own) && !Permissions.CanStepInAnywhere(own));
    Check("...no assigning, taking or passing on", !Permissions.CanAssignToAnyone(own) && !Permissions.CanClaim(own) && !Permissions.CanPassOn(own));
    Check("...opens no ticket", !Permissions.CanOpenTicket(own) && !Permissions.CanAssignWhenOpening(own));
    Check("...no billing queue, no closing, no approving", !Permissions.CanWorkBilling(own) && !Permissions.CanCloseWorkflowTicket(own) && !Permissions.CanApprove(own));
    Check("...emails no client from a ticket", !Permissions.CanEmailClient(own));
    Check("...answers no website chat and hands over nobody's", !Permissions.CanAnswerWebChat(own) && !Permissions.CanHandOverAnyWebChat(own));
    Check("...and is not told of billing, even with no office administrator", !Permissions.HearsOfBilling(own, false) && !Permissions.HearsOfBilling(own, true));
    var tk = new Ticket { AssignedTo = "Olive Owner", WorkedBy = "Olive Owner", Stage = Workflow.RepairableStage };
    Check("...presses no button in any lane, even on a job with its own name on it",
        !Workflow.MayAct(WorkLane.Bench, tk, own, "Olive Owner") && !Workflow.MayAct(WorkLane.Office, tk, own, "Olive Owner")
        && !Workflow.MayAct(WorkLane.Supervisor, tk, own, "Olive Owner") && !Workflow.MayMarkCollected(tk, own, "Olive Owner"));
    Check("...edits no quote lines", !QuoteService.MayEditLines(tk, own, "Olive Owner"));
    Check("...goes back on no step, not even one it wrote",
        !TicketService.MayGoBack(tk, new TicketNote { Author = "Olive Owner", Stage = Workflow.GeneralFirst }, own, "Olive Owner"));
    Check("ticking ANY other box beside Owner gives the ticket work back",
        Permissions.CanStepInAnywhere(TenantRole.Owner | TenantRole.Technician) && Permissions.CanWorkBilling(TenantRole.Owner | TenantRole.Technician)
        && Permissions.CanWorkBilling(TenantRole.Owner | TenantRole.Billing) && Permissions.CanAnswerWebChat(TenantRole.Owner | TenantRole.Billing)
        && Permissions.CanApprove(TenantRole.OwnerSupervisor) && Permissions.CanEmailClient(TenantRole.Owner | TenantRole.Technician));
    Check("a supervisor, technician and office administrator still work tickets",
        Permissions.CanWorkTickets(TenantRole.Admin) && Permissions.CanWorkTickets(TenantRole.Technician) && Permissions.CanWorkTickets(TenantRole.Billing)
        && Permissions.CanStepInAnywhere(TenantRole.Admin) && Permissions.CanWorkBilling(TenantRole.Billing) && Permissions.CanChangeStatus(TenantRole.Technician));

    // The page lock: an allow-list on the ticket pages, asked before any handler runs.
    var ticketPage = typeof(RemoteSupport.Platform.Pages.Portal.TicketModel);
    var newPage = typeof(RemoteSupport.Platform.Pages.Portal.NewTicketModel);
    var recurPage = typeof(RemoteSupport.Platform.Pages.Portal.RecurringEditModel);
    bool Refused(Type page, string method, string? handler, TenantRole r) => RemoteSupport.Platform.Pages.Portal.TicketWorkAttribute.Refuses(page, method, handler, r);
    Check("the ticket page opens for it, and its comment box posts", !Refused(ticketPage, "GET", null, own) && !Refused(ticketPage, "POST", "Comment", own)
        && !Refused(ticketPage, "POST", "Update", own));
    var workHandlers = new[] { "Act", "Edit", "Claim", "Link", "SetClient", "WorkDone", "AddLine", "AddLinePhotos", "RemoveLine", "SetMachine",
        "AddSupplier", "RequestQuote", "EmailNote", "AddSender", "SomethingAddedNextMonth" };
    var through = workHandlers.Where(h => !Refused(ticketPage, "POST", h, own)).ToList();
    Check("every other handler on it is refused — a new one too, until it is allowed", through.Count == 0, string.Join(",", through));
    Check("New ticket and a recurring task's page are refused outright",
        Refused(newPage, "GET", null, own) && Refused(newPage, "POST", null, own) && Refused(recurPage, "GET", null, own) && Refused(recurPage, "POST", "Save", own));
    Check("nobody who works tickets is refused anything there",
        new[] { TenantRole.Technician, TenantRole.Billing, TenantRole.Admin, TenantRole.OwnerSupervisor }
            .All(r => workHandlers.All(h => !Refused(ticketPage, "POST", h, r)) && !Refused(newPage, "GET", null, r)));
    Check("a page that is not ticket work is never refused", !Refused(typeof(RemoteSupport.Platform.Pages.Portal.ClientsModel), "POST", "Add", own));
    Check("a supervisor manages the company", Permissions.CanManageCompany(TenantRole.Admin));
    Check("technicians and office administrators do not", !Permissions.CanManageCompany(TenantRole.Technician) && !Permissions.CanManageCompany(TenantRole.Billing));
    Check("only an owner may change the owner", Permissions.CanChangeOwner(TenantRole.Owner) && Permissions.CanChangeOwner(TenantRole.OwnerSupervisor)
        && !Permissions.CanChangeOwner(TenantRole.Admin) && !Permissions.CanChangeOwner(TenantRole.Billing) && !Permissions.CanChangeOwner(TenantRole.Technician));
    Check("the portal treats both owners as the company's administrators",
        PortalIdentity.RoleNames(TenantRole.Owner).Contains("tenant") && PortalIdentity.RoleNames(TenantRole.OwnerSupervisor).Contains("tenant")
        && PortalIdentity.HomePage(TenantRole.Owner) == "/portal");
    // "owner" is the ASP.NET role of the PLATFORM's operator. A company's owner wearing it would
    // be able to approve other companies' signups.
    Check("a company's owner is never given the PLATFORM owner's role", !PortalIdentity.RoleNames(TenantRole.Owner).Contains("owner")
        && !PortalIdentity.RoleNames(TenantRoles.All).Contains("owner"));

    // ---- owners, through the service ----
    Console.WriteLine();
    Console.WriteLine("== the owner's row ==");
    var cfg = new ConfigurationBuilder().Build();
    var tenants = new TenantService(new Factory(options), cfg, dir, DataProtectionProvider.Create("rolecheck"));
    // Company C: p7 is Owner / Supervisor, 3 seats.
    var (sup, _) = await tenants.AddTechnicianAsync(3, "sup@x.test", "password1", "Sup", TenantRole.Admin);
    Check("a second supervisor can be added", sup != null);
    Check("a supervisor cannot change the owner's row", (await tenants.SetRoleAsync(3, "p7@x.test", TenantRole.Owner, TenantRole.Admin)) == TenantService.OnlyAnOwner);
    Check("the ONLY owner cannot untick Owner — a company always has one",
        (await tenants.SetRoleAsync(3, "p7@x.test", TenantRole.Admin, TenantRole.OwnerSupervisor)) == TenantService.LastOwner);
    var gaveUp = await tenants.SetRoleAsync(3, "p7@x.test", TenantRole.Owner, TenantRole.OwnerSupervisor);
    Check("the owner can give up their seat (Owner / Supervisor -> Owner)", gaveUp == "", gaveUp);
    var list = await tenants.TechniciansAsync(3);
    Check("...and the seat is free again", Pricing.PeopleInSeats(list) == 1, $"{Pricing.PeopleInSeats(list)} seated");
    // Since 2026-10-05 seats are a PRICE, not a cap: past the package each person is one more seat,
    // agreed by the owner first (seatcheck is where that is proved). The migration lifted this
    // company's package to seven, and FiveSeatPackage (2026-10-08) made it five; it is put back to three so it fills.
    if (upgrade) Check("the migrations made the package five seats", (await tenants.ByIdAsync(3))!.Seats == 5);
    await tenants.UpdateBrandingAsync(3, t => t.Seats = 3);
    await tenants.AddTechnicianAsync(3, "t1@x.test", "password1", "T1", TenantRole.Technician);
    await tenants.AddTechnicianAsync(3, "t2@x.test", "password1", "T2", TenantRole.Technician);
    var full = await tenants.SetRoleAsync(3, "p7@x.test", TenantRole.OwnerSupervisor, TenantRole.Owner);
    Check("taking the seat back when every seat is in use is refused (no more allowed for this company)", full.Contains("may have 3 seats"), full);
    var (oaBySup, oaErr) = await tenants.AddTechnicianAsync(3, "oa@x.test", "password1", "OA", TenantRole.Billing, TenantRole.Admin);
    Check("an office administrator is no longer free: one more past the package is REFUSED", oaBySup == null && oaErr.Contains("may have 3 seats"), oaErr);
    // Back to the five every company has, so the owners below have room.
    await tenants.UpdateBrandingAsync(3, t => t.Seats = 5);
    await tenants.AddTechnicianAsync(3, "oa@x.test", "password1", "OA", TenantRole.Billing);
    Check("the ONLY owner cannot be removed, even from the command line", (await tenants.RemoveTechnicianAsync(3, "p7@x.test")) == TenantService.LastOwner);

    // Clint, 2026-10-08: several owners, and "if more than one owner is selected, this must be considered
    // a seat taken even if no extra roles are assigned". Seats now: sup, t1, t2, oa = 4 of 5.
    Console.WriteLine();
    Console.WriteLine("== several owners (2026-10-08) ==");
    int Seats3() => Pricing.PeopleInSeats(tenants.TechniciansAsync(3).Result);
    var (bySupOwner, bySupOwnerErr) = await tenants.AddTechnicianAsync(3, "two@x.test", null, "Second", TenantRole.Owner, TenantRole.Admin);
    Check("a SUPERVISOR adding an owner is refused", bySupOwner == null && bySupOwnerErr == TenantService.OnlyAnOwner, bySupOwnerErr);
    var (two, twoErr) = await tenants.AddTechnicianAsync(3, "two@x.test", null, "Second", TenantRole.Owner, TenantRole.Owner);
    Check("an owner adds a second owner on their own — who takes a seat: TWO owners, five seats", two != null && two.IsOwner && Seats3() == 5, $"{twoErr} {Seats3()}");
    var (three, threeErr) = await tenants.AddTechnicianAsync(3, "three@x.test", null, "Third", TenantRole.Owner, TenantRole.Owner);
    Check("...and a third, at five, is refused — an owner-only login past the first is a seat", three == null && threeErr.Contains("may have 5 seats"), threeErr);
    Check("a supervisor cannot make themselves an owner", (await tenants.SetRoleAsync(3, "sup@x.test", TenantRole.OwnerSupervisor, TenantRole.Admin)) == TenantService.OnlyAnOwner);
    Check("a supervisor cannot change the second owner's row", (await tenants.SetRoleAsync(3, "two@x.test", TenantRole.Owner | TenantRole.Billing, TenantRole.Admin)) == TenantService.OnlyAnOwner);
    Check("a supervisor cannot remove an owner", (await tenants.RemoveTechnicianAsync(3, "two@x.test", TenantRole.Admin)).Contains("Only an owner"));
    var supOwner = await tenants.SetRoleAsync(3, "sup@x.test", TenantRole.OwnerSupervisor, TenantRole.Owner);
    Check("an owner makes a supervisor an owner too — no new seat, so no question", supOwner == "" && Seats3() == 5, supOwner);
    var p7Steps = await tenants.SetRoleAsync(3, "p7@x.test", TenantRole.Admin, TenantRole.Owner);
    Check("with other owners, an owner may give up being one", p7Steps == "" && !tenants.TechniciansAsync(3).Result.First(x => x.Email == "p7@x.test").IsOwner, p7Steps);
    Check("...and the second owner on their own is now the free one: still five", Seats3() == 5, $"{Seats3()}");
    var removedTwo = await tenants.RemoveTechnicianAsync(3, "two@x.test", TenantRole.OwnerSupervisor);
    Check("an owner removes another owner", removedTwo == "" && Seats3() == 5, removedTwo);
    Check("...and the owner left is the last one: not removable", (await tenants.RemoveTechnicianAsync(3, "sup@x.test", TenantRole.OwnerSupervisor)) == TenantService.LastOwner);

    // Handing over (command line): the new owner gains the box; EVERY other owner becomes a supervisor.
    await tenants.SetRoleAsync(3, "sup@x.test", TenantRole.Owner, TenantRole.Owner);                       // sup: owner only, free
    await tenants.SetRoleAsync(3, "t2@x.test", TenantRole.Owner | TenantRole.Technician, TenantRole.Owner); // t2: an owner too
    var before = (await tenants.SeatAgreementsAsync(3)).Count;
    var moved = await tenants.SetOwnerAsync(3, "p7@x.test");
    list = await tenants.TechniciansAsync(3);
    Check("handing the company over works from the command line, the old owner taking the last seat", moved == "" && Seats3() == 5, $"{moved} {Seats3()}");
    Check("...and nothing goes on the price record — seats no longer raise the price", (await tenants.SeatAgreementsAsync(3)).Count == before);
    Check("...the new owner kept their boxes", list.First(x => x.Email == "p7@x.test").Role == TenantRole.OwnerSupervisor);
    Check("...EVERY old owner is now a supervisor", list.First(x => x.Email == "sup@x.test").Role == TenantRole.Admin
        && list.First(x => x.Email == "t2@x.test").Role == (TenantRole.Technician | TenantRole.Admin));
    Check("...and there is exactly one owner", list.Count(x => x.IsOwner) == 1);

    // ---- who appears in an assignee list ----
    await using (var db = new PlatformDb(options))
    {
        var workers = await db.Technicians.Where(t => t.TenantId == 3 && (t.Role & TenantRoles.Seated) != 0).Select(t => t.Email).ToListAsync();
        Check("the assignee list is the seated people, and not the office administrator", workers.Contains("t1@x.test") && !workers.Contains("oa@x.test"));
    }

    // ---- 3.6.8: roles are tick boxes, and a person holds the union ----
    Console.WriteLine();
    Console.WriteLine("== tick boxes (3.6.8) ==");
    var techOffice = TenantRole.Technician | TenantRole.Billing;
    Check("technician + office administrator: one seat, and the console",
        P(techOffice).UsesSeat && P(techOffice).CanUseConsole);
    Check("...moves a job along like a technician", Permissions.CanChangeStatus(techOffice) && Permissions.CanAddWorkDone(techOffice) && Permissions.CanClaim(techOffice));
    Check("...and works the billing queue like the office", Permissions.CanWorkBilling(techOffice) && Permissions.CanCloseWorkflowTicket(techOffice)
        && Permissions.CanAssignToAnyone(techOffice) && Permissions.CanSeeWorkload(techOffice));
    Check("...sees every ticket, because the office box does", Permissions.CanSeeAllTickets(techOffice));
    Check("...and still does NOT run the company", !Permissions.CanManageCompany(techOffice) && !Permissions.CanApprove(techOffice));
    Check("a technician alone still sees only their own", !Permissions.CanSeeAllTickets(TenantRole.Technician));
    Check("no box at all may do nothing that matters",
        !Permissions.CanSeeAllTickets(TenantRole.None) && !Permissions.CanChangeStatus(TenantRole.None) && !Permissions.CanWorkBilling(TenantRole.None)
        && !Permissions.CanManageCompany(TenantRole.None) && !P(TenantRole.None).UsesSeat);
    var ownerTech = TenantRole.Owner | TenantRole.Technician;
    Check("owner + technician: one seat, the console, and runs the company",
        P(ownerTech).UsesSeat && P(ownerTech).CanUseConsole && Permissions.CanManageCompany(ownerTech) && Permissions.CanChangeOwner(ownerTech));
    Check("labels list every box, in reading order", TenantRoles.Label(TenantRole.Technician | TenantRole.Billing | TenantRole.Owner) == "Owner, Technician, Office administrator",
        TenantRoles.Label(TenantRole.Technician | TenantRole.Billing | TenantRole.Owner));
    Check("the portal gives both boxes' roles", PortalIdentity.RoleNames(techOffice).OrderBy(x => x).SequenceEqual(new[] { "billing", "technician" }));
    Check("...and lands them on the office's home, which has the technician's links too", PortalIdentity.HomePage(techOffice) == "/portal/billing");

    // The People page's own reading of what the boxes posted.
    Check("the page reads several ticked boxes as one set",
        RemoteSupport.Platform.Pages.Portal.TechniciansModel.Parse(new[] { "technician", "office" }) == techOffice);
    Check("a stale page's Owner / Supervisor still means both",
        RemoteSupport.Platform.Pages.Portal.TechniciansModel.Parse(new[] { "owner-supervisor" }) == TenantRole.OwnerSupervisor);
    Check("nothing ticked reads as nothing", RemoteSupport.Platform.Pages.Portal.TechniciansModel.Parse(Array.Empty<string>()) == TenantRole.None);

    // Through the service, in a company of its own with 2 seats.
    await using (var db = new PlatformDb(options))
    {
        db.Tenants.Add(new Tenant { Id = 4, Slug = "d", CompanyName = "D", TenantKey = "kd", Seats = 2 });
        await db.SaveChangesAsync();
    }
    var (both, bothErr) = await tenants.AddTechnicianAsync(4, "both@x.test", "password1", "Both", techOffice);
    Check("a person can be ADDED with two boxes", both?.Role == techOffice, bothErr);
    Check("a person with no box is REFUSED", (await tenants.AddTechnicianAsync(4, "none@x.test", "password1", "None", TenantRole.None)).tech == null);
    Check("setting no box is REFUSED", (await tenants.SetRoleAsync(4, "both@x.test", TenantRole.None, TenantRole.Admin)).Length > 0);
    Check("a number that is not a box is REFUSED", (await tenants.SetRoleAsync(4, "both@x.test", (TenantRole)64, TenantRole.Admin)).Length > 0);
    await tenants.AddTechnicianAsync(4, "tech@x.test", "password1", "Tech", TenantRole.Technician);
    var dList = await tenants.TechniciansAsync(4);
    Check("two people, one of them with two boxes, are TWO seats", dList.Count(x => x.UsesSeat && !x.Disabled) == 2);
    var (techs4, _, _) = await tenants.CountsAsync(4);
    Check("...and the database agrees, counting by box", techs4 == 2, techs4.ToString());
    Check("adding the office box to a seated person needs no new seat",
        await tenants.SetRoleAsync(4, "tech@x.test", techOffice, TenantRole.Admin) == "");
    Check("an office-only person takes a seat too — past the package nobody can add one, the command line included",
        (await tenants.AddTechnicianAsync(4, "oa4@x.test", "password1", "OA4", TenantRole.Billing, TenantRole.Admin)).tech == null
        && (await tenants.AddTechnicianAsync(4, "oa4@x.test", "password1", "OA4", TenantRole.Billing)).tech == null);
    await tenants.UpdateBrandingAsync(4, t => t.Seats = 3);
    Check("...with a third seat, they are added", (await tenants.AddTechnicianAsync(4, "oa4@x.test", "password1", "OA4", TenantRole.Billing)).tech != null);
    var dNow = await tenants.TechniciansAsync(4);
    Check("...so adding the technician box to them needs no NEW seat",
        Pricing.SeatsIf(dNow, dNow.First(x => x.Email == "oa4@x.test"), techOffice) == Pricing.PeopleInSeats(dNow));
    await using (var db = new PlatformDb(options))
    {
        var seated = await db.Technicians.Where(t => t.TenantId == 4 && (t.Role & TenantRoles.Seated) != 0).Select(t => t.Email).OrderBy(e => e).ToListAsync();
        Check("the database finds a two-box person among the seated, and not the office-only one",
            seated.SequenceEqual(new[] { "both@x.test", "tech@x.test" }), string.Join(",", seated));
        var visible = TicketService.VisibleTo(db.Tickets, techOffice, "Both").ToQueryString();
        Check("technician + office is not narrowed to their own tickets", !visible.Contains("WHERE"), "");
    }
    var handed = await tenants.SetOwnerAsync(4, "both@x.test");
    var dOwner = (await tenants.TechniciansAsync(4)).First(x => x.Email == "both@x.test");
    Check("handing the company to somebody keeps every box they had", handed == "" && dOwner.Role == (techOffice | TenantRole.Owner), dOwner.Role.ToString());

    // ---- the cookie: the numbers changed, so an old one must never read as more than it was ----
    Console.WriteLine();
    Console.WriteLine("== the portal's cookie ==");
    System.Security.Claims.ClaimsPrincipal Cookie(params (string type, string value)[] claims) =>
        new(new System.Security.Claims.ClaimsIdentity(claims.Select(c => new System.Security.Claims.Claim(c.type, c.value)), "test"));
    var built = PortalIdentity.Build(new TechSession(4, 1, "Both", "both@x.test", false, techOffice), "d");
    Check("a new cookie reads back as the same boxes", PortalIdentity.RoleOf(built) == techOffice);
    Check("...and carries a role for each", built.IsInRole("technician") && built.IsInRole("billing") && !built.IsInRole("tenant"));
    Check("an OLD cookie's supervisor (1) is still a supervisor, not a technician",
        PortalIdentity.RoleOf(Cookie((PortalIdentity.OldTenantRoleClaim, "1"))) == TenantRole.Admin);
    Check("an OLD cookie's technician (0) is still a technician",
        PortalIdentity.RoleOf(Cookie((PortalIdentity.OldTenantRoleClaim, "0"))) == TenantRole.Technician);
    Check("an OLD cookie's office administrator (2) is still the office",
        PortalIdentity.RoleOf(Cookie((PortalIdentity.OldTenantRoleClaim, "2"))) == TenantRole.Billing);
    Check("a cookie with no role is the LEAST, never an administrator",
        PortalIdentity.RoleOf(Cookie()) == TenantRole.Technician);
    Check("a cookie from before 3.6.3 is read from the roles it was issued with",
        PortalIdentity.RoleOf(Cookie((System.Security.Claims.ClaimTypes.Role, "tenant"))) == TenantRole.Admin
        && PortalIdentity.RoleOf(Cookie((System.Security.Claims.ClaimTypes.Role, "billing"))) == TenantRole.Billing);
    Check("a cookie with a number that is not a box is the least too",
        PortalIdentity.RoleOf(Cookie((PortalIdentity.TenantRolesClaim, "64"))) == TenantRole.Technician
        && PortalIdentity.RoleOf(Cookie((PortalIdentity.TenantRolesClaim, "0"))) == TenantRole.Technician);
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { try { Directory.Delete(dir, true); } catch { } }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} passed");
return fail == 0 ? 0 : 1;

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
