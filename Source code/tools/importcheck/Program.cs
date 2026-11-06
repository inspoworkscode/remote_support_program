// =====================================================================================
// importcheck - proves the client import (2026-09-28) against the real reader, the real rules,
// the real service and the real migrations, on a throwaway SQLite database.
//
// What it is here to catch, in order of how much it would cost to find later:
//
//   1. Overwriting. A client already on file must only ever have its BLANK fields filled, and
//      the same file imported twice must add nothing the second time. An import that quietly
//      replaces a number somebody corrected by hand is worse than no import.
//
//   2. The wrong people importing. The service refuses a technician itself; the page hiding a
//      button is not the check (§34).
//
//   3. The repairs. Excel eats a phone number's leading zero; an export opened with the wrong
//      encoding turns é into Ã©. Both are put right, and ONLY those — correct text is untouched.
//
//   4. Placeholder addresses. An email on three or more different clients is listed to be left
//      out, and leaving it out leaves it off the clients AND their contacts.
//
// Run it with no arguments for the made-up cases. Give it one or more .xlsx / .csv paths as
// well and it imports each real file into its own throwaway company and says what happened —
// which is how Clint's own list was checked before it went near the live server.
// =====================================================================================

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Tools;

int pass = 0, fail = 0;

void Check(string what, bool ok, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}" + (detail is null ? "" : $"  [{detail}]")); }
}

