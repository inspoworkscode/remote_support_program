// =====================================================================================
// mentioncheck - proves step 4 of the Workshop workflow: @mentions (2026-09-24).
//
// Clint: "if more details are needed one can simply post in a comment section @so and so what is
// going on here and it flags that person to add to the comments". Answer 18: staff only, a badge,
// and the flag stays until they reply.
//
// Asked of TicketService, never of a page:
//
//   who a comment mentions - full names, a first name while it is unique, never an email address
//   the flag is raised, shows on the right person's badge, and only theirs
//   a technician asked about a colleague's job can open it to answer
//   the flag clears when they reply on THAT ticket - a comment or a Work done entry - and not
//     when they write somewhere else
//   automatic lines, yourself, switched-off people and another company's people raise nothing
//
//   dotnet run -c Debug --project tools/mentioncheck/mentioncheck.csproj
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

var dir = Path.Combine(Path.GetTempPath(), "mentioncheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
Console.WriteLine("mentioncheck - @someone, a badge, and a flag that stays until they reply");
Console.WriteLine();

const string Sue = "Sue Jones", Bob = "Bob Smith", Olive = "Olive Office", Sam1 = "Sam Adams", Sam2 = "Sam Baker", Gone = "Gary Gone";

try
{
    Console.WriteLine("== reading a comment ==");
    string[] people = { Sue, Bob, Olive, Sam1, Sam2, "Sue" };
    string Found(string body) => string.Join(",", TicketService.MentionsIn(body, people));
    Check("a full name is a mention", Found("@Bob Smith what is going on here?") == Bob, Found("@Bob Smith what is going on here?"));
    Check("case does not matter", Found("@bob smith?") == Bob);
    Check("a first name is enough while only one person has it", Found("@Bob can you look") == Bob);
    Check("an ambiguous first name mentions nobody", Found("@Sam can you look") == "", Found("@Sam can you look"));
    Check("...and the full name still works", Found("@Sam Baker can you look") == Sam2);
    Check("the longest name wins: \"@Sue Jones\" is not \"@Sue\" and the word Jones", Found("@Sue Jones please") == Sue, Found("@Sue Jones please"));
    Check("an email address is NOT a mention", Found("send it to bob@smith.test") == "");
    Check("two people in one comment", Found("@Bob Smith and @Olive Office, see above") == $"{Bob},{Olive}");
    Check("the same person twice is one mention", Found("@Bob Smith ... @bob smith") == Bob);
    Check("a name that runs on into a longer word is not a match", Found("@Bobby is not here") == "");

    // ---- the list that pops up on @ (2026-10-21) ----
    // Clint: "putting in the @ should list everyone assigned to the task". The console uses MentionPick; the
    // portal's mentionpick.js is the same rule, walked in the browser against these same cases.
    Console.WriteLine();
    Console.WriteLine("== the list that pops up on @ ==");
    string Tok(string text, int caret) => RemoteSupport.Shared.MentionPick.Typing(text, caret) is { } t ? $"{t.At}:{t.Query}" : "none";
    Check("a bare @ opens it", Tok("ask @", 5) == "4:", Tok("ask @", 5));
    Check("what follows is what it narrows by", Tok("ask @Bo", 7) == "4:Bo", Tok("ask @Bo", 7));
    Check("an email address does not open it", Tok("mail bob@smi", 12) == "none");
    Check("a new line ends it", Tok("@Bob\nmore", 9) == "none");
    Check("a space straight after the @ is not a name", Tok("cost @ R50", 7) == "none");
    var onJob = new[] { Bob, Sue };
    string Pick(string q, string me) => string.Join(",", RemoteSupport.Shared.MentionPick.Matches(q, onJob, people, me).Select(c => (c.OnTicket ? "*" : "") + c.Name));
    Check("the people on the ticket come first, then everybody else, never the one typing",
          Pick("", Olive) == $"*{Bob},*{Sue},{Sam1},{Sam2},Sue", Pick("", Olive));
    Check("...and the one typing is left out even when they are on it", !Pick("", Bob).Contains(Bob), Pick("", Bob));
    Check("narrowing by any word of the name: \"s\" finds Smith and the Sams", Pick("s", Olive) == $"*{Bob},*{Sue},{Sam1},{Sam2},Sue", Pick("s", Olive));
    Check("narrowing by the start of the name, with a space typed", Pick("sam b", Olive) == Sam2, Pick("sam b", Olive));
    Check("nobody matching closes it", Pick("zz", Olive) == "");
    Check("a whole name and a space typed closes it", Pick("Bob Smith ", Olive) == "", Pick("Bob Smith ", Olive));
    var put = RemoteSupport.Shared.MentionPick.Insert("ask @Bo please", 4, 7, Bob);
    Check("choosing puts the whole name in place of what was typed", put.Text == "ask @Bob Smith please" && put.Caret == 15, $"\"{put.Text}\" caret {put.Caret}");
    var end = RemoteSupport.Shared.MentionPick.Insert("ask @Bo", 4, 7, Bob);
    Check("...with a space after it at the end of the words", end.Text == "ask @Bob Smith " && end.Caret == end.Text.Length, $"\"{end.Text}\"");
    Check("...and what it puts in is read as a mention", Found(end.Text) == Bob);
    var onIt = TicketService.OnTicket($"{Sue}, {Bob}", Olive, people);
    Check("on the ticket: everybody assigned, then whoever did the work", string.Join(",", onIt) == $"{Sue},{Bob},{Olive}", string.Join(",", onIt));
    Check("...each once, and only people who can be mentioned", string.Join(",", TicketService.OnTicket($"{Sue}, {Gone}", Sue, people)) == Sue);

    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options))
    {
        await HarnessDb.MigrateAsync(db);
        db.Tenants.Add(new Tenant { Id = 1, Slug = "m", CompanyName = "M", TenantKey = "km" });
        db.Tenants.Add(new Tenant { Id = 2, Slug = "n", CompanyName = "N", TenantKey = "kn" });
        Technician P(int tenant, string name, TenantRole r, bool off = false) =>
            new() { TenantId = tenant, Email = $"{name.Replace(' ', '.')}.{tenant}@x.test", DisplayName = name, Role = r, Disabled = off };
        db.Technicians.AddRange(P(1, Sue, TenantRole.Technician), P(1, Bob, TenantRole.Technician), P(1, Olive, TenantRole.Billing),
            P(1, Gone, TenantRole.Technician, off: true), P(2, "Nina Other", TenantRole.Technician));
        await db.SaveChangesAsync();
    }
    var tickets = new TicketService(new Factory(options));
    async Task<List<TicketMention>> All() { await using var db = new PlatformDb(options); return await db.TicketMentions.ToListAsync(); }

    Console.WriteLine();
    Console.WriteLine("== raising the flag ==");
    var job = await tickets.CreateAsync(1, "Laptop", "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.Workshop);
    var other = await tickets.CreateAsync(1, "Printer", "", Olive, TicketSource.Manual, assignedTo: Sue);

    await using (var db = new PlatformDb(options))
        Check("before being asked, Bob cannot see Sue's job", !await TicketService.VisibleTo(db.Tickets, TenantRole.Technician, Bob).AnyAsync(t => t.Id == job.Id));

    await tickets.AddNoteAsync(1, job.Id, Olive, "@Bob Smith what is going on here?", false);
    var bobs = await tickets.OpenMentionsAsync(1, Bob);
    Check("Bob is flagged", bobs.Count == 1 && bobs[0].TicketId == job.Id && bobs[0].MentionedBy == Olive);
    Check("...the flag knows which ticket, for the badge to link to", bobs[0].Ticket?.Number == job.Number);
    Check("nobody else is", (await tickets.OpenMentionsAsync(1, Sue)).Count == 0 && (await tickets.OpenMentionsAsync(1, Olive)).Count == 0);
    await using (var db = new PlatformDb(options))
        Check("now Bob CAN open Sue's job, to answer", await TicketService.VisibleTo(db.Tickets, TenantRole.Technician, Bob).AnyAsync(t => t.Id == job.Id));

    await tickets.AddNoteAsync(1, job.Id, Olive, "Asked @Olive Office myself", false);
    Check("mentioning yourself flags nobody", (await tickets.OpenMentionsAsync(1, Olive)).Count == 0);
    await tickets.AddNoteAsync(1, job.Id, "System", "@Sue Jones automatic line", true);
    Check("an automatic line flags nobody", (await tickets.OpenMentionsAsync(1, Sue)).Count == 0);
    await tickets.AddNoteAsync(1, job.Id, Olive, "@Gary Gone are you there", false);
    Check("a switched-off person is not flagged", (await tickets.OpenMentionsAsync(1, Gone)).Count == 0);
    await tickets.AddNoteAsync(1, job.Id, Olive, "@Nina Other hello", false);
    Check("another company's person is not flagged", (await All()).All(m => m.Person != "Nina Other"));

    Console.WriteLine();
    Console.WriteLine("== the flag stays until they reply ==");
    await tickets.AddNoteAsync(1, other.Id, Bob, "Printer is fine.", false);
    Check("Bob writing on ANOTHER ticket does not clear it", (await tickets.OpenMentionsAsync(1, Bob)).Count == 1);
    await tickets.AddNoteAsync(1, job.Id, Sue, "I'll ask Bob too", false);
    Check("somebody else replying does not clear Bob's", (await tickets.OpenMentionsAsync(1, Bob)).Count == 1);
    await tickets.AddNoteAsync(1, job.Id, Bob, "It needed a new battery, Sue has it now.", false);
    Check("Bob replying on the ticket clears it", (await tickets.OpenMentionsAsync(1, Bob)).Count == 0);
    Check("...and the flag is kept as answered, not deleted", (await All()).Any(m => m.Person == Bob && m.AnsweredUtc != null));

    await tickets.AddNoteAsync(1, job.Id, Olive, "@Sue Jones is it fixed?", false);
    Check("Sue is flagged", (await tickets.OpenMentionsAsync(1, Sue)).Count == 1);
    await tickets.ActAsync(1, job.Id, Sue, TenantRole.Technician, "wait-customer");
    Check("pressing a stage button is not a reply", (await tickets.OpenMentionsAsync(1, Sue)).Count == 1);
    await tickets.AddWorkDoneAsync(1, job.Id, Sue, TenantRole.Technician, "Replaced battery, tested for an hour.");
    Check("a Work done entry IS a reply", (await tickets.OpenMentionsAsync(1, Sue)).Count == 0);

    await tickets.AddWorkDoneAsync(1, job.Id, Sue, TenantRole.Technician, "@Bob Smith please double-check the charger.");
    Check("a Work done entry can ask somebody too", (await tickets.OpenMentionsAsync(1, Bob)).Count == 1);
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { try { Directory.Delete(dir, true); } catch { } }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
