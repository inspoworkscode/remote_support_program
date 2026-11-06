// =====================================================================================
// handoffcheck - proves stage 3.6.3: the portal knows who you are, and the console can open
// it as you without anybody typing a password twice.
//
// Three things, and all three fail quietly rather than loudly if they are wrong, which is
// why they are worth a harness:
//
//   THE HAND-OFF TOKEN. Single use and short lived. A token that could be used twice, or
//   that outlived its minute, would be a password sitting in a browser history - and
//   nothing about the console's behaviour would look any different.
//
//   THE PATH. It arrives from the console and ends in a redirect. "Starts with a slash" is
//   not a rule that keeps out "//evil.example.com", which a browser reads as another host
//   entirely. The allow-list is what stops that, so the allow-list is tested with the
//   things that would get past a naive check.
//
//   THE SCOPE. A technician sees their own tickets and the unassigned pool, and nothing of
//   a colleague's. Get this wrong and the portal simply shows more than it should, on a
//   page that otherwise looks perfectly normal.
//
//   dotnet run -c Release --project tools/handoffcheck/handoffcheck.csproj
// =====================================================================================

using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Tools;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

Console.WriteLine("handoffcheck - stage 3.6.3, who the portal thinks you are");
Console.WriteLine();
Console.WriteLine("== the hand-off token ==");

var handoff = new PortalHandoffService();
var tech = new TechSession(TenantId: 1, TechnicianId: 7, DisplayName: "Sue Jones",
    Email: "sue@example.test", IsAdmin: false, Role: TenantRole.Technician);

var token = handoff.Issue(tech, "/portal/tickets");
Check("a token is issued", !string.IsNullOrWhiteSpace(token));
Check("issuing a token does not leak the session into it",
    !token.Contains("sue", StringComparison.OrdinalIgnoreCase) && !token.Contains('@'), token);

var (first, path) = handoff.Redeem(token);
Check("redeeming returns the person who asked", first?.TechnicianId == 7 && first.Role == TenantRole.Technician);
Check("redeeming returns where they were going", path == "/portal/tickets", path);

// The property the whole design rests on.
var (second, _) = handoff.Redeem(token);
Check("the SAME token cannot be redeemed twice", second == null);

Check("a token nobody issued is refused", handoff.Redeem("not-a-real-token").session == null);
Check("an empty token is refused", handoff.Redeem("").session == null);
Check("a null token is refused", handoff.Redeem(null).session == null);

// Outstanding tokens are swept, so a console left clicking cannot grow the dictionary forever.
var before = handoff.Outstanding;
for (int i = 0; i < 20; i++) handoff.Issue(tech, null);
Check("issuing leaves tokens outstanding until used", handoff.Outstanding == before + 20, $"{handoff.Outstanding}");

Check("the token's life is under two minutes",
    PortalHandoffService.Lifetime < TimeSpan.FromMinutes(2), PortalHandoffService.Lifetime.ToString());

Console.WriteLine();
Console.WriteLine("== where a hand-off may land ==");

Check("the tickets queue is allowed", PortalHandoffService.SafePath("/portal/tickets") == "/portal/tickets");
Check("clients is allowed", PortalHandoffService.SafePath("/portal/clients") == "/portal/clients");
Check("no path at all lands on the queue", PortalHandoffService.SafePath(null) == PortalHandoffService.DefaultPath);
Check("an empty path lands on the queue", PortalHandoffService.SafePath("   ") == PortalHandoffService.DefaultPath);

// Everything below would get past "does it start with a slash".
Check("a protocol-relative URL is NOT followed",
    PortalHandoffService.SafePath("//evil.example.com") == PortalHandoffService.DefaultPath,
    PortalHandoffService.SafePath("//evil.example.com"));
Check("an absolute URL is NOT followed",
    PortalHandoffService.SafePath("https://evil.example.com/portal/tickets") == PortalHandoffService.DefaultPath);
Check("a traversal is NOT followed",
    PortalHandoffService.SafePath("/portal/tickets/../../etc") == PortalHandoffService.DefaultPath);
Check("a page nobody listed is NOT followed",
    PortalHandoffService.SafePath("/portal/billing") == PortalHandoffService.DefaultPath);
Check("a backslash trick is NOT followed",
    PortalHandoffService.SafePath("/\\evil.example.com") == PortalHandoffService.DefaultPath);
Check("a trailing slash still matches", PortalHandoffService.SafePath("/portal/tickets/") == "/portal/tickets");
Check("case does not matter", PortalHandoffService.SafePath("/Portal/Tickets") == "/portal/tickets");
Check("a query string is dropped rather than carried",
    PortalHandoffService.SafePath("/portal/tickets?filter=all") == "/portal/tickets");
Check("one ticket is followed, and so are its Request quote step and its photos",
    PortalHandoffService.SafePath("/portal/tickets/12") == "/portal/tickets/12"
    && PortalHandoffService.SafePath("/portal/tickets/12?open=quote-requested") == "/portal/tickets/12?open=quote-requested"
    && PortalHandoffService.SafePath("/portal/tickets/12?tab=photos") == "/portal/tickets/12?tab=photos");
Check("any other query on a ticket is NOT followed",
    PortalHandoffService.SafePath("/portal/tickets/12?open=abandon") == "/portal/tickets"
    && PortalHandoffService.SafePath("/portal/tickets/12?tab=photos&x=//evil.example.com") == "/portal/tickets");