var dir = Path.Combine(Path.GetTempPath(), "importcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var conn = $"Data Source={Path.Combine(dir, "platform.db")}";

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("importcheck - importing a client list");
Console.WriteLine($"Database : {Path.Combine(dir, "platform.db")}");
Console.WriteLine();

var services = new ServiceCollection();
services.AddDbContextFactory<PlatformDb>(o => o.UseHarnessDb(conn));
services.AddSingleton<ClientImportService>();
var sp = services.BuildServiceProvider();
var dbf = sp.GetRequiredService<IDbContextFactory<PlatformDb>>();
var import = sp.GetRequiredService<ClientImportService>();

async Task<int> NewTenant(string name, string country)
{
    await using var db = await dbf.CreateDbContextAsync();
    var t = new Tenant
    {
        CompanyName = name, Country = country,
        Slug = name.ToLowerInvariant().Replace(' ', '-') + "-" + Guid.NewGuid().ToString("N")[..6],
        TenantKey = Guid.NewGuid().ToString("N"),
    };
    db.Tenants.Add(t);
    await db.SaveChangesAsync();
    return t.Id;
}

Spreadsheet.Cell T(string s) => new(s, false);
Spreadsheet.Sheet SheetOf(params Spreadsheet.Cell[][] rows) { var s = new Spreadsheet.Sheet(); s.Rows.AddRange(rows); return s; }
Spreadsheet.Cell[] R(params string[] cells) => cells.Select(T).ToArray();
var none = new HashSet<string>();

try
{
    await using (var db = await dbf.CreateDbContextAsync()) await HarnessDb.MigrateAsync(db);

    // ---------------------------------------------------------------- the template
    Console.WriteLine("The template");
    var template = Spreadsheet.WriteTemplate(ClientImport.TemplateHeaders, new double[] { 40, 26, 18, 32, 40, 40 },
        new HashSet<int> { 2 }, new[] { "How to", "line two" });
    var (tsheet, terr) = Spreadsheet.Read(template, "Client import template.xlsx");
    Check("the template reads back", tsheet != null, terr);
    Check("its first row is the six headings, in order",
        tsheet != null && tsheet.Rows.Count >= 1 && tsheet.Rows[0].Select(c => c.Text).SequenceEqual(ClientImport.TemplateHeaders),
        tsheet == null ? "" : string.Join("|", tsheet.Rows[0].Select(c => c.Text)));
    Check("only the Clients sheet is read, not the how-to sheet", tsheet?.Rows.Count == 1, $"{tsheet?.Rows.Count} rows");
    string sheet1;
    using (var z = new ZipArchive(new MemoryStream(template)))
    using (var r = new StreamReader(z.GetEntry("xl/worksheets/sheet1.xml")!.Open())) sheet1 = r.ReadToEnd();
    Check("the Phone column is formatted as text, so Excel keeps a leading 0",
        sheet1.Contains("<col min=\"3\" max=\"3\"") && sheet1.Split("<col min=\"3\"")[1].Split("/>")[0].Contains("style=\"2\""));
    var emptyParse = ClientImport.Parse(tsheet!, true);
    Check("an untouched template says it has no clients, rather than importing nothing quietly",
        emptyParse.Error.Contains("no clients"), emptyParse.Error);

    // ---------------------------------------------------------------- reading real files
    Console.WriteLine();
    Console.WriteLine("Reading an .xlsx the way Excel writes one");
    var xlsx = MakeXlsx();
    var (xs, xerr) = Spreadsheet.Read(xlsx, "made.xlsx");
    Check("it reads", xs != null, xerr);
    Check("shared strings come back as text", xs?.Rows[0][0].Text == "Client");
    Check("a number cell is marked as a number", xs?.Rows[2][2] is { Text: "823483295", IsNumber: true },
        xs == null ? "" : $"{xs.Rows[2][2]}");
    Check("a skipped row stays skipped (row 2 is blank)", xs != null && xs.Rows[1].Length == 0);
    Check("a skipped cell keeps the cells after it in their columns", xs?.Rows[2].Length >= 4 && xs.Rows[2][3].Text == "bob@acme.co.za");
    Check("an inline string reads", xs?.Rows[3][0].Text == "Zenith");
    Check("a long number written in E notation reads as its digits", xs?.Rows[3][2].Text == "27824623001", xs?.Rows[3][2].Text);
    Check("the old .xls format is refused with what to do instead",
        Spreadsheet.Read(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 1, 2 }, "old.xls").error.Contains("Save As"));
    Check("a broken workbook is refused, not thrown", Spreadsheet.Read(new byte[] { (byte)'P', (byte)'K', 3, 4, 9, 9, 9 }, "x.xlsx").error.Length > 0);

    Console.WriteLine();
    Console.WriteLine("Reading a CSV");
    var csv = Spreadsheet.Read(Encoding.UTF8.GetBytes("﻿Client,Contact person,Notes\r\n\"Smith, Jones & Co\",\"Sue \"\"Boss\"\" Smith\",\"line one\nline two\"\r\n"), "a.csv").sheet!;
    Check("a UTF-8 BOM is not part of the first heading", csv.Rows[0][0].Text == "Client");
    Check("a quoted comma stays in the field", csv.Rows[1][0].Text == "Smith, Jones & Co");
    Check("doubled quotes are one quote", csv.Rows[1][1].Text == "Sue \"Boss\" Smith");
    Check("a line break inside quotes stays in the field", csv.Rows[1][2].Text == "line one\nline two" && csv.Rows.Count == 2);
    var semi = Spreadsheet.Read(Encoding.UTF8.GetBytes("Client;Phone\nAcme;082 1\n"), "b.csv").sheet!;
    Check("a semicolon CSV (Excel in a comma-decimal country) splits on semicolons", semi.Rows[1].Length == 2 && semi.Rows[1][1].Text == "082 1");
    var ansi = Spreadsheet.Read(new byte[] { (byte)'C', (byte)'l', (byte)'i', (byte)'e', (byte)'n', (byte)'t', 10, (byte)'A', (byte)'n', (byte)'d', (byte)'r', 0xE9, 10 }, "c.csv").sheet!;
    Check("a plain Excel CSV (Windows-1252) reads é as é", ansi.Rows[1][0].Text == "André", ansi.Rows[1][0].Text);

    // ---------------------------------------------------------------- repairs
    Console.WriteLine();
    Console.WriteLine("Repairs, and only repairs");
    Check("Ã‰mile → Émile", ClientImport.RepairMojibake("Ã‰mile") == "Émile");
    Check("Dr AndrÃ© Wiid → Dr André Wiid", ClientImport.RepairMojibake("Dr AndrÃ© Wiid") == "Dr André Wiid");
    Check("MornÃ© → Morné", ClientImport.RepairMojibake("MornÃ© Pienaar") == "Morné Pienaar");
    Check("an en dash garbled to â€“ comes back", ClientImport.RepairMojibake("First Reservations â€“ Trading as") == "First Reservations – Trading as");
    Check("correctly written André is left alone", ClientImport.RepairMojibake("André", out var r1) == "André" && !r1);
    Check("correctly written Émile is left alone", ClientImport.RepairMojibake("Émile") == "Émile");
    Check("SÃO (not a garbling) is left alone", ClientImport.RepairMojibake("SÃO PAULO") == "SÃO PAULO");
    Check("plain text is left alone", ClientImport.RepairMojibake("W J Mitchell & Associates") == "W J Mitchell & Associates");

    Check("a 9-digit NUMBER cell gets its leading 0 back (South Africa)", ClientImport.CleanPhone("823483295", true, true).phone == "0823483295");
    Check("…and says so", ClientImport.CleanPhone("823483295", true, true).fix?.Contains("leading 0") == true);
    Check("82 691 0211 typed without its 0 gets it back", ClientImport.CleanPhone("82 691 0211", false, true).phone == "082 691 0211");
    Check("083 380 377 (a digit short, not a lost 0) is left alone", ClientImport.CleanPhone("083 380 377", false, true) is ("083 380 377", null));
    Check("082 78 00 888 (ten digits, odd spacing) is left alone", ClientImport.CleanPhone("082 78 00 888", false, true) is ("082 78 00 888", null));
    Check("outside South Africa a number cell is left alone", ClientImport.CleanPhone("823483295", true, false).phone == "823483295");
    Check("outside South Africa 82 691 0211 is left alone", ClientImport.CleanPhone("82 691 0211", false, false).phone == "82 691 0211");
    Check("+27 82 123 4567 → 082 123 4567", ClientImport.CleanPhone("+27 82 123 4567", false, true).phone == "082 123 4567");
    Check("27 11 477 2140 → 011 477 2140 (Johannesburg)", ClientImport.CleanPhone("27 11 477 2140", false, true).phone == "011 477 2140");
    Check("+27 (0)82 123 4567 → 082 123 4567", ClientImport.CleanPhone("+27 (0)82 123 4567", false, true).phone == "082 123 4567");
    Check("a phone of only country letters (RSA) is left blank, and says so", ClientImport.CleanPhone("RSA", false, true) is ("", not null));
    Check("Xero's UK 44 7781 136389 → +44 7781 136389", ClientImport.CleanPhone("UK 44 7781 136389", false, true).phone == "+44 7781 136389");
    Check("Xero's CA 1 (416) 574-5543 → +1 (416) 574-5543", ClientImport.CleanPhone("CA 1 (416) 574-5543", false, true).phone == "+1 (416) 574-5543");
    Check("…and the same outside South Africa", ClientImport.CleanPhone("UK 44 7781 136389", false, false).phone == "+44 7781 136389");
    Check("RSA 382 1330 (a local number with no code) is not read as Montenegro's +382",
        !ClientImport.CleanPhone("RSA 382 1330", false, true).phone.StartsWith('+'), ClientImport.CleanPhone("RSA 382 1330", false, true).phone);
    Check("something unrecognised is kept exactly", ClientImport.CleanPhone("1 USA 407 252 7159", false, true) is ("1 USA 407 252 7159", null));
    Check("27824623001 as a number → 0824623001", ClientImport.CleanPhone("27824623001", true, true).phone == "0824623001");
    Check("Xero's RSA 27 082 568 7976 → 082 568 7976", ClientImport.CleanPhone("RSA 27 082 568 7976", false, true).phone == "082 568 7976");
    Check("Xero's RSA 27 82 908 7936 → 082 908 7936", ClientImport.CleanPhone("RSA 27 82 908 7936", false, true).phone == "082 908 7936");
    Check("rsa 27 (0)82 123 4567 → 082 123 4567", ClientImport.CleanPhone("rsa 27 (0)82 123 4567", false, true).phone == "082 123 4567");
    Check("RSA 082 123 4567 → 082 123 4567", ClientImport.CleanPhone("RSA 082 123 4567", false, true).phone == "082 123 4567");
    Check("a name after the number is kept", ClientImport.CleanPhone("082 123 4567 (Butch)", false, true).phone == "082 123 4567 (Butch)");
    Check("an ordinary local number is untouched and not reported", ClientImport.CleanPhone(" 082  123 4567 ", false, true) is ("082 123 4567", null));

    // ---------------------------------------------------------------- headings
    Console.WriteLine();
    Console.WriteLine("Headings");
    var clintHead = ClientImport.Parse(SheetOf(R("Client", "Contact Person", "PhoneNumber", "EmailAddress", "Address"),
        R("Acme", "Bob", "082 111 1111", "bob@acme.co.za", "1 Main Rd")), true);
    Check("Clint's own headings are all understood", clintHead.Error == "" && clintHead.ColumnsIgnored.Count == 0
        && clintHead.Rows[0] is { Client: "Acme", Contact: "Bob", Phone: "082 111 1111", Email: "bob@acme.co.za", Address: "1 Main Rd" },
        string.Join(", ", clintHead.ColumnsIgnored));
    var xeroish = ClientImport.Parse(SheetOf(R("Company", "*ContactName", "Mobile", "Phone")), true);
    Check("ContactName is ignored, because Xero and people mean different things by it", xeroish.ColumnsIgnored.Contains("*ContactName"));
    var twoPhones = ClientImport.Parse(SheetOf(R("Company", "Mobile", "Phone"), R("Acme", "082 111 1111", "044 222 2222")), true);
    Check("a Mobile and a Phone column: Phone holds ONE number, the first", twoPhones.Rows[0].Phone == "082 111 1111", twoPhones.Rows[0].Phone);
    Check("…and the other is kept in Notes, not lost", twoPhones.Rows[0].Notes.Contains("Other number: 044 222 2222"), twoPhones.Rows[0].Notes);
    var noClient = ClientImport.Parse(SheetOf(R("Contact", "Phone"), R("Bob", "1")), true);
    Check("no Client column is refused, naming the headings it did find", noClient.Error.Contains("Client") && noClient.Error.Contains("Contact"), noClient.Error);
    var late = ClientImport.Parse(SheetOf(R(), R("", ""), R("Client"), R("Acme")), true);
    Check("blank rows above the headings are skipped", late.Error == "" && late.Rows.Count == 1 && late.Rows[0].Line == 4, $"{late.Error} {late.Rows.FirstOrDefault()?.Line}");
    var messy = ClientImport.Parse(SheetOf(R("Client", "Contact"), R("  Sonia   Blignaut\n", " Sonia "), R("", "Nobody"), R("", ""), R("Client", "Contact")), true);
    Check("spaces and line breaks in a name are tidied", messy.Rows[0].Client == "Sonia Blignaut" && messy.Rows[0].Contact == "Sonia");
    Check("a row with a contact but no client is an error row, not a client", messy.Rows.Count == 2 && messy.Rows[1].Error.Length > 0);
    Check("blank rows and a repeated heading row are not rows", messy.Rows.Count == 2);
    var badMail = ClientImport.Parse(SheetOf(R("Client", "Email"), R("Acme", "bob at acme")), true);
    Check("an email that is not one stays OUT of Email (nothing could be sent to it)…", badMail.Rows[0].Email == "", badMail.Rows[0].Email);
    Check("…and goes to Notes word for word, with one warning",
        badMail.Rows[0].Notes == "Email as written: bob at acme" && badMail.Rows[0].Warnings.Count == 1, badMail.Rows[0].Notes);

    // ---------------------------------------------------------------- one value per field
    Console.WriteLine();
    Console.WriteLine("Phone and Email hold one value each, that can be dialled or mailed");
    Spreadsheet.Cell[] One(string phone, string email = "", string notes = "") => R("Acme", phone, email, notes);
    ClientImport.Row Row1(string phone, string email = "", string notes = "") =>
        ClientImport.Parse(SheetOf(R("Client", "Phone", "Email", "Notes"), One(phone, email, notes)), true).Rows[0];
    var butch = Row1("082 891 9508 (Butch)");
    Check("082 891 9508 (Butch) → Phone 082 891 9508", butch.Phone == "082 891 9508", butch.Phone);
    Check("…and the whole cell is in Notes, so Butch is not lost", butch.Notes == "Phone as written: 082 891 9508 (Butch)", butch.Notes);
    Check("Sadiya: 083 601 3000 → 083 601 3000", Row1("Sadiya: 083 601 3000").Phone == "083 601 3000");
    Check("065 526 5961 WHATSAPP → 065 526 5961", Row1("065 526 5961 WHATSAPP").Phone == "065 526 5961");
    Check("072 306 2041- Ellis → 072 306 2041", Row1("072 306 2041- Ellis").Phone == "072 306 2041");
    Check("RSA 27 082 576 0066- Ellis → 082 576 0066", Row1("RSA 27 082 576 0066- Ellis").Phone == "082 576 0066", Row1("RSA 27 082 576 0066- Ellis").Phone);
    Check("82 691 0211 (Butch) → 082 691 0211: the 0 is put back after the name comes off",
        Row1("82 691 0211 (Butch)").Phone == "082 691 0211", Row1("82 691 0211 (Butch)").Phone);
    var usa = Row1("1 USA 407 252 7159");
    Check("1 USA 407 252 7159 is kept whole: taking the number alone would lose the country code",
        usa.Phone == "1 USA 407 252 7159" && usa.Notes == "", usa.Phone);
    var words = Row1("WhatsApp only");
    Check("a phone cell with no number in it goes to Notes, Phone blank", words.Phone == "" && words.Notes == "Phone as written: WhatsApp only");
    Check("a plain number goes in untouched, nothing in Notes", Row1("044 382 2871") is { Phone: "044 382 2871", Notes: "" });
    var twoMail = Row1("", "sue@acme.co.za; bob@acme.co.za");
    Check("two addresses in one cell: Email holds ONE, the first", twoMail.Email == "sue@acme.co.za", twoMail.Email);
    Check("…and the other is kept in Notes", twoMail.Notes == "Other email: bob@acme.co.za", twoMail.Notes);
    Check("a, b and a / b and a b split the same way",
        Row1("", "a@x.co, b@y.co").Email == "a@x.co" && Row1("", "a@x.co / b@y.co").Email == "a@x.co" && Row1("", "a@x.co b@y.co").Email == "a@x.co");
    Check("mailto: and angle brackets come off", Row1("", "<mailto:Sue@Acme.co.za>").Email == "Sue@Acme.co.za", Row1("", "<mailto:Sue@Acme.co.za>").Email);
    var ownNotes = Row1("082 891 9508 (Butch)", "", "Pays cash");
    Check("the row's own notes come first, what did not fit follows", ownNotes.Notes == "Pays cash\nPhone as written: 082 891 9508 (Butch)", ownNotes.Notes);

    // ---------------------------------------------------------------- into the database
    Console.WriteLine();
    Console.WriteLine("Into the database");
    var t1 = await NewTenant("Test IT", "South Africa");
    var first = ClientImport.Parse(SheetOf(
        R("Client", "Contact person", "Phone", "Email"),
        R("Acme", "Bob", "082 111 1111", "bob@acme.co.za"),
        R("acme ", "Sue", "082 222 2222", "sue@acme.co.za"),
        R("Zenith", "", "044 333 3333", ""),
        R("", "Orphan", "1", "")), true).Rows;
    var plan1 = await import.PlanAsync(t1, first, none);
    Check("the preview: 2 new clients, 2 contacts, 1 row left out",
        plan1 is { New: 2, ContactsAdded: 2, Errors: 1, AddsToExisting: 0 }, $"new {plan1.New} contacts {plan1.ContactsAdded} errors {plan1.Errors}");
    await using (var db = await dbf.CreateDbContextAsync())
        Check("the preview saved nothing", !await db.Clients.AnyAsync(c => c.TenantId == t1));

    var (done1, e1) = await import.ImportAsync(t1, TenantRole.Billing, first, none);
    Check("the office administrator may import", done1 != null, e1);
    await using (var db = await dbf.CreateDbContextAsync())
    {
        var acme = await db.Clients.SingleOrDefaultAsync(c => c.TenantId == t1 && c.Name == "Acme");
        var people = acme == null ? new List<Contact>() : await db.Contacts.Where(c => c.ClientId == acme.Id).OrderBy(c => c.Name).ToListAsync();
        Check("two rows naming Acme (one as \"acme \") made ONE client", await db.Clients.CountAsync(c => c.TenantId == t1) == 2 && acme != null);
        Check("…with both people as contacts, each with their own phone and email",
            people.Count == 2 && people[0] is { Name: "Bob", Phone: "082 111 1111", Email: "bob@acme.co.za" }
                              && people[1] is { Name: "Sue", Phone: "082 222 2222", Email: "sue@acme.co.za" });
        Check("…and the first row's details on the client itself, which the type-ahead searches",
            acme is { ContactName: "Bob", Phone: "082 111 1111", Email: "bob@acme.co.za" });
        var zen = await db.Clients.SingleAsync(c => c.TenantId == t1 && c.Name == "Zenith");
        Check("a row with no contact person makes a client with no contacts", zen.Phone == "044 333 3333"
            && !await db.Contacts.AnyAsync(c => c.ClientId == zen.Id));
        Check("the row with no client name went nowhere", !await db.Contacts.AnyAsync(c => c.TenantId == t1 && c.Name == "Orphan"));
    }

    var again = await import.ImportAsync(t1, TenantRole.Billing, first, none);
    Check("the same file twice: nothing new the second time",
        again.plan is { New: 0, AddsToExisting: 0, NothingNew: 2, ContactsAdded: 0 },
        $"new {again.plan?.New} adds {again.plan?.AddsToExisting}");
    await using (var db = await dbf.CreateDbContextAsync())
        Check("…and the database agrees", await db.Clients.CountAsync(c => c.TenantId == t1) == 2
                                            && await db.Contacts.CountAsync(c => c.TenantId == t1) == 2);

    // Somebody corrects Zenith by hand; an old export must not undo it.
    await using (var db = await dbf.CreateDbContextAsync())
    {
        var z = await db.Clients.SingleAsync(c => c.TenantId == t1 && c.Name == "Zenith");
        z.Phone = "044 999 9999 (corrected)";
        await db.SaveChangesAsync();
    }
    var older = ClientImport.Parse(SheetOf(R("Client", "Contact", "Phone", "Email", "Address"),
        R("ZENITH", "Tom", "044 333 3333", "info@zenith.co.za", "5 High St"),
        R("Acme", "Bob", "000 000 0000", "", "")), true).Rows;
    var plan2 = await import.PlanAsync(t1, older, none);
    var zp = plan2.Clients.Single(c => c.Name == "Zenith");
    Check("the preview says what an existing client gains, and not what it keeps",
        zp.Outcome == ClientImportService.Outcome.AddsToExisting && zp.Fills.Contains("email") && zp.Fills.Contains("address")
        && !zp.Fills.Contains("phone") && zp.NewContacts.SequenceEqual(new[] { "Tom" }), string.Join(", ", zp.Fills));
    Check("Acme with a different phone for Bob is 'nothing new'",
        plan2.Clients.Single(c => c.Name == "Acme").Outcome == ClientImportService.Outcome.NothingNew);
    await import.ImportAsync(t1, TenantRole.Owner, older, none);
    await using (var db = await dbf.CreateDbContextAsync())
    {
        var z = await db.Clients.SingleAsync(c => c.TenantId == t1 && c.Name == "Zenith");
        Check("a number corrected by hand is NOT overwritten", z.Phone == "044 999 9999 (corrected)", z.Phone);
        Check("blank fields ARE filled", z is { Email: "info@zenith.co.za", Address: "5 High St", ContactName: "Tom" });
        var bob = await db.Contacts.SingleAsync(c => c.TenantId == t1 && c.Name == "Bob");
        Check("an existing contact's phone is not overwritten either", bob.Phone == "082 111 1111", bob.Phone);
        Check("still two clients: ZENITH matched Zenith", await db.Clients.CountAsync(c => c.TenantId == t1) == 2);
    }

    // ---------------------------------------------------------------- who may
    Console.WriteLine();
    Console.WriteLine("Who may import");
    var t2 = await NewTenant("Other IT", "South Africa");
    var one = ClientImport.Parse(SheetOf(R("Client"), R("Acme")), true).Rows;
    var tech = await import.ImportAsync(t2, TenantRole.Technician, one, none);
    Check("a technician is refused BY THE SERVICE", tech.plan == null && tech.error.Length > 0);
    await using (var db = await dbf.CreateDbContextAsync())
        Check("…and nothing was saved", !await db.Clients.AnyAsync(c => c.TenantId == t2));
    foreach (var role in new[] { TenantRole.Owner, TenantRole.OwnerSupervisor, TenantRole.Admin, TenantRole.Billing })
        Check($"{TenantRoles.Label(role)} may", Permissions.CanImportClients(role));
    var other = await import.PlanAsync(t2, one, none);
    Check("another company's Acme is not this company's: it is New here", other.New == 1);

    // ---------------------------------------------------------------- placeholder addresses
    Console.WriteLine();
    Console.WriteLine("An address on many clients");
    var t3 = await NewTenant("Third IT", "South Africa");
    var placeholder = ClientImport.Parse(SheetOf(R("Client", "Contact", "Email"),
        R("A", "Ann", "jason@anywhereit.co.za"), R("B", "Ben", "Jason@AnywhereIT.co.za "), R("C", "", "jason@anywhereit.co.za"),
        R("D", "Di", "shared@two.co.za"), R("E", "Ed", "shared@two.co.za"),
        R("F", "Fay", "office@f.co.za"), R("F", "Gus", "office@f.co.za"), R("F", "Hal", "office@f.co.za")), true).Rows;
    var shared = ClientImport.SharedEmails(placeholder);
    Check("an address on 3 clients is listed, whatever its case", shared.Count == 1 && shared[0] == ("jason@anywhereit.co.za", 3),
        string.Join("; ", shared.Select(s => $"{s.email} {s.clients}")));
    Check("one on only 2 clients is not", !shared.Any(s => s.email == "shared@two.co.za"));
    Check("one company's 3 people sharing its office address is not", !shared.Any(s => s.email == "office@f.co.za"));
    await import.ImportAsync(t3, TenantRole.Admin, placeholder, shared.Select(s => s.email).ToHashSet());
    await using (var db = await dbf.CreateDbContextAsync())
    {
        Check("left out: no client has it", !await db.Clients.AnyAsync(c => c.TenantId == t3 && c.Email.ToLower().Contains("jason@")));
        Check("left out: no contact has it either", !await db.Contacts.AnyAsync(c => c.TenantId == t3 && c.Email.ToLower().Contains("jason@")));
        Check("the other addresses went in", await db.Clients.CountAsync(c => c.TenantId == t3 && c.Email != "") == 3);
    }

    // ---------------------------------------------------------------- the preview's sealed copy
    Console.WriteLine();
    Console.WriteLine("The rows survive the trip through the form");
    var json = JsonSerializer.Serialize(first);
    var back = JsonSerializer.Deserialize<List<ClientImport.Row>>(json)!;
    Check("every row and field comes back", back.Count == first.Count
        && back.Zip(first).All(p => p.First.Client == p.Second.Client && p.First.Contact == p.Second.Contact
                                    && p.First.Phone == p.Second.Phone && p.First.Error == p.Second.Error && p.First.Line == p.Second.Line));

    // ---------------------------------------------------------------- real files
    foreach (var path in args)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 90));
        Console.WriteLine($"REAL FILE: {path}");
        // Shared read, so a file still open in Excel can be checked without closing it.
        byte[] bytes;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        { using var ms = new MemoryStream(); await fs.CopyToAsync(ms); bytes = ms.ToArray(); }
        var (sheet, err) = Spreadsheet.Read(bytes, path);
        if (sheet == null) { Check($"{Path.GetFileName(path)} reads", false, err); continue; }
        var parsed = ClientImport.Parse(sheet, southAfrica: true);
        if (parsed.Error.Length > 0) { Check($"{Path.GetFileName(path)} parses", false, parsed.Error); continue; }
        Console.WriteLine($"Columns read   : {string.Join(", ", parsed.ColumnsUsed)}");
        Console.WriteLine($"Columns ignored: {(parsed.ColumnsIgnored.Count == 0 ? "none" : string.Join(", ", parsed.ColumnsIgnored))}");
        var sh = ClientImport.SharedEmails(parsed.Rows);
        Console.WriteLine($"Shared emails  : {(sh.Count == 0 ? "none" : string.Join("; ", sh.Select(s => $"{s.email} on {s.clients} clients")))}");

        var tid = await NewTenant("Real " + Path.GetFileNameWithoutExtension(path), "South Africa");
        var leave = sh.Select(s => s.email).ToHashSet();
        var (p, perr) = await import.ImportAsync(tid, TenantRole.Owner, parsed.Rows, leave);
        if (p == null) { Check("imports", false, perr); continue; }
        Console.WriteLine($"Rows {parsed.Rows.Count} → new clients {p.New}, contacts {p.ContactsAdded}, left out {p.Errors}");

        foreach (var c in p.Clients.Where(c => c.Outcome == ClientImportService.Outcome.Error))
            Console.WriteLine($"  LEFT OUT  row {string.Join(",", c.Lines)}: {c.Name} — {c.Error}");
        foreach (var c in p.Clients.Where(c => c.Warnings.Count > 0))
            foreach (var w in c.Warnings) Console.WriteLine($"  WARNING   {c.Name}: {w}");
        var fixes = p.Clients.SelectMany(c => c.Fixes.Select(f => $"{c.Name}: {f}")).ToList();
        Console.WriteLine($"  Repairs: {fixes.Count}");
        foreach (var f in fixes) Console.WriteLine($"  REPAIRED  {f}");
        var multi = p.Clients.Where(c => c.Lines.Count > 1).ToList();
        foreach (var c in multi) Console.WriteLine($"  MERGED    rows {string.Join(",", c.Lines)} → {c.Name} ({string.Join(", ", c.NewContacts)})");

        await using (var db = await dbf.CreateDbContextAsync())
        {
            var inDb = await db.Clients.CountAsync(c => c.TenantId == tid);
            Check($"{Path.GetFileName(path)}: every client in the preview is in the database", inDb == p.New, $"{inDb} vs {p.New}");
            foreach (var s in leave)
                Check($"{Path.GetFileName(path)}: nobody has {s}",
                    !await db.Clients.AnyAsync(c => c.TenantId == tid && c.Email.ToLower().Contains(s))
                    && !await db.Contacts.AnyAsync(c => c.TenantId == tid && c.Email.ToLower().Contains(s)));
            Check($"{Path.GetFileName(path)}: no garbled letters left", !await db.Clients.AnyAsync(c => c.TenantId == tid
                && (c.Name.Contains("Ã") || c.Name.Contains("â€") || c.ContactName.Contains("Ã") || c.Address.Contains("Ã"))));
            var odd = (await db.Clients.Where(c => c.TenantId == tid && c.Phone != "").Select(c => new { c.Name, c.Phone }).ToListAsync())
                .Where(c => !System.Text.RegularExpressions.Regex.IsMatch(c.Phone, @"^0\d{2} ?\d{3} ?\d{4}$")).ToList();
            Console.WriteLine($"  Phones not in the 0xx xxx xxxx shape: {odd.Count}");
            foreach (var c in odd) Console.WriteLine($"    ODD PHONE  {c.Name}: {c.Phone}");
            Console.WriteLine("  First 8 as stored:");
            foreach (var c in await db.Clients.Where(c => c.TenantId == tid).OrderBy(c => c.Id).Take(8).ToListAsync())
                Console.WriteLine($"    {c.Name} | {c.ContactName} | {c.Phone} | {c.Email} | {c.Address.Replace("\n", " / ")}");
        }
        var twice = await import.ImportAsync(tid, TenantRole.Owner, parsed.Rows, leave);
        Check($"{Path.GetFileName(path)}: imported twice, nothing new the second time",
            twice.plan is { New: 0, AddsToExisting: 0, ContactsAdded: 0 }, $"new {twice.plan?.New} adds {twice.plan?.AddsToExisting}");
    }
}
finally
{
    SqliteCleanup();
}

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;

