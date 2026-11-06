// =====================================================================================
// quotecheck - proves step 3 of the Workshop workflow: the company's item list, the lines of a
// quote, and the quote request email (2026-09-24).
//
// Clint: "one technician might start with DDR for memory and another might say memory DDR. it
// must be able to recognise each to prevent duplicates being added." So most of this is about
// the list NOT growing: same words in another order are the same item, near-misses are asked
// about, and genuinely different parts are still allowed.
//
// Then the lines: who may change them and when, the part number remembered, merged items, and
// the draft email - opened as a real MIME message and read back, because a file that "looks
// right" in a text editor and opens as an attachment-less mess in Outlook proves nothing.
//
//   dotnet run -c Debug --project tools/quotecheck/quotecheck.csproj
// =====================================================================================

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Tools;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

var dir = Path.Combine(Path.GetTempPath(), "quotecheck-" + Guid.NewGuid().ToString("N")[..8]);
var dataDir = Path.Combine(dir, "data");
Directory.CreateDirectory(dataDir);
Console.WriteLine("quotecheck - one item list with no duplicates, quote lines, and the quote request email");
Console.WriteLine();

const string Sue = "Sue Tech", Bob = "Bob Tech", Olive = "Olive Office";
const TenantRole T = TenantRole.Technician, OA = TenantRole.Billing, S = TenantRole.Admin;