// The path is fixed at issue time, so tampering after the fact is not a thing that exists.
var bad = handoff.Issue(tech, "//evil.example.com");
Check("a bad path is corrected when the token is MINTED, not when it is used",
    handoff.Redeem(bad).path == PortalHandoffService.DefaultPath);

Console.WriteLine();
Console.WriteLine("== who the portal thinks you are ==");

Check("an administrator is the tenant role", PortalIdentity.RoleNames(TenantRole.Admin).SequenceEqual(new[] { "tenant" }));
Check("billing keeps its own role", PortalIdentity.RoleNames(TenantRole.Billing).SequenceEqual(new[] { "billing" }));
Check("a technician has a role of their own", PortalIdentity.RoleNames(TenantRole.Technician).SequenceEqual(new[] { "technician" }));

Check("the owner lands on the dashboard", PortalIdentity.HomePage(TenantRole.Admin) == "/portal");
Check("the office administrator lands on billing", PortalIdentity.HomePage(TenantRole.Billing) == "/portal/billing");
Check("a technician lands on their tickets", PortalIdentity.HomePage(TenantRole.Technician) == "/portal/tickets");

var principal = PortalIdentity.Build(tech, "acme-it");
Check("the cookie carries the person, not just the company",
    PortalIdentity.TechnicianIdOf(principal) == 7, $"{PortalIdentity.TechnicianIdOf(principal)}");
Check("the cookie carries the role", PortalIdentity.RoleOf(principal) == TenantRole.Technician);
Check("the cookie carries the name", principal.Identity?.Name == "Sue Jones");
Check("the cookie puts them in their ASP.NET role", principal.IsInRole("technician"));
Check("a technician is NOT in the admin role", !principal.IsInRole("tenant"));

// A cookie issued before 3.6.3 has no role NUMBER. Anyone holding one was an administrator, because
// until 3.6.3 an administrator is the only thing that could sign in to the portal — and the cookie
// says so itself, with the ASP.NET role "tenant" it was issued with. Since 2026-09-29 that is what
// is read, rather than assuming administrator for any cookie at all (rolecheck has the rest).
var old = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
    new[] { new System.Security.Claims.Claim("tenantId", "1"), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "tenant") }, "cookie"));
Check("a cookie from before this stage is read as an administrator",
    PortalIdentity.RoleOf(old) == TenantRole.Admin);
var bare = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
    new[] { new System.Security.Claims.Claim("tenantId", "1") }, "cookie"));
Check("...but a cookie that says nothing is NOT", PortalIdentity.RoleOf(bare) == TenantRole.Technician);

Console.WriteLine();
Console.WriteLine("== what each role may see ==");

var dir = Path.Combine(Path.GetTempPath(), "handoffcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var dbPath = Path.Combine(dir, "platform.db");
try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={dbPath}").Options;
    await using (var seed = new PlatformDb(options))
    {
        await HarnessDb.MigrateAsync(seed);
        seed.Tenants.Add(new Tenant { Slug = "check", CompanyName = "Check IT", TenantKey = Guid.NewGuid().ToString("N") });
        await seed.SaveChangesAsync();
        int n = 0;
        foreach (var (subject, assigned) in new[]
                 {
                     ("Sue's own job", "Sue Jones"),
                     ("Sue's second job", "Sue Jones"),
                     ("Bob's job", "Bob Smith"),
                     ("Nobody's job yet", ""),
                 })
            seed.Tickets.Add(new Ticket { TenantId = 1, Number = ++n, Subject = subject, AssignedTo = assigned });
        await seed.SaveChangesAsync();
    }

    await using var db = new PlatformDb(options);
    List<string> Seen(TenantRole role, string me) =>
        TicketService.VisibleTo(db.Tickets.Where(t => t.TenantId == 1), role, me)
            .Select(t => t.Subject).ToList();

    var sue = Seen(TenantRole.Technician, "Sue Jones");
    Check("a technician sees their own tickets",
        sue.Contains("Sue's own job") && sue.Contains("Sue's second job"));
    Check("a technician sees the unassigned pool", sue.Contains("Nobody's job yet"));
    Check("a technician does NOT see a colleague's ticket", !sue.Contains("Bob's job"), string.Join(", ", sue));

    var owner = Seen(TenantRole.Admin, "Test Owner");
    Check("the owner sees everything", owner.Count == 4, $"{owner.Count} of 4");

    var office = Seen(TenantRole.Billing, "Test Office");
    Check("the office administrator sees everything", office.Count == 4, $"{office.Count} of 4");

    // The scope must survive the page's own filters, because it is applied first. A technician
    // asking for "all" is asking within what they are allowed, not instead of it.
    var allFilter = TicketService.VisibleTo(db.Tickets.Where(t => t.TenantId == 1), TenantRole.Technician, "Sue Jones")
        .Select(t => t.Subject).ToList();
    Check("filter=all cannot widen a technician's scope", !allFilter.Contains("Bob's job"));

    // A technician nobody has assigned anything to still sees the pool, and only the pool.
    var newcomer = Seen(TenantRole.Technician, "Brand New");
    Check("a technician with no tickets sees only the pool",
        newcomer.Count == 1 && newcomer[0] == "Nobody's job yet", string.Join(", ", newcomer));
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;
