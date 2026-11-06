// =====================================================================================
// photogate - proves stage 3.6.5: a workshop job cannot be booked in without three photos
// of the machine as it arrived, or closed without three of it finished.
//
// The gates are asked of TicketPhotoService and TicketService, never of a page. A counter
// on a form is a courtesy to the person filling it in; the rule is whatever runs when the
// form is posted anyway - from a stale tab, from the console, or by hand. If the rule is
// only in the markup then the photos prove nothing, and the photos are the entire point:
// they are what settles an argument about a scratch six weeks later.
//
// It also checks the things that are easy to get wrong and quiet when you do:
//
//   the file's NAME is not what decides whether it is an image
//   the gates do not touch any category but Workshop
//   a technician can still say "in progress" on a job with no photos at all
//   two people booking machines in at one counter cannot take each other's photographs
//
//   dotnet run -c Release --project tools/photogate/photogate.csproj
// =====================================================================================

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

// A real, minimal PNG. The service sniffs BYTES, so a harness that fed it made-up data would
// prove nothing about what the server actually accepts.
byte[] Png() => RemoteSupport.Tools.TestImages.Png();
byte[] Jpeg() => RemoteSupport.Tools.TestImages.Jpeg(200);

var dir = Path.Combine(Path.GetTempPath(), "photogate-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var dataDir = Path.Combine(dir, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "platform.db");

Console.WriteLine("photogate - stage 3.6.5, three photos in and three out");
Console.WriteLine();

try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={dbPath}").Options;
    await using (var seed = new PlatformDb(options))
    {
        await HarnessDb.MigrateAsync(seed);
        seed.Tenants.Add(new Tenant { Slug = "check", CompanyName = "Check IT", TenantKey = Guid.NewGuid().ToString("N") });
        await seed.SaveChangesAsync();
    }

    var factory = new Factory(options);
    var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
    var tickets = new TicketService(factory, photos);

    const string Sue = "Sue Jones", Bob = "Bob Smith";
    int need = TicketPhotoService.Required;

    Console.WriteLine("== what counts as a photo ==");

    Check("a JPEG is recognised from its bytes", TicketPhotoService.SniffType(Jpeg()) == "image/jpeg");
    Check("a PNG is recognised from its bytes", TicketPhotoService.SniffType(Png()) == "image/png");
    Check("something that is not an image is not one", TicketPhotoService.SniffType(new byte[] { 1, 2, 3, 4, 5 }) == "");

    // The name is a claim, not evidence. This is the check that stops an upload folder the server
    // serves back to browsers becoming a way to host whatever somebody likes.
    var (bad, badErr) = await photos.AddAsync(1, System.Text.Encoding.ASCII.GetBytes("<script>alert(1)</script>"),
        "totally-a-photo.jpg", PhotoKind.Before, Sue, null, "stage-x");
    Check("a text file called .jpg is REFUSED", bad == null && badErr.Length > 0, badErr);

    // The pentest's F7 (2026-10-23): the first bytes are not a picture either. Their probe was a PNG signature and junk.
    var pngJunk = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Enumerable.Repeat((byte)'A', 100)).ToArray();
    var (junk, junkErr) = await photos.AddAsync(1, pngJunk, "x.png", PhotoKind.Before, Sue, null, "stage-x");
    Check("a PNG signature followed by junk is REFUSED", junk == null && junkErr.Length > 0, junkErr);
    var jpegHead = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.Concat(new byte[200]).ToArray();
    Check("so is a JPEG's first four bytes and zeros", TicketPhotoService.WholeImageType(jpegHead) == ""
        && TicketPhotoService.WholeImageType(Jpeg()) == "image/jpeg" && TicketPhotoService.WholeImageType(Png()) == "image/png");
    var webp = "RIFF"u8.ToArray().Concat(BitConverter.GetBytes(20)).Concat("WEBPVP8 "u8.ToArray()).Concat(BitConverter.GetBytes(8)).Concat(new byte[8]).ToArray();
    var heic = new byte[] { 0, 0, 0, 16 }.Concat("ftypheic"u8.ToArray()).Concat(new byte[4]).Concat(new byte[] { 0, 0, 0, 8 }).Concat("meta"u8.ToArray()).ToArray();
    Check("a WebP and a HEIC whose boxes hold together are pictures; cut short, they are not",
        TicketPhotoService.WholeImageType(webp) == "image/webp" && TicketPhotoService.WholeImageType(heic) == "image/heic"
        && TicketPhotoService.WholeImageType(webp[..20]) == "" && TicketPhotoService.WholeImageType(heic[..18]) == "");

    var (empty, emptyErr) = await photos.AddAsync(1, Array.Empty<byte>(), "nothing.jpg", PhotoKind.Before, Sue, null, "stage-x");
    Check("an empty file is refused", empty == null && emptyErr.Length > 0);

    var (huge, hugeErr) = await photos.AddAsync(1, Jpeg().Concat(new byte[TicketPhotoService.MaxBytes]).ToArray(),
        "enormous.jpg", PhotoKind.Before, Sue, null, "stage-x");
    Check("a file over the size limit is refused", huge == null && hugeErr.Length > 0, hugeErr);

    var (nowhere, nowhereErr) = await photos.AddAsync(1, Jpeg(), "loose.jpg", PhotoKind.Before, Sue, null, "");
    Check("a photo with no ticket and no booking form is refused", nowhere == null && nowhereErr.Length > 0);

    Console.WriteLine();
    Console.WriteLine("== booking in ==");

    const string stage = "booking-1";
    Check($"a workshop booking with no photos is refused",
        (await photos.CheckBookInAsync(1, TicketCategory.Workshop, stage, Sue)).Length > 0);

    for (int i = 0; i < need - 1; i++)
        await photos.AddAsync(1, Jpeg(), $"arrived-{i}.jpg", PhotoKind.Before, Sue, null, stage);

    var twoPhotos = await photos.CheckBookInAsync(1, TicketCategory.Workshop, stage, Sue);
    Check($"a workshop booking with {need - 1} photos is refused", twoPhotos.Length > 0, twoPhotos);
    Check("...and the refusal says how many there are", twoPhotos.Contains("added"), twoPhotos);

    await photos.AddAsync(1, Jpeg(), "arrived-last.jpg", PhotoKind.Before, Sue, null, stage);
    Check($"a workshop booking with {need} photos is allowed",
        (await photos.CheckBookInAsync(1, TicketCategory.Workshop, stage, Sue)).Length == 0);

    // Every other category is untouched. A remote support ticket has no machine on a bench.
    foreach (var c in new[] { TicketCategory.RemoteSupport, TicketCategory.WalkIn, TicketCategory.CallOut, TicketCategory.General })
        Check($"a {TicketService.CategoryName(c).ToLower()} ticket needs no photos",
            (await photos.CheckBookInAsync(1, c, "nothing-staged", Sue)).Length == 0);

    // Two counters at once.
    await photos.AddAsync(1, Jpeg(), "bobs.jpg", PhotoKind.Before, Bob, null, "booking-2");
    Check("one person's staged photos are not visible to another",
        (await photos.StagedAsync(1, stage, Bob)).Count == 0);
    Check("...and cannot be counted towards their booking",
        (await photos.CheckBookInAsync(1, TicketCategory.Workshop, stage, Bob)).Length > 0);

    Console.WriteLine();
    Console.WriteLine("== the photos follow the ticket ==");

    var workshop = await tickets.CreateAsync(1, "Laptop will not charge", "", Sue, TicketSource.Manual,
        assignedTo: Sue, category: TicketCategory.Workshop);
    int attached = await photos.AttachStagedAsync(1, workshop.Id, stage, Sue);
    Check($"all {need} staged photos attach to the new ticket", attached == need, $"{attached}");
    Check("they are on the ticket as 'before'", await photos.CountAsync(1, workshop.Id, PhotoKind.Before) == need);
    Check("the booking form is left with nothing staged", (await photos.StagedAsync(1, stage, Sue)).Count == 0);
    Check("the OTHER person's staged photo is untouched", (await photos.StagedAsync(1, "booking-2", Bob)).Count == 1);

    Console.WriteLine();
    Console.WriteLine("== closing ==");

    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Id == id); }

    // A technician can get on with the job with no after photos at all. Since 2026-09-24 a
    // workshop job moves by its STAGES (tools/workflowcheck proves the route); what this harness
    // keeps asking is that the photos gate cannot be walked around by any path.
    Check("a workshop job with no after photos can still be worked on",
        await tickets.ActAsync(1, workshop.Id, Sue, TenantRole.Technician, "inspection", new StageInput { Details = "Charging port loose." }) == ""
        && (await Get(workshop.Id)).Status == TicketStatus.InProgress);
    Check("...and put on hold waiting for the customer",
        await tickets.ActAsync(1, workshop.Id, Sue, TenantRole.Technician, "wait-customer") == ""
        && (await Get(workshop.Id)).Status == TicketStatus.OnHold);
    await tickets.ActAsync(1, workshop.Id, Sue, TenantRole.Technician, "resume");

    await tickets.UpdateAsync(1, workshop.Id, Sue, TenantRole.Technician, TicketStatus.Resolved, null, null, null);
    Check("a workshop job with no after photos CANNOT be resolved",
        (await Get(workshop.Id)).Status != TicketStatus.Resolved, (await Get(workshop.Id)).Status.ToString());
    Check("...and says why", tickets.LastBlockedReason.Length > 0, tickets.LastBlockedReason);

    await tickets.UpdateAsync(1, workshop.Id, Sue, TenantRole.Technician, TicketStatus.Closed, null, null, null);
    Check("a workshop job with no after photos CANNOT be closed",
        (await Get(workshop.Id)).Status != TicketStatus.Closed);

    // Even the owner. This is a rule about evidence, not about rank.
    await tickets.UpdateAsync(1, workshop.Id, "The Owner", TenantRole.Admin, TicketStatus.Closed, null, null, null);
    Check("not even the owner can close it without them", (await Get(workshop.Id)).Status != TicketStatus.Closed);

    // The out-photos are taken at COLLECTION now, in front of the customer, and closing needs the
    // machine collected. Take the job through the bench to billing, where it may be collected.
    await tickets.ActAsync(1, workshop.Id, Sue, TenantRole.Technician, "repairable");
    await tickets.ActAsync(1, workshop.Id, Sue, TenantRole.Technician, "solved-invoice", new StageInput { Details = "Replaced the charging port.", Minutes = 45, Amount = 650 });
    await tickets.ActAsync(1, workshop.Id, "Olive Office", TenantRole.Billing, "client-invoiced", new StageInput { Amount = 650, Reference = "INV-1" });
    await tickets.ActAsync(1, workshop.Id, "Olive Office", TenantRole.Billing, "payment-received");
    Check("paid but not collected is NOT closed", (await Get(workshop.Id)).Status != TicketStatus.Closed, (await Get(workshop.Id)).Stage);

    for (int i = 0; i < need - 1; i++)
        await photos.AddAsync(1, Jpeg(), $"finished-{i}.jpg", PhotoKind.After, Sue, workshop.Id, "");
    var tooFew = await tickets.ActAsync(1, workshop.Id, "Olive Office", TenantRole.Billing, TicketService.CollectedKey);
    Check($"{need - 1} after photos are still not enough to collect it", tooFew.Length > 0, tooFew);

    await photos.AddAsync(1, Png(), "finished-last.png", PhotoKind.After, Sue, workshop.Id, "");
    Check($"with {need} after photos it is collected",
        await tickets.ActAsync(1, workshop.Id, "Olive Office", TenantRole.Billing, TicketService.CollectedKey) == "");
    var closeWhy = await tickets.ActAsync(1, workshop.Id, "Olive Office", TenantRole.Billing, "close");
    Check("...and then it closes", closeWhy == "" && (await Get(workshop.Id)).Status == TicketStatus.Closed, closeWhy);

    // A note posted alongside a refused close must survive. Losing what somebody just typed is a
    // worse failure than the refusal itself.
    var other = await tickets.CreateAsync(1, "Second bench job", "", Sue, TicketSource.Manual,
        assignedTo: Sue, category: TicketCategory.Workshop);
    await tickets.UpdateAsync(1, other.Id, Sue, TenantRole.Technician, TicketStatus.Closed, TicketPriority.High, null, null);
    Check("a refused close still applies the rest of the update",
        (await Get(other.Id)).Priority == TicketPriority.High && (await Get(other.Id)).Status != TicketStatus.Closed);

    // Non-workshop is untouched at the closing end too.
    // Since 2026-09-27 it closes by its own route, not the status — and still with no photos.
    var remote = await tickets.CreateAsync(1, "Remote job", "", Sue, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.RemoteSupport);
    await tickets.ActAsync(1, remote.Id, Sue, TenantRole.Technician, "remote-sla");
    await tickets.ActAsync(1, remote.Id, "Olive Office", TenantRole.Billing, "close-dni");
    Check("a remote support ticket closes with no photos at all", (await Get(remote.Id)).Status == TicketStatus.Closed, (await Get(remote.Id)).Stage);

    Console.WriteLine();
    Console.WriteLine("== housekeeping ==");

    await using (var db = new PlatformDb(options))
    {
        // Age the surviving staged photo past the sweep's cut-off.
        foreach (var p in await db.TicketPhotos.Where(p => p.TicketId == null).ToListAsync())
            p.UploadedUtc = DateTime.UtcNow.AddDays(-2);
        await db.SaveChangesAsync();
    }
    var swept = await photos.SweepStagedAsync();
    Check("abandoned booking photos are swept", swept == 1, $"{swept}");
    await using (var db = new PlatformDb(options))
        Check("...and a real ticket's photos are left alone",
            await db.TicketPhotos.CountAsync(p => p.TicketId == workshop.Id) == need * 2);

    var onTicket = await photos.ForTicketAsync(1, workshop.Id);
    Check("every photo on the ticket has a file on disk",
        onTicket.All(p => File.Exists(photos.FilePath(1, p.FileName))), $"{onTicket.Count} photos");

    var first = onTicket[0];
    await photos.DeleteAsync(1, first.Id);
    Check("deleting a photo removes its file too", !File.Exists(photos.FilePath(1, first.FileName)));
    Check("...and its row", await photos.GetAsync(1, first.Id) == null);
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

/// <summary>The context factory the services want, over one throwaway SQLite file.</summary>
file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