try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options))
    {
        await HarnessDb.MigrateAsync(db);
        db.Tenants.Add(new Tenant { Id = 1, Slug = "q", CompanyName = "Quote IT", TenantKey = "kq" });
        db.Tenants.Add(new Tenant { Id = 2, Slug = "o", CompanyName = "Other IT", TenantKey = "ko" });
        await db.SaveChangesAsync();
    }
    var factory = new Factory(options);
    var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
    var tickets = new TicketService(factory, photos);
    var quotes = new QuoteService(factory);

    Console.WriteLine("== the words ==");
    Check("\"memory DDR\" and \"DDR memory\" are the same key", QuoteService.MatchKey("memory DDR") == QuoteService.MatchKey("DDR memory"));
    Check("case and punctuation do not matter", QuoteService.MatchKey("DDR-Memory") == QuoteService.MatchKey("ddr memory"));
    Check("\"DDR4 8GB\" and \"DDR4 16GB\" are NOT the same key", QuoteService.MatchKey("DDR4 8GB") != QuoteService.MatchKey("DDR4 16GB"));

    Console.WriteLine();
    Console.WriteLine("== adding to the list ==");
    var (ddr, e1, _) = await quotes.AddItemAsync(1, "DDR4 memory 8GB", Sue);
    Check("an item is added", ddr != null, e1);
    var (again, e2, sim2) = await quotes.AddItemAsync(1, "memory  8GB DDR4", Bob, confirmed: true);
    Check("the same words in another order are NOT added — the existing item comes back", again?.Id == ddr!.Id && sim2.Count == 0, e2);
    var (near, e3, sim3) = await quotes.AddItemAsync(1, "DDR4 memory", Bob);
    Check("a near-miss is not added without asking", near == null && sim3.Any(i => i.Id == ddr.Id), e3);
    var (big, e4, _) = await quotes.AddItemAsync(1, "DDR4 memory 16GB", Bob, confirmed: true);
    Check("...but a genuinely different part is added once confirmed", big != null && big.Id != ddr.Id, e4);
    var (ssd, _, simSsd) = await quotes.AddItemAsync(1, "SSD 512GB NVMe", Sue);
    Check("something unrelated is added without a question", ssd != null && simSsd.Count == 0);
    var (blank, eb, _) = await quotes.AddItemAsync(1, "  - ", Sue);
    Check("a name with no words is refused", blank == null && eb.Length > 0, eb);
    var (otherCo, _, _) = await quotes.AddItemAsync(2, "DDR4 memory 8GB", "Someone");
    Check("another company's list is its own", otherCo != null && otherCo.Id != ddr.Id && (await quotes.ItemsAsync(1)).All(i => i.TenantId == 1));
    // The Items page's Add an item (2026-10-01) passes a part number; quoting never does.
    var (bat, eBat, _) = await quotes.AddItemAsync(2, "Laptop battery 6-cell", "Someone", partNumber: "  BAT-123 ");
    Check("an item added on the Items page keeps its part number, trimmed", bat?.LastPartNumber == "BAT-123", eBat.Length > 0 ? eBat : bat?.LastPartNumber ?? "");
    var (batAgain, _, _) = await quotes.AddItemAsync(2, "battery laptop 6 cell", "Someone", partNumber: "XYZ");
    await using (var db = new PlatformDb(options))
    {
        var kept = await db.CatalogItems.FirstAsync(i => i.Id == bat!.Id);
        Check("...and the same words again with another part number add nothing and change nothing",
            batAgain?.Id == bat!.Id && kept.LastPartNumber == "BAT-123" && await db.CatalogItems.CountAsync(i => i.TenantId == 2) == 2, kept.LastPartNumber);
    }

    Console.WriteLine();
    Console.WriteLine("== predictive text ==");
    var s1 = await quotes.SuggestAsync(1, "DDR");
    Check("\"DDR\" finds both memory items", s1.Count == 2 && s1.All(i => i.Name.Contains("DDR4")), string.Join(", ", s1.Select(i => i.Name)));
    var s2 = await quotes.SuggestAsync(1, "memory ddr");
    Check("\"memory ddr\" finds them too — word order does not matter", s2.Count == 2);
    var s3 = await quotes.SuggestAsync(1, "mem 16");
    Check("the start of each word is enough: \"mem 16\" finds the 16GB", s3.Count == 1 && s3[0].Id == big!.Id, string.Join(", ", s3.Select(i => i.Name)));
    Check("nothing typed suggests nothing", (await quotes.SuggestAsync(1, " ")).Count == 0);
    Check("another company's items are never suggested", (await quotes.SuggestAsync(2, "ddr")).All(i => i.TenantId == 2));

    Console.WriteLine();
    Console.WriteLine("== tidying the list ==");
    Check("a technician cannot rename an item", (await quotes.RenameItemAsync(1, ssd!.Id, "NVMe SSD 512GB", T)).Length > 0);
    Check("the office administrator can", await quotes.RenameItemAsync(1, ssd.Id, "NVMe SSD 512GB M.2", OA) == "");
    Check("renaming onto another item's words is refused — that is a merge",
        (await quotes.RenameItemAsync(1, big.Id, "8GB DDR4 memory", S)).Contains("Merge"));

    Console.WriteLine();
    Console.WriteLine("== quote lines ==");
    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Id == id); }
    var job = await tickets.CreateAsync(1, "Slow laptop", "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.Workshop);
    // The bench in Clint's order (2026-09-27): quote lines are added once it is found repairable.
    async Task ToRepairable(int id)
    {
        await tickets.ActAsync(1, id, Sue, T, "inspection", new StageInput { Details = "Slow." });
        await tickets.ActAsync(1, id, Sue, T, "repairable");
    }
    var (early, eEarly) = await quotes.AddLineAsync(1, job.Id, ddr.Id, "KVR26S19S8/8", 1, null, Sue, T);
    Check("no lines before the job is on the bench", early == null, eEarly);
    await ToRepairable(job.Id);
    var (l1, el1) = await quotes.AddLineAsync(1, job.Id, ddr.Id, "KVR26S19S8/8", 2, null, Sue, T);
    Check("the technician adds a line on the bench", l1 != null && l1.Quantity == 2 && l1.ItemName == "DDR4 memory 8GB", el1);
    Check("a colleague cannot add to it", (await quotes.AddLineAsync(1, job.Id, ssd.Id, "", 1, null, Bob, T)).line == null);
    Check("the office administrator cannot add while it is on the bench", (await quotes.AddLineAsync(1, job.Id, ssd.Id, "", 1, null, Olive, OA)).line == null);

    var (ph, _) = await photos.AddAsync(1, RemoteSupport.Tools.TestImages.Jpeg(), "label.jpg", PhotoKind.Part, Sue, job.Id, "");
    var (l2, el2) = await quotes.AddLineAsync(1, job.Id, ssd.Id, "SA400S37/480G", 1, new[] { ph!.Id }, Sue, T);
    Check("a line can carry a photo of the part", l2 != null && l2.PhotoIds.SequenceEqual(new[] { ph.Id }), el2);
    Check("a part photo does not count towards the three out-photos", await photos.CountAsync(1, job.Id, PhotoKind.After) == 0);
    var other = await tickets.CreateAsync(1, "Other job", "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.Workshop);
    await ToRepairable(other.Id);
    Check("a photo from another ticket is refused", (await quotes.AddLineAsync(1, other.Id, ssd.Id, "", 1, new[] { ph.Id }, Sue, T)).line == null);

    var (l3, _) = await quotes.AddLineAsync(1, other.Id, ddr.Id, "", 1, null, Sue, T);
    Check("the part number is pre-filled from the item's last use", l3?.PartNumber == "KVR26S19S8/8", l3?.PartNumber ?? "");

    await tickets.ActAsync(1, job.Id, Sue, T, "quote-needed");
    Check("at Quote needed the office administrator can change the lines",
        await quotes.RemoveLineAsync(1, l2!.Id, Olive, OA) == "" && (await quotes.LinesAsync(1, job.Id)).Count == 1);
    var (withSerial, _) = await quotes.AddLineAsync(1, job.Id, ssd.Id, "SA400S37/480G", 1, new[] { ph.Id }, Sue, T, " 50026B7784A1C2F3 ");
    Check("...and so can the technician who did the work", withSerial != null);
    // Clint, 2026-09-27: "lets also add a field for serial number".
    Check("a line can carry the part's serial number, trimmed", withSerial?.SerialNumber == "50026B7784A1C2F3", withSerial?.SerialNumber ?? "");
    Check("...and it is in the line's description, after the part number",
        withSerial != null && QuoteService.Describe(withSerial).EndsWith("part number SA400S37/480G, serial number 50026B7784A1C2F3"),
        withSerial == null ? "" : QuoteService.Describe(withSerial));
    var (noSerial, _) = await quotes.AddLineAsync(1, other.Id, ssd.Id, "", 1, null, Sue, T);
    Check("a serial is NOT remembered on the item the way the part number is — it belongs to one part",
        noSerial?.SerialNumber == "" && noSerial.PartNumber == "SA400S37/480G", $"serial '{noSerial?.SerialNumber}', part '{noSerial?.PartNumber}'");
    await tickets.ActAsync(1, job.Id, Olive, OA, "quote-requested", new StageInput { SupplierEmail = "sales@supplier.test" });
    Check("once the quote is requested, the lines are fixed", (await quotes.AddLineAsync(1, job.Id, big.Id, "", 1, null, Olive, OA)).line == null);
    Check("a removed item's photo was freed, so it could go on the item added after it",
        (await quotes.LinesAsync(1, job.Id)).Single(l => l.Id == withSerial!.Id).PhotoIds.SequenceEqual(new[] { ph.Id }));

    Console.WriteLine();
    Console.WriteLine("== several photos of one part (2026-10-06) ==");
    // Clint: "When requesting quotes, uploading photos only allows for one photo."
    async Task<long> PartPhoto(int ticketId, PhotoKind kind = PhotoKind.Part)
    {
        var (p, e) = await photos.AddAsync(1, RemoteSupport.Tools.TestImages.Jpeg(64, Guid.NewGuid().ToByteArray()), "p.jpg", kind, Sue, ticketId, "");
        return p?.Id ?? throw new Exception(e);
    }
    var three = new[] { await PartPhoto(other.Id), await PartPhoto(other.Id), await PartPhoto(other.Id) };
    var (many, eMany) = await quotes.AddLineAsync(1, other.Id, ssd.Id, "", 1, three, Sue, T);
    Check("one item takes three photos at once", many != null && many.PhotoIds.SequenceEqual(three), eMany);
    Check("...and reads back with all three, oldest first",
        (await quotes.LinesAsync(1, other.Id)).Single(l => l.Id == many!.Id).PhotoIds.SequenceEqual(three));
    await using (var db = new PlatformDb(options))
        Check("the story says how many", await db.TicketNotes.AnyAsync(n => n.TicketId == other.Id && n.Body.StartsWith("Added to the quote: NVMe SSD 512GB M.2") && n.Body.EndsWith(", with 3 photos.")));
    Check("a photo already on one item cannot go on another",
        (await quotes.AddLineAsync(1, other.Id, ddr.Id, "", 1, new[] { three[0] }, Sue, T)).error == "That photo is already on another item.");
    var twice = await PartPhoto(other.Id);
    var (once, eOnce) = await quotes.AddLineAsync(1, other.Id, ddr.Id, "", 1, new[] { twice, twice, 0L }, Sue, T);
    Check("the same photo named twice in one go is one photo", once != null && once.PhotoIds.SequenceEqual(new[] { twice }), eOnce);
    var fourth = await PartPhoto(other.Id);
    var fifth = await PartPhoto(other.Id);
    Check("more photos can be added to an item already on the quote",
        await quotes.AddLinePhotosAsync(1, many!.Id, new[] { fourth, fifth }, Sue, T) == ""
        && (await quotes.LinesAsync(1, other.Id)).Single(l => l.Id == many.Id).PhotoIds.Count == 5);
    Check("...not by a colleague who is not on the job", (await quotes.AddLinePhotosAsync(1, many.Id, new[] { await PartPhoto(other.Id) }, Bob, T)).Length > 0);
    var arrived = await PartPhoto(other.Id, PhotoKind.Before);
    Check("...and never a photo of the machine as it arrived", (await quotes.AddLinePhotosAsync(1, many.Id, new[] { arrived }, Sue, T)).Contains("not on this ticket"));
    var jobPhoto = await PartPhoto(job.Id);
    Check("...nor one from another ticket", (await quotes.AddLinePhotosAsync(1, many.Id, new[] { jobPhoto }, Sue, T)).Contains("not on this ticket"));
    var lots = new List<long>();
    for (int i = 0; i < QuoteService.MaxPhotosPerLine - 5 + 1; i++) lots.Add(await PartPhoto(other.Id));
    Check($"no more than {QuoteService.MaxPhotosPerLine} on one item", (await quotes.AddLinePhotosAsync(1, many.Id, lots, Sue, T)).Contains($"up to {QuoteService.MaxPhotosPerLine}"));
    Check("...and a refusal changes nothing", (await quotes.LinesAsync(1, other.Id)).Single(l => l.Id == many.Id).PhotoIds.Count == 5);
    Check("asking for nothing new is said, not done", await quotes.AddLinePhotosAsync(1, many.Id, Array.Empty<long>(), Sue, T) == "Choose a photo first.");
    var (_, wordsMany) = QuoteService.QuoteRequestText(new Tenant { CompanyName = "Quote IT" }, await Get(other.Id), await quotes.LinesAsync(1, other.Id), "Sue");
    Check("the request says how many photos each item has",
        wordsMany.Contains("NVMe SSD 512GB M.2 — part number SA400S37/480G — quantity 1 (5 photos attached)"), wordsMany.Replace("\r\n", " | "));
    Check("each photo is named for its item's number in the email",
        QuoteService.PhotoFileName(2, 3, 5, ".jpg") == "item-2-photo-3.jpg" && QuoteService.PhotoFileName(1, 1, 1, ".png") == "item-1.png");
    Check("removing the item frees all five for another",
        await quotes.RemoveLineAsync(1, many.Id, Sue, T) == ""
        && (await quotes.AddLineAsync(1, other.Id, big.Id, "", 1, new[] { three[0], three[1], three[2], fourth, fifth }, Sue, T)).line?.PhotoIds.Count == 5);

    Console.WriteLine();
    Console.WriteLine("== the one photo a line had before, carried across by the migration ==");
    if (HarnessDb.SqliteUpgradePart("the one photo a line had before"))
    {
        // An old database, at the migration before this one, with rows written as the product wrote
        // them then (§59: by SQL, never through today's model).
        var oldOptions = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "old.db")};Pooling=False").Options;
        await using (var db = new PlatformDb(oldOptions))
        {
            await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db.Database)
                .MigrateAsync("20261006075446_WakeOnLan");
            await Seed.OldRow(db, "QuoteLines", new() { ["Id"] = 50, ["TenantId"] = 1, ["TicketId"] = 7, ["ItemId"] = 1, ["ItemName"] = "Battery", ["Quantity"] = 1, ["PhotoId"] = 900 });
            await Seed.OldRow(db, "QuoteLines", new() { ["Id"] = 51, ["TenantId"] = 1, ["TicketId"] = 7, ["ItemId"] = 1, ["ItemName"] = "Screen", ["Quantity"] = 1, ["PhotoId"] = null });
            await Seed.OldRow(db, "TicketPhotos", new() { ["Id"] = 900, ["TenantId"] = 1, ["TicketId"] = 7, ["Kind"] = 2, ["FileName"] = "a.jpg" });
            await Seed.OldRow(db, "TicketPhotos", new() { ["Id"] = 901, ["TenantId"] = 1, ["TicketId"] = 7, ["Kind"] = 2, ["FileName"] = "b.jpg" });
            await Seed.OldRow(db, "TicketPhotos", new() { ["Id"] = 902, ["TenantId"] = 1, ["TicketId"] = 7, ["Kind"] = 0, ["FileName"] = "c.jpg" });
            await HarnessDb.MigrateAsync(db);
        }
        var oldQuotes = new QuoteService(new Factory(oldOptions));
        var carried = await oldQuotes.LinesAsync(1, 7);
        Check("the line that had a photo still has it", carried.Single(l => l.Id == 50).PhotoIds.SequenceEqual(new[] { 900L }),
            string.Join(",", carried.Single(l => l.Id == 50).PhotoIds));
        Check("the line that had none still has none", carried.Single(l => l.Id == 51).PhotoIds.Count == 0);
        await using (var db = new PlatformDb(oldOptions))
            Check("no other photo was put on a line", await db.TicketPhotos.CountAsync(p => p.QuoteLineId != null) == 1);
    }

    Console.WriteLine();
    Console.WriteLine("== merging ==");
    var (dup, _, _) = await quotes.AddItemAsync(1, "RAM stick 8GB DDR4", Bob, confirmed: true);
    var (lDup, _) = await quotes.AddLineAsync(1, other.Id, dup!.Id, "X-1", 1, null, Sue, T);
    Check("a technician cannot merge", (await quotes.MergeItemAsync(1, dup.Id, ddr.Id, T)).Length > 0);
    Check("a supervisor can", await quotes.MergeItemAsync(1, dup.Id, ddr.Id, S) == "");
    Check("the merged item leaves the dropdown", !(await quotes.ItemsAsync(1)).Any(i => i.Id == dup.Id));
    var lines = await quotes.LinesAsync(1, other.Id);
    Check("its lines now point at the survivor, keeping the name they were written with",
        lines.Any(l => l.Id == lDup!.Id && l.ItemId == ddr.Id && l.ItemName == "RAM stick 8GB DDR4"));
    var (viaOld, _) = await quotes.AddLineAsync(1, other.Id, dup.Id, "", 1, null, Sue, T);
    Check("quoting the merged item quotes the survivor", viaOld?.ItemId == ddr.Id && viaOld.ItemName == "DDR4 memory 8GB");

    Console.WriteLine();
    Console.WriteLine("== the quote request email ==");
    Tenant tenant; await using (var db = new PlatformDb(options)) tenant = await db.Tenants.FirstAsync(t => t.Id == 1);
    var t1 = await Get(job.Id);
    var jobLines = await quotes.LinesAsync(1, job.Id);
    var (subject, body) = QuoteService.QuoteRequestText(tenant, t1, jobLines, "Olive Office");
    Check("the subject names the job", subject.Contains($"#{t1.Number}"), subject);
    Check("the body lists each item with its part number and quantity",
        body.Contains("DDR4 memory 8GB — part number KVR26S19S8/8 — quantity 2") && body.Contains("SA400S37/480G"), body.Replace("\r\n", " | "));
    Check("...with the serial number where there is one, and none where there is not",
        body.Contains("— part number SA400S37/480G — serial number 50026B7784A1C2F3 — quantity 1")
        && body.Split("\r\n").Count(x => x.Contains("serial number")) == 1, body.Replace("\r\n", " | "));
    Check("...says which have a photo, and is signed", body.Contains("(photo attached)") && body.Contains("Quote IT"));

    var photoBytes = await File.ReadAllBytesAsync(photos.FilePath(1, (await photos.GetAsync(1, ph.Id))!.FileName));
    var eml = QuoteService.QuoteRequestEml("sales@supplier.test", subject + " ✓", body, new[] { ("part-1.jpg", "image/jpeg", photoBytes) });
    var text = Encoding.ASCII.GetString(eml);
    Check("the draft is addressed", text.Contains("To: sales@supplier.test\r\n"));
    Check("the draft opens UNSENT, ready to send", text.Contains("X-Unsent: 1\r\n"));
    Check("a subject with non-English characters is encoded, not mangled", text.Contains("Subject: =?utf-8?B?"));
    Check("the file is pure ASCII, as mail must be", eml.All(b => b < 128));
    var parts = text.Split("--rsp-").Length;
    var boundaryLine = text.Split("\r\n").First(l => l.StartsWith("Content-Type: multipart/mixed"));
    var boundary = boundaryLine.Split("boundary=\"")[1].TrimEnd('"');
    var sections = text.Split("--" + boundary);
    Check("two parts: the words and one photo, and a closing boundary", sections.Length == 4 && sections[3].StartsWith("--"), $"{sections.Length - 2} parts");
    string Decode(string section) => Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(section.Split("\r\n\r\n", 2)[1].Split("\r\n"))));
    Check("the words read back exactly", Decode(sections[1]) == body);
    Check("the photo reads back byte for byte", Convert.FromBase64String(string.Concat(sections[2].Split("\r\n\r\n", 2)[1].Split("\r\n"))).SequenceEqual(photoBytes));
    Check("no line is longer than mail allows", text.Split("\r\n").All(l => l.Length <= 998));
    Check("a newline typed into the address cannot add a header",
        !Encoding.ASCII.GetString(QuoteService.QuoteRequestEml("a@b.test\r\nBcc: evil@x.test", "s", "b", Array.Empty<(string, string, byte[])>())).Contains("\r\nBcc:"));

    Console.WriteLine();
    Console.WriteLine("== Gmail and Outlook on the web ==");
    var g = QuoteService.ComposeUrl(MailClients.Gmail, "sales@supplier.test", subject, body);
    Check("Gmail's compose window is addressed and written", g.StartsWith("https://mail.google.com/") && g.Contains("to=sales%40supplier.test") && g.Contains("su=") && g.Contains("body="));
    var o = QuoteService.ComposeUrl(MailClients.OutlookWeb, "sales@supplier.test", subject, body);
    Check("Outlook on the web's too", o.StartsWith("https://outlook.office.com/mail/deeplink/compose?to=sales%40supplier.test"));
    Check("the body survives the trip through the link", Uri.UnescapeDataString(g.Split("&body=")[1]) == body);
    Check("desktop mail gets no link — it gets the file", QuoteService.ComposeUrl(MailClients.Desktop, "a@b.test", "s", "b") == "");
    Check("one supplier's request has no Bcc line at all", !text.Contains("Bcc:"));

    // ---------------------------------------------------------------- 2026-09-29
    // Clint: "a quote is needed for a battery replacement, the quote would ask for the battery
    // replacement for an Asus vivo book 123, with SN123" — one machine per quote, optional; suppliers
    // kept like items and ticked several at once, all in BCC; the company's own To address and a
    // delivery address, with the delivery fee asked for; and the Item box as a dropdown.

    Console.WriteLine();
    Console.WriteLine("== the machine a quote is for ==");
    var booked = await tickets.CreateAsync(1, "Battery", "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.Workshop,
        extra: t => { t.AssetMake = "Asus"; t.AssetModel = "VivoBook 123"; t.AssetSerial = "SN123"; });
    var bm = QuoteService.MachineOf(await Get(booked.Id));
    Check("a workshop job's quote starts with the machine it was booked in with",
        bm == new QuoteMachine("", "Asus", "VivoBook 123", "SN123"), bm.ToString());
    Check("...described the way the email says it", bm.Describe() == "Asus VivoBook 123 — serial number SN123", bm.Describe());
    Check("a job with no booking is for no machine until somebody says so",
        QuoteService.MachineOf(await tickets.CreateAsync(1, "Parts", "", Olive, TicketSource.Manual)).IsEmpty);
    Check("the machine cannot be set before the job is on the bench",
        (await quotes.SetMachineAsync(1, booked.Id, "Laptop", "Asus", "VivoBook 123", "SN123", Sue, T)).Length > 0);
    await ToRepairable(booked.Id);
    Check("a colleague cannot set it", (await quotes.SetMachineAsync(1, booked.Id, "Laptop", "x", "y", "z", Bob, T)).Length > 0);
    Check("the technician on the job can", await quotes.SetMachineAsync(1, booked.Id, "laptop", " Asus ", "VivoBook  123", "SN123", Sue, T) == "");
    var saved = QuoteService.MachineOf(await Get(booked.Id));
    Check("the type is one of the three, however it was typed, and the words are tidied",
        saved == new QuoteMachine("Laptop", "Asus", "VivoBook 123", "SN123"), saved.ToString());
    Check("a type that is not one of the three is left out",
        await quotes.SetMachineAsync(1, booked.Id, "Tablet", "Asus", "VivoBook 123", "SN123", Sue, T) == ""
        && QuoteService.MachineOf(await Get(booked.Id)).Type == "");
    await quotes.SetMachineAsync(1, booked.Id, "Laptop", "Asus", "VivoBook 123", "SN123", Sue, T);
    await using (var db = new PlatformDb(options))
        Check("setting it is in the timeline", await db.TicketNotes.AnyAsync(n => n.TicketId == booked.Id && n.Body == "The quote is for: Laptop — Asus VivoBook 123 — serial number SN123."));
    int notesBefore; await using (var db = new PlatformDb(options)) notesBefore = await db.TicketNotes.CountAsync(n => n.TicketId == booked.Id);
    await quotes.SetMachineAsync(1, booked.Id, "Laptop", "Asus", "VivoBook 123", "SN123", Sue, T);
    await using (var db = new PlatformDb(options))
        Check("saving the same again writes nothing", await db.TicketNotes.CountAsync(n => n.TicketId == booked.Id) == notesBefore);

    var stock = await tickets.CreateAsync(1, "Stock", "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.Workshop,
        extra: t => { t.AssetMake = "Dell"; t.AssetModel = "OptiPlex"; });
    await ToRepairable(stock.Id);
    Check("emptying it says the quote is for no machine", await quotes.SetMachineAsync(1, stock.Id, "", "", "", "", Sue, T) == "");
    Check("...and it stays empty — the booking does not creep back", QuoteService.MachineOf(await Get(stock.Id)).IsEmpty);

    Console.WriteLine();
    Console.WriteLine("== the request's words: the machine and the delivery fee ==");
    var (battery, _, _) = await quotes.AddItemAsync(1, "Battery", Sue);
    await quotes.AddLineAsync(1, booked.Id, battery!.Id, "C31N1339", 1, null, Sue, T);
    await using (var db = new PlatformDb(options)) tenant = await db.Tenants.FirstAsync(t => t.Id == 1);
    var (bs, bb) = QuoteService.QuoteRequestText(tenant, await Get(booked.Id), await quotes.LinesAsync(1, booked.Id), Olive);
    Check("the subject names the machine", bs.EndsWith("— Asus VivoBook 123"), bs);
    Check("the body says which machine, before the items",
        bb.Contains("quote us for the following, for this machine:\r\n\r\nLaptop — Asus VivoBook 123 — serial number SN123\r\n\r\n1. Battery — part number C31N1339"), bb.Replace("\r\n", " | "));
    Check("with no delivery address set, it still asks for the delivery fee, stock and the ETA",
        bb.Contains("\r\nPlease include the delivery fee, whether each item is in stock, and the ETA (how soon each item can be delivered).\r\n"), bb.Replace("\r\n", " | "));
    await quotes.AddLineAsync(1, stock.Id, battery.Id, "", 3, null, Sue, T);
    var (ps, pb) = QuoteService.QuoteRequestText(tenant, await Get(stock.Id), await quotes.LinesAsync(1, stock.Id), Olive);
    Check("a quote for no machine says nothing about one", pb.Contains("quote us for the following:\r\n\r\n1. Battery") && !ps.Contains(" — Dell"), pb.Replace("\r\n", " | "));

    Check("only the people who request quotes set the delivery address",
        (await quotes.SaveQuoteSettingsAsync(1, "accounts@quote.test", "x", T)).Length > 0);
    Check("a To address that is not one is refused", (await quotes.SaveQuoteSettingsAsync(1, "accounts at quote", "", OA)).Length > 0);
    Check("the office administrator sets both",
        await quotes.SaveQuoteSettingsAsync(1, " accounts@quote.test ", "  12 Main Road \r\n\r\n Cape Town  \r\n", OA) == "");
    await using (var db = new PlatformDb(options)) tenant = await db.Tenants.FirstAsync(t => t.Id == 1);
    Check("...kept tidy: trimmed, one line per line", tenant.QuoteToAddress == "accounts@quote.test" && tenant.QuoteDeliveryAddress == "12 Main Road\nCape Town",
        tenant.QuoteDeliveryAddress.Replace("\n", " | "));
    (_, bb) = QuoteService.QuoteRequestText(tenant, await Get(booked.Id), await quotes.LinesAsync(1, booked.Id), Olive);
    Check("the delivery fee is asked for, for delivery to the address",
        bb.Contains("\r\nPlease include the delivery fee, whether each item is in stock, and the ETA (how soon each item can be delivered), for delivery to:\r\n\r\n12 Main Road\r\nCape Town\r\n\r\nOur reference"), bb.Replace("\r\n", " | "));
    Check("...and with no signature it is signed with the person's name and the company's, as before",
        bb.EndsWith("Thank you,\r\nOlive Office\r\nQuote IT\r\n"), bb[^60..].Replace("\r\n", " | "));

    Console.WriteLine();
    Console.WriteLine("== suppliers ==");
    Check("a technician cannot add a supplier", (await quotes.AddSupplierAsync(1, "Rectron", "sales@rectron.test", "", "", Sue, T)).supplier == null);
    var (rectron, re1, _) = await quotes.AddSupplierAsync(1, " Rectron  Cape Town ", "sales@rectron.test", "Jo", "021 555 0101", Olive, OA);
    Check("the office administrator can", rectron != null && rectron.Name == "Rectron Cape Town", re1);
    Check("the same words in another order are not added twice",
        (await quotes.AddSupplierAsync(1, "Cape Town Rectron", "other@rectron.test", "", "", Olive, OA, confirmed: true)).supplier == null);
    var (_, reMail, _) = await quotes.AddSupplierAsync(1, "Somebody Else", "SALES@rectron.test", "", "", Olive, OA, confirmed: true);
    Check("one address is never two suppliers, whatever its capitals", reMail.Contains("Rectron Cape Town"), reMail);
    var (nearS, _, simS) = await quotes.AddSupplierAsync(1, "Rectron", "jhb@rectron.test", "", "", Olive, OA);
    Check("a name that looks like one already there is asked about", nearS == null && simS.Any(s => s.Id == rectron!.Id));
    var (jhb, _, _) = await quotes.AddSupplierAsync(1, "Rectron", "jhb@rectron.test", "", "", Olive, OA, confirmed: true);
    Check("...and added once confirmed", jhb != null);
    Check("an address that is not one is refused", (await quotes.AddSupplierAsync(1, "Tarsus", "tarsus", "", "", Olive, OA)).supplier == null);
    var (tarsus, _, _) = await quotes.AddSupplierAsync(1, "Tarsus", "quotes@tarsus.test", "", "", "Sam Super", S);
    Check("a supervisor can add one too", tarsus != null);
    var (elsewhere, _, _) = await quotes.AddSupplierAsync(2, "Rectron Cape Town", "sales@rectron.test", "", "", "Someone", OA);
    Check("another company's list is its own", elsewhere != null && (await quotes.SuppliersAsync(1)).All(s => s.TenantId == 1));
    Check("renaming onto another supplier's name is refused",
        (await quotes.UpdateSupplierAsync(1, tarsus!.Id, "Town Cape Rectron", "quotes@tarsus.test", "", "", OA)).Length > 0);
    Check("a supplier's details can be changed",
        await quotes.UpdateSupplierAsync(1, tarsus.Id, "Tarsus Distribution", "quotes@tarsus.test", "Mo", "", OA) == ""
        && (await quotes.SuppliersAsync(1)).Any(s => s.Name == "Tarsus Distribution" && s.ContactPerson == "Mo"));
    Check("the list is in order by name", (await quotes.SuppliersAsync(1)).Select(s => s.Name).SequenceEqual(new[] { "Rectron", "Rectron Cape Town", "Tarsus Distribution" }));
    Check("a technician cannot remove one", (await quotes.RemoveSupplierAsync(1, jhb!.Id, T)).Length > 0);
    Check("a removed supplier leaves the list", await quotes.RemoveSupplierAsync(1, jhb.Id, OA) == "" && (await quotes.SuppliersAsync(1)).All(s => s.Id != jhb.Id));

    Console.WriteLine();
    Console.WriteLine("== several suppliers: all in BCC ==");
    var (to1, bcc1) = QuoteService.Addressing(tenant, "sales@rectron.test");
    Check("one supplier goes in To, with no BCC", to1 == "sales@rectron.test" && bcc1 == "");
    var (to2, bcc2) = QuoteService.Addressing(tenant, "sales@rectron.test, quotes@tarsus.test");
    Check("several: the company's own address in To, every supplier in BCC",
        to2 == "accounts@quote.test" && bcc2 == "sales@rectron.test, quotes@tarsus.test", $"to {to2}, bcc {bcc2}");
    Check("with no To address set, the support address stands in",
        QuoteService.Addressing(new Tenant { SupportEmail = "help@quote.test" }, "a@x.test, b@y.test").to == "help@quote.test");
    Check("the same supplier twice is asked once", QuoteService.Addresses("a@x.test; A@X.test, b@y.test").Count == 2);
    var eml2 = Encoding.ASCII.GetString(QuoteService.QuoteRequestEml(to2, bs, bb, Array.Empty<(string, string, byte[])>(), bcc2));
    Check("the draft has them in Bcc and the company in To",
        eml2.Contains("To: accounts@quote.test\r\nBcc: sales@rectron.test, quotes@tarsus.test\r\n"));
    Check("a newline typed into the BCC cannot add a header either",
        !Encoding.ASCII.GetString(QuoteService.QuoteRequestEml("a@b.test", "s", "b", Array.Empty<(string, string, byte[])>(), "c@d.test\r\nX-Evil: 1")).Contains("\r\nX-Evil:"));
    var g2 = QuoteService.ComposeUrl(MailClients.Gmail, to2, bs, bb, bcc2);
    Check("Gmail's compose window carries the BCC", g2.Contains("&to=accounts%40quote.test&bcc=sales%40rectron.test%2C%20quotes%40tarsus.test&su="), g2[..Math.Min(160, g2.Length)]);
    var o2 = QuoteService.ComposeUrl(MailClients.OutlookWeb, to2, bs, bb, bcc2);
    Check("Outlook on the web's too", o2.Contains("?to=accounts%40quote.test&bcc=sales%40rectron.test"));

    await tickets.ActAsync(1, booked.Id, Sue, T, "quote-needed");
    Check("asking a supplier that is not an address is refused",
        (await tickets.ActAsync(1, booked.Id, Olive, OA, "quote-requested", new StageInput { SupplierEmail = "sales@rectron.test, nonsense" })).Length > 0);
    Check("asking nobody is refused",
        await tickets.ActAsync(1, booked.Id, Olive, OA, "quote-requested", new StageInput { SupplierEmail = " , " }) == "Choose at least one supplier to ask.");
    Check("asking two is done", await tickets.ActAsync(1, booked.Id, Olive, OA, "quote-requested",
        new StageInput { SupplierEmail = "sales@rectron.test, quotes@tarsus.test", SupplierNames = "Rectron Cape Town, Tarsus Distribution" }) == "");
    var asked = await Get(booked.Id);
    Check("the ticket keeps both addresses and both names",
        asked.QuoteSupplierEmail == "sales@rectron.test, quotes@tarsus.test" && asked.QuoteSupplierNames == "Rectron Cape Town, Tarsus Distribution");
    await using (var db = new PlatformDb(options))
    {
        var step = await db.TicketNotes.Where(n => n.TicketId == booked.Id && n.MovedTo == "quote-requested").Select(n => n.Body).FirstAsync();
        Check("the timeline names who was asked, and for which machine",
            step.StartsWith("Asked: Rectron Cape Town, Tarsus Distribution\nFor: Laptop — Asus VivoBook 123 — serial number SN123"), step.Replace("\n", " | "));
    }
    Check("the console's facts name them too",
        TicketService.WorkflowFacts(asked).Any(f => f.Label == "Quote asked of" && f.Value == "Rectron Cape Town, Tarsus Distribution"));

    Console.WriteLine();
    Console.WriteLine("== the Item box as a dropdown ==");
    var everything = await quotes.BrowseAsync(1, "");
    Check("nothing typed lists every item on the list, by name",
        everything.Select(i => i.Name).SequenceEqual((await quotes.ItemsAsync(1)).Select(i => i.Name)) && everything.Count == 4,
        string.Join(", ", everything.Select(i => i.Name)));
    Check("...never a merged one", everything.All(i => i.Id != dup.Id));
    Check("...never another company's", everything.All(i => i.TenantId == 1) && (await quotes.BrowseAsync(2, null)).All(i => i.TenantId == 2));
    Check("typing narrows it, word order still not mattering", (await quotes.BrowseAsync(1, "memory ddr")).Count == 2);

    // ---------------------------------------------------------------- 2026-10-06
    // Clint: "an option to upload a signature or multiple signatures that one can select before sending
    // in case there is more than one person in the organization that requests quotes. In the case of
    // multiple signatures, the system should first ask which signature to use" — text and/or an image,
    // the text with "font, size and color selection".
    Console.WriteLine();
    Console.WriteLine("== signatures: what a line may be ==");
    var bad = QuoteSignatures.Clean("Clint\r\nBcc: x", "Comic Sans\"; background:url(x)", 999, "red;background:url(x)", "blink");
    Check("an unknown font, colour or style is put back to the default", bad.Font == "Arial" && bad.Color == "#222222" && bad.Style == "normal", bad.ToString());
    Check("a size off the list goes to the nearest on it", bad.Size == 32 && QuoteSignatures.Clean("x", "Arial", 15, "#000000", "bold").Size is 14 or 16);
    Check("a newline cannot be typed into a line", bad.Text == "ClintBcc: x");
    Check("a colour is kept as six hex digits", QuoteSignatures.Clean("x", "Georgia", 18, "#1A2B3C", "bold-italic") == new QuoteSignatures.Line("x", "Georgia", 18, "#1a2b3c", "bold-italic"));
    var tidy = QuoteSignatures.CleanAll(new[] { new QuoteSignatures.Line("A", "Arial", 14, "#000000", "bold"), new QuoteSignatures.Line("", "Arial", 14, "#000000", ""),
        new QuoteSignatures.Line("B", "Arial", 12, "#000000", ""), new QuoteSignatures.Line("  ", "Arial", 12, "#000000", ""), new QuoteSignatures.Line("", "Arial", 12, "#000000", "") });
    Check("blank lines at the end are dropped, the one in the middle kept as spacing", tidy.Select(l => l.Text).SequenceEqual(new[] { "A", "", "B" }), string.Join("|", tidy.Select(l => l.Text)));

    // Headers only matter for the width; the harness is not drawing them.
    // A whole PNG since the pentest's F7: a signature's image must hold together to be saved.
    byte[] Png(int w) => RemoteSupport.Tools.TestImages.Png(w, 50);
    var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x64, 0x01, 0x2C, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xD9 };
    var gif = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0xF4, 0x01, 0x64, 0x00, 0, 0, 0 };
    Check("PNG, JPEG and GIF are recognised by their bytes", QuoteSignatures.SniffImage(Png(10)) == "image/png" && QuoteSignatures.SniffImage(jpeg) == "image/jpeg" && QuoteSignatures.SniffImage(gif) == "image/gif");
    Check("...WebP and an SVG are not — Outlook draws neither, and an SVG can carry a script",
        QuoteSignatures.SniffImage(Encoding.ASCII.GetBytes("RIFF0000WEBPVP8 ")) == "" && QuoteSignatures.SniffImage(Encoding.ASCII.GetBytes("<svg onload=alert(1)>")) == "");
    Check("each one's own width is read from its header", QuoteSignatures.NaturalWidth(Png(600)) == 600 && QuoteSignatures.NaturalWidth(jpeg) == 300 && QuoteSignatures.NaturalWidth(gif) == 500,
        $"{QuoteSignatures.NaturalWidth(Png(600))}, {QuoteSignatures.NaturalWidth(jpeg)}, {QuoteSignatures.NaturalWidth(gif)}");
    Check("a new image starts at its own width, no wider than 400", QuoteSignatures.StartingWidth(Png(1800)) == 400 && QuoteSignatures.StartingWidth(Png(180)) == 180);

    Console.WriteLine();
    Console.WriteLine("== signatures: kept by the people who request quotes ==");
    var sigs = new QuoteSignatureService(factory);
    var L = (string text, string style) => new QuoteSignatures.Line(text, "Georgia", 18, "#1f4e79", style);
    Check("a technician cannot add one", (await sigs.SaveAsync(1, 0, "Sue", new[] { L("Sue", "bold") }, null, false, 0, false, Sue, T)).error.Length > 0);
    Check("one with no name is refused", (await sigs.SaveAsync(1, 0, "  ", new[] { L("Olive", "bold") }, null, false, 0, false, Olive, OA)).error.Length > 0);
    Check("one with neither text nor an image is refused",
        (await sigs.SaveAsync(1, 0, "Empty", new[] { L("  ", "") }, null, false, 0, false, Olive, OA)).error == "A signature needs some text, an image, or both.");
    Check("an image that is not PNG, JPEG or GIF is refused",
        (await sigs.SaveAsync(1, 0, "Svg", Array.Empty<QuoteSignatures.Line>(), Encoding.ASCII.GetBytes("<svg onload=alert(1)>"), false, 0, false, Olive, OA)).error.Contains("PNG, JPEG or GIF"));
    var huge = Png(300).Concat(new byte[QuoteSignatures.MaxImageBytes]).ToArray();
    Check("an image over 1 MB is refused", (await sigs.SaveAsync(1, 0, "Huge", Array.Empty<QuoteSignatures.Line>(), huge, false, 0, false, Olive, OA)).error.Contains("1 MB"));
    var (oliveSig, se1) = await sigs.SaveAsync(1, 0, "Olive — Accounts",
        new[] { L("Olive Office", "bold"), new QuoteSignatures.Line("Accounts <script>alert(1)</script> & \"quotes\"", "Arial", 13, "#555555", "italic") }, null, false, 0, false, Olive, OA);
    Check("the office administrator adds a text signature", oliveSig != null && se1 == "", se1);
    Check("the same name twice is refused, whatever its case",
        (await sigs.SaveAsync(1, 0, "olive — accounts", new[] { L("x", "") }, null, false, 0, false, Olive, OA)).error.Contains("already a signature"));
    var (bannerSig, se2) = await sigs.SaveAsync(1, 0, "Banner only", Array.Empty<QuoteSignatures.Line>(), Png(1200), false, 0, true, Olive, S);
    Check("a supervisor adds an image-only signature", bannerSig != null && QuoteSignatures.HasImage(bannerSig) && !QuoteSignatures.HasText(bannerSig), se2);
    Check("...drawn at 400 wide, from 1200", bannerSig!.ImageWidth == 400, bannerSig.ImageWidth.ToString());
    Check("...with a random 32-character address", bannerSig.ImageToken.Length == 32 && bannerSig.ImageToken.All(Uri.IsHexDigit));
    var firstToken = bannerSig.ImageToken;
    var served = await sigs.ImageAsync(firstToken);
    Check("its address serves the image, by its own type", served is { } sv && sv.type == "image/png" && sv.bytes.SequenceEqual(Png(1200)));
    Check("a made-up or malformed address serves nothing",
        await sigs.ImageAsync(new string('a', 32)) == null && await sigs.ImageAsync("../../platform.db") == null && await sigs.ImageAsync("") == null);
    var (bannerAgain, _) = await sigs.SaveAsync(1, bannerSig.Id, "Banner only", Array.Empty<QuoteSignatures.Line>(), jpeg, false, 0, true, Olive, OA);
    Check("a new image gets a new address", bannerAgain!.ImageToken != firstToken && bannerAgain.ImageType == "image/jpeg" && bannerAgain.ImageWidth == 300);
    Check("...and the old address serves nothing — the row holds one image", await sigs.ImageAsync(firstToken) == null);
    var (resized, _) = await sigs.SaveAsync(1, bannerSig.Id, "Banner only", Array.Empty<QuoteSignatures.Line>(), null, false, 9999, true, Olive, OA);
    Check("the width can be changed without uploading again, and is held to 800", resized!.ImageWidth == 800 && resized.ImageToken == bannerAgain.ImageToken);
    Check("removing the image from an image-only signature is refused — it would be empty",
        (await sigs.SaveAsync(1, bannerSig.Id, "Banner only", Array.Empty<QuoteSignatures.Line>(), null, true, 0, true, Olive, OA)).error.Length > 0);
    Check("another company's signature cannot be changed",
        (await sigs.SaveAsync(2, oliveSig!.Id, "Mine now", new[] { L("x", "") }, null, false, 0, false, "Someone", OA)).error.Length > 0);
    Check("the list is this company's, by name", (await sigs.ListAsync(1)).Select(s => s.Name).SequenceEqual(new[] { "Banner only", "Olive — Accounts" }) && (await sigs.ListAsync(2)).Count == 0);

    Console.WriteLine();
    Console.WriteLine("== signatures: which one signs the request ==");
    Check("with none on the list, none — signed with the person's name", await sigs.ChooseAsync(2, null) is (null, ""));
    Check("with several, nothing chosen is refused, never guessed",
        (await sigs.ChooseAsync(1, null)).error == "Choose which signature to sign the request with.");
    Check("...and another company's signature is not a choice", (await sigs.ChooseAsync(1, 99999)).sig == null);
    Check("with several, the one chosen", (await sigs.ChooseAsync(1, oliveSig.Id)).sig?.Id == oliveSig.Id);
    var live = await sigs.ListAsync(1);
    Check("the one used last is ticked", QuoteSignatureService.Preselected(live, oliveSig.Id) == oliveSig.Id);
    Check("...or the first, when the last one is gone or never was", QuoteSignatureService.Preselected(live, 4242) == live[0].Id && QuoteSignatureService.Preselected(live, null) == live[0].Id);
    Check("a technician cannot remove one", (await sigs.RemoveAsync(1, bannerSig.Id, T)).Length > 0);
    Check("removing one", await sigs.RemoveAsync(1, bannerSig.Id, OA) == "" && (await sigs.ListAsync(1)).Count == 1);
    Check("with ONE left it is used without asking, whatever was posted", (await sigs.ChooseAsync(1, null)).sig?.Id == oliveSig.Id && (await sigs.ChooseAsync(1, bannerSig.Id)).sig?.Id == oliveSig.Id);
    Check("a removed one is still found by id — a ticket signed with it is downloaded again the same", (await sigs.GetAsync(1, bannerSig.Id))?.Name == "Banner only");
    Check("...its image still served", await sigs.ImageAsync(bannerAgain.ImageToken) != null);
    Check("...but never another company's by id", await sigs.GetAsync(2, oliveSig.Id) == null);

    int oliveId;
    await using (var db = new PlatformDb(options))
    {
        var tech = new Technician { TenantId = 1, DisplayName = Olive, Email = "olive@quote.test", Salt = "x", Hash = "x", Role = OA };
        db.Technicians.Add(tech); await db.SaveChangesAsync(); oliveId = tech.Id;
    }
    await sigs.RememberAsync(1, booked.Id, oliveId, oliveSig.Id);
    await using (var db = new PlatformDb(options))
        Check("after the request the ticket keeps the signature, and the person's next request starts on it",
            (await db.Tickets.FirstAsync(t => t.Id == booked.Id)).QuoteSignatureId == oliveSig.Id
            && (await db.Technicians.FirstAsync(t => t.Id == oliveId)).LastQuoteSignatureId == oliveSig.Id);
    await sigs.RememberAsync(2, booked.Id, oliveId, null);
    await using (var db = new PlatformDb(options))
        Check("...and another company cannot change it", (await db.Tickets.FirstAsync(t => t.Id == booked.Id)).QuoteSignatureId == oliveSig.Id);

    Console.WriteLine();
    Console.WriteLine("== signatures: in the email ==");
    var bt = await Get(booked.Id); var bl = await quotes.LinesAsync(1, booked.Id);
    var (ss, sb2) = QuoteService.QuoteRequestText(tenant, bt, bl, Olive, oliveSig);
    Check("a signature with words is the whole closing — no \"Thank you,\" of ours, no person's name",
        !sb2.Contains("Thank you,") && sb2.EndsWith($"job #{bt.Number}.\r\n\r\nOlive Office\r\nAccounts <script>alert(1)</script> & \"quotes\"\r\n"), sb2[^80..].Replace("\r\n", " | "));
    var (_, sb3) = QuoteService.QuoteRequestText(tenant, bt, bl, Olive, bannerAgain);
    Check("an image-only signature keeps the person's name in the plain words", sb3.EndsWith("Thank you,\r\nOlive Office\r\nQuote IT\r\n"));
    var words = QuoteService.QuoteRequestWords(tenant, bt, bl).words;
    Check("web mail's words stop after \"Our reference\" — the signature is pasted in below",
        words.EndsWith("Our reference: job #" + bt.Number + ".\r\n\r\n") && !words.Contains("Thank you") && !words.Contains(Olive));
    // Clint's own example: "our text may read Kind Regards, with the signature image directly below".
    var kind = new QuoteSignature { Name = "Kind regards", LinesJson = QuoteSignatures.Serialize(new[] { new QuoteSignatures.Line("Kind regards", "Calibri", 14, "#222222", "normal") }),
        Image = Png(300), ImageType = "image/png", ImageToken = new string('c', 32), ImageWidth = 300, ImageFirst = false };
    var kindText = QuoteService.QuoteRequestText(tenant, bt, bl, Olive, kind).body;
    Check("\"Kind regards\" closes the plain words straight after the reference",
        kindText.EndsWith($"job #{bt.Number}.\r\n\r\nKind regards\r\n") && !kindText.Contains("Thank you"), kindText[^50..].Replace("\r\n", " | "));
    var kindHtml = QuoteService.QuoteRequestHtml(tenant, bt, bl, kind);
    Check("...and in the HTML, with the signature image directly below it",
        kindHtml.IndexOf("Our reference") < kindHtml.IndexOf(">Kind regards<") && kindHtml.IndexOf(">Kind regards<") < kindHtml.IndexOf("<img") && !kindHtml.Contains("Thank you"));
    var sigHtml = QuoteSignatures.Html(oliveSig, "https://x.test/q");
    Check("in HTML every line is drawn in its own font, size, colour and style",
        sigHtml.Contains("font-family:Georgia, &#39;Times New Roman&#39;, serif;font-size:18px;line-height:1.3;color:#1f4e79;font-weight:bold\">Olive Office</div>")
        && sigHtml.Contains("font-size:13px;line-height:1.3;color:#555555;font-style:italic"), sigHtml);
    Check("...and what was typed is words, never markup", sigHtml.Contains("Accounts &lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;quotes&quot;") && !sigHtml.Contains("<script>"));
    var banner = QuoteSignatures.Html(resized, "cid:" + QuoteSignatures.ContentId);
    Check("an image is drawn at its width", banner.Contains("<img src=\"cid:signature@rsp\" width=\"800\"") && banner.Contains("width:800px"), banner);
    var both = new QuoteSignature { Name = "Both", LinesJson = QuoteSignatures.Serialize(new[] { L("Name", "bold") }), Image = Png(200), ImageType = "image/png", ImageToken = new string('b', 32), ImageWidth = 200, ImageFirst = true };
    var bothHtml = QuoteSignatures.Html(both, "cid:x");
    Check("the image above the text when asked, below it otherwise",
        bothHtml.IndexOf("<img") < bothHtml.IndexOf(">Name<") && (both.ImageFirst = false) == false && QuoteSignatures.Html(both, "cid:x") is var below && below.IndexOf("<img") > below.IndexOf(">Name<"));

    // The draft, read back by a real MIME parser — what Outlook does with it.
    var html = QuoteService.QuoteRequestHtml(tenant, bt, bl, both);
    var signedEml = QuoteService.QuoteRequestEml("sales@supplier.test", ss, QuoteService.QuoteRequestText(tenant, bt, bl, Olive, both).body,
        new[] { ("item-1-photo-1.jpg", "image/jpeg", photoBytes) }, "", html, ("image/png", both.Image!));
    Check("the signed draft is still pure ASCII, unsent, and addressed",
        signedEml.All(b => b < 128) && Encoding.ASCII.GetString(signedEml).Contains("X-Unsent: 1\r\n") && Encoding.ASCII.GetString(signedEml).Contains("To: sales@supplier.test\r\n"));
    var msg = MimeKit.MimeMessage.Load(new MemoryStream(signedEml));
    Check("it has a plain version and an HTML version", msg.TextBody != null && msg.HtmlBody != null);
    Check("the plain version is the words, signed", msg.TextBody!.Replace("\r\n", "\n") == QuoteService.QuoteRequestText(tenant, bt, bl, Olive, both).body.Replace("\r\n", "\n"));
    Check("the HTML has the request's words and the signature after them",
        msg.HtmlBody!.Contains("ETA (how soon each item can be delivered)") && msg.HtmlBody.Contains("whether each item is in stock") && msg.HtmlBody.IndexOf("Our reference") < msg.HtmlBody.IndexOf(">Name<") && !msg.HtmlBody.Contains("Thank you,"));
    var inlinePart = msg.BodyParts.OfType<MimeKit.MimePart>().FirstOrDefault(p => p.ContentId == QuoteSignatures.ContentId);
    Check("the signature's image is inside the draft, where the HTML points", inlinePart != null && msg.HtmlBody.Contains("cid:" + QuoteSignatures.ContentId));
    using (var ms = new MemoryStream()) { inlinePart?.Content.DecodeTo(ms); Check("...byte for byte", ms.ToArray().SequenceEqual(both.Image!)); }
    Check("...shown in the email, not listed as an attachment", inlinePart?.ContentDisposition?.Disposition == "inline");
    var photoPart = msg.Attachments.OfType<MimeKit.MimePart>().FirstOrDefault(p => p.FileName == "item-1-photo-1.jpg");
    using (var ms = new MemoryStream()) { photoPart?.Content.DecodeTo(ms); Check("the part photo is still attached, byte for byte", photoPart != null && ms.ToArray().SequenceEqual(photoBytes)); }
    Check("no line is longer than mail allows", Encoding.ASCII.GetString(signedEml).Split("\r\n").All(l => l.Length <= 998));
    var textOnlyEml = MimeKit.MimeMessage.Load(new MemoryStream(QuoteService.QuoteRequestEml("a@b.test", ss, sb2, Array.Empty<(string, string, byte[])>(), "",
        QuoteService.QuoteRequestHtml(tenant, bt, bl, oliveSig), null)));
    Check("a text-only signature makes an HTML draft with no image in it",
        textOnlyEml.HtmlBody != null && textOnlyEml.HtmlBody.Contains("Olive Office") && !textOnlyEml.BodyParts.Any(p => p.ContentType.MediaType == "image"));
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