void SqliteCleanup()
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(dir, true); } catch { /* a locked temp file is not a failure */ }
}

// An .xlsx the way Excel writes one: shared strings, a number cell, a blank row left out of the file,
// a cell left out of a row, an inline string, and a long number in E notation.
static byte[] MakeXlsx()
{
    const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    using var ms = new MemoryStream();
    using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
    {
        void Put(string p, string x) { using var w = new StreamWriter(z.CreateEntry(p).Open()); w.Write(x); }
        Put("xl/workbook.xml", $"<workbook xmlns=\"{main}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Contacts\" sheetId=\"7\" r:id=\"rId3\"/></sheets></workbook>");
        Put("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId3\" Type=\"x\" Target=\"worksheets/sheet9.xml\"/></Relationships>");
        Put("xl/sharedStrings.xml", $"<sst xmlns=\"{main}\"><si><t>Client</t></si><si><t>Contact Person</t></si><si><t>PhoneNumber</t></si><si><t>EmailAddress</t></si>" +
            "<si><r><t>Ac</t></r><r><t>me</t></r></si><si><t>bob@acme.co.za</t></si></sst>");
        Put("xl/worksheets/sheet9.xml", $"<worksheet xmlns=\"{main}\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c><c r=\"D1\" t=\"s\"><v>3</v></c></row>" +
            "<row r=\"3\"><c r=\"A3\" t=\"s\"><v>4</v></c><c r=\"C3\" s=\"2\"><v>823483295</v></c><c r=\"D3\" t=\"s\"><v>5</v></c></row>" +
            "<row r=\"4\"><c r=\"A4\" t=\"inlineStr\"><is><t>Zenith</t></is></c><c r=\"C4\"><v>2.7824623001E10</v></c></row>" +
            "</sheetData></worksheet>");
    }
    return ms.ToArray();
}
