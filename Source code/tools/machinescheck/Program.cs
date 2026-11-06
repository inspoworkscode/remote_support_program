// =====================================================================================
// machinescheck - proves Add another machine (Clint, 2026-09-30): a client who brings in more than
// one machine at the same time.
//
//   "at the beginning of the workflow, there must be an option to add machine with it's fault
//   description and ask for the same details as the original."
//
// His answers: one ticket per machine (booked together and linked); paying together or separately,
// leaving together or separately, "and there's always a chance of one or two of them being
// abandoned" — so nothing waits for its siblings. Every rule is asked of TicketService, never a
// page: who may add one, when, what it must have (a fault and its OWN three photos), what the new
// ticket inherits, that the link is shared by the whole booking as equals, and that each machine
// then goes its own way.
//
//   dotnet run -c Debug --project tools/machinescheck/machinescheck.csproj
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

var dir = Path.Combine(Path.GetTempPath(), "machinescheck-" + Guid.NewGuid().ToString("N")[..8]);
var dataDir = Path.Combine(dir, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "platform.db");
Console.WriteLine("machinescheck - several machines booked in at once, through the service");
Console.WriteLine();

const string Sue = "Sue Tech", Bob = "Bob Tech", Olive = "Olive Office";
const TenantRole T = TenantRole.Technician, OA = TenantRole.Billing;
const string Add = TicketService.AddMachineKey;

try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={dbPath};Pooling=False").Options;
    int clientId;
    await using (var db = new PlatformDb(options))
    {
        await HarnessDb.MigrateAsync(db);
        await Seed.OldTenant(db, 1, "m", "Machines IT", "km");
        var c = new Client { TenantId = 1, Name = "Acme Ltd", ContactName = "Jane" };
        db.Clients.Add(c);
        await db.SaveChangesAsync();
        clientId = c.Id;
    }
    var factory = new Factory(options);
    var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
    var tickets = new TicketService(factory, photos);
    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Id == id); }
    string[] Keys(Ticket t, TenantRole r, string me) => TicketService.ActionsFor(t, r, me).Select(a => a.Key).ToArray();
    async Task<string> Stage(string by, int n)
    {
        var id = Guid.NewGuid().ToString("N");
        for (int i = 0; i < n; i++) await photos.AddAsync(1, Jpeg(), $"in-{i}.jpg", PhotoKind.Before, by, null, id);
        return id;
    }
    async Task<Ticket?> Newest() { await using var db = new PlatformDb(options); return await db.Tickets.OrderByDescending(t => t.Id).FirstOrDefaultAsync(); }

    var first = await tickets.CreateAsync(1, "Dell laptop will not charge", "Charger light off.", Olive, TicketSource.Manual,
        contactName: "Jane", priority: TicketPriority.High, assignedTo: $"{Sue}, {Bob}", category: TicketCategory.Workshop, clientId: clientId,
        extra: x => { x.AssetMake = "Dell"; x.AssetModel = "Latitude"; });

    Console.WriteLine("== who may add one, and when ==");
    Check("at Booked in the office is offered Add another machine, ahead of Abandoned",
        Keys(first, OA, Olive).SequenceEqual(new[] { Add, "abandon" }), string.Join(",", Keys(first, OA, Olive)));
    Check("...and so are the technicians on the job", Keys(first, T, Sue).Contains(Add));
    Check("...but not a technician who is not on it", !Keys(first, T, "Tom Tech").Contains(Add));
    Check("it asks same job or separate FIRST, then the fault, then the booking's questions",
        TicketService.AddMachineFields.SequenceEqual(new[] { StageField.SameJob, StageField.Details, StageField.AssetMake, StageField.AssetModel,
            StageField.AssetSerial, StageField.Accessories, StageField.DevicePassword }));
    Check("the step form is a form in both UIs (it has fields)",
        TicketService.ActionsFor(first, OA, Olive).First(a => a.Key == Add).Fields.Length == 7);

    Console.WriteLine();
    Console.WriteLine("== what it must have ==");
    var three = await Stage(Olive, 3);
    var noFault = await tickets.ActAsync(1, first.Id, Olive, OA, Add, new StageInput { PhotoStageId = three });
    Check("without the fault it is REFUSED", noFault.Contains("wrong"), noFault);
    var noPics = await tickets.ActAsync(1, first.Id, Olive, OA, Add, new StageInput { Details = "Screen cracked." });
    Check("without photos it is REFUSED, as any booking is", noPics.Contains("photos"), noPics);
    var two = await Stage(Olive, 2);
    var twoPics = await tickets.ActAsync(1, first.Id, Olive, OA, Add, new StageInput { Details = "Screen cracked.", PhotoStageId = two });
    Check("...and with only two", twoPics.Contains("photos"), twoPics);
    var bobs = await Stage(Bob, 3);
    var notMine = await tickets.ActAsync(1, first.Id, Olive, OA, Add, new StageInput { Details = "Screen cracked.", PhotoStageId = bobs });
    Check("photos staged by somebody else do not count", notMine.Contains("photos"), notMine);
    int before;
    await using (var db = new PlatformDb(options)) before = await db.Tickets.CountAsync();
    Check("a refusal made no ticket", before == 1);

    Console.WriteLine();
    Console.WriteLine("== a second machine ==");
    Check("with a fault and three of its own photos it is booked in",
        await tickets.ActAsync(1, first.Id, Olive, OA, Add, new StageInput
        {
            Details = "Screen cracked across the middle.\nStill boots.", AssetMake = " HP ", AssetModel = "ProBook 450",
            AssetSerial = "5CD99", Accessories = "Charger", DevicePasswordEnc = "ENCRYPTED", PhotoStageId = three
        }) == "");
    var second = (await Newest())!;
    first = await Get(first.Id);
    Check("it is a NEW ticket, a Workshop job at Booked in", second.Id != first.Id && second is { Category: TicketCategory.Workshop, Stage: "awaiting-diagnosis" });
    Check("...for the same client, contact, people and priority",
        second.ClientId == clientId && second is { ContactName: "Jane", AssignedTo: "Sue Tech, Bob Tech", Priority: TicketPriority.High },
        $"{second.ClientId} / {second.ContactName} / {second.AssignedTo} / {second.Priority}");
    Check("...its subject is the machine and the fault's first line", second.Subject == "HP ProBook 450 — Screen cracked across the middle.", second.Subject);
    Check("...its fault reported is the whole of what was typed", second.Description == "Screen cracked across the middle.\nStill boots.");
    Check("...with its own machine details, the password kept encrypted",
        second is { AssetMake: "HP", AssetModel: "ProBook 450", AssetSerial: "5CD99", Accessories: "Charger", DevicePasswordProtected: true, DevicePasswordEnc: "ENCRYPTED" });
    await using (var db = new PlatformDb(options))
    {
        Check("...its three photos as it arrived are ITS photos", await db.TicketPhotos.CountAsync(p => p.TicketId == second.Id && p.Kind == PhotoKind.Before) == 3);
        Check("...and none were added to the first machine", await db.TicketPhotos.CountAsync(p => p.TicketId == first.Id) == 0);
        var n1 = await db.TicketNotes.Where(n => n.TicketId == first.Id).Select(n => n.Body).ToListAsync();
        var n2 = await db.TicketNotes.Where(n => n.TicketId == second.Id).Select(n => n.Body).ToListAsync();
        Check("the first machine's story says the second was booked in with it, in one sentence",
            n1.Any(b => b.StartsWith($"#{second.Number} booked in with this one") && !b.EndsWith("..")), string.Join(" | ", n1));
        Check("the second's says what it was booked in with, and its photos",
            n2.Any(b => b.StartsWith($"Booked in with #{first.Number}") && b.Contains("3 photos")), string.Join(" | ", n2));
    }
    Check("both carry the same booking: the first one's id", first.BookingId == first.Id && second.BookingId == first.Id, $"{first.BookingId} / {second.BookingId}");
    Check("the first stays at Booked in — adding a machine moves nothing", first.Stage == "awaiting-diagnosis");

    Console.WriteLine();
    Console.WriteLine("== a third, added from the second ==");
    var more = await Stage(Sue, 4);
    Check("a technician on the job adds a third from the SECOND ticket",
        await tickets.ActAsync(1, second.Id, Sue, T, Add, new StageInput { Details = "Keyboard missing keys.", PhotoStageId = more }) == "");
    var third = (await Newest())!;
    Check("...with no machine named, the subject is just the fault", third.Subject == "Keyboard missing keys.", third.Subject);
    Check("...and it joins the SAME booking, not a new one", third.BookingId == first.Id, $"{third.BookingId}");
    var with1 = await tickets.BookedWithAsync(1, await Get(first.Id));
    var with3 = await tickets.BookedWithAsync(1, third);
    Check("each lists the other two, oldest first, with their stage",
        with1.Select(x => x.Id).SequenceEqual(new[] { second.Id, third.Id }) && with3.Select(x => x.Id).SequenceEqual(new[] { first.Id, second.Id })
        && with1.All(x => x.StageLabel == "Booked in"),
        string.Join(", ", with1.Select(x => $"#{x.Number} {x.StageLabel}")));
    var detail = new RemoteSupport.Shared.TicketDetail();
    await tickets.FillWorkflowAsync(detail, third, OA, Olive);
    Check("the console is sent the same list", detail.BookedWith.Select(x => x.Number).SequenceEqual(new[] { first.Number, second.Number }));

    Console.WriteLine();
    Console.WriteLine("== each goes its own way ==");
    await tickets.ActAsync(1, first.Id, Sue, T, "inspection", new StageInput { Details = "DC jack loose." });
    Check("the first is inspected; the others stay at Booked in",
        (await Get(first.Id)).Stage == "inspected" && (await Get(second.Id)).Stage == "awaiting-diagnosis" && (await Get(third.Id)).Stage == "awaiting-diagnosis");
    Check("once work has started it is no longer offered", !Keys(await Get(first.Id), OA, Olive).Contains(Add));
    var late = await tickets.ActAsync(1, first.Id, Olive, OA, Add, new StageInput { Details = "x", PhotoStageId = await Stage(Olive, 3) });
    Check("...and pressing it then is REFUSED, saying to book it in on its own", late.Contains("new ticket"), late);
    Check("one can be abandoned and the others carry on",
        await tickets.ActAsync(1, third.Id, Olive, OA, "abandon", new StageInput { Reason = "Customer took it back." }) == ""
        && (await Get(third.Id)).Stage == "abandoned" && (await Get(second.Id)).Stage == "awaiting-diagnosis");
    Check("...and shows as Abandoned beside them",
        (await tickets.BookedWithAsync(1, await Get(second.Id))).Any(x => x.Id == third.Id && x.StageLabel == "Abandoned"));

    // ---- the SAME job (2026-10-02): a data transfer's old and new machine on one ticket ----
    Console.WriteLine();
    Console.WriteLine("== two machines on ONE job: a data transfer (2026-10-02) ==");
    var job = await tickets.CreateAsync(1, "Data transfer", "Move everything from the old laptop to the new one.", Olive, TicketSource.Manual,
        contactName: "Jane", assignedTo: Sue, category: TicketCategory.Workshop, clientId: clientId,
        extra: x => { x.AssetMake = "Lenovo"; x.AssetModel = "T440"; });
    var jobIn = await Stage(Olive, 3);
    await photos.AttachStagedAsync(1, job.Id, jobIn, Olive);
    int ticketsBefore;
    await using (var db = new PlatformDb(options)) ticketsBefore = await db.Tickets.CountAsync();

    var noPicsSame = await tickets.ActAsync(1, job.Id, Olive, OA, Add, new StageInput { SameJob = true, Details = "The new laptop." });
    Check("same job still needs three photos of THAT machine", noPicsSame.Contains("photos"), noPicsSame);
    var newPics = await Stage(Olive, 3);
    Check("same job with no words typed is fine — the job's fault is already written",
        await tickets.ActAsync(1, job.Id, Olive, OA, Add, new StageInput
        {
            SameJob = true, AssetMake = "Lenovo", AssetModel = "X1 Carbon", AssetSerial = "PF3NEW",
            Accessories = "USB-C charger", DevicePasswordEnc = "ENC-NEW", PhotoStageId = newPics
        }) == "");
    await using (var db = new PlatformDb(options))
    {
        Check("NO new ticket was made", await db.Tickets.CountAsync() == ticketsBefore);
        var ms = await db.TicketMachines.Where(m => m.TicketId == job.Id).ToListAsync();
        Check("the machine is on THIS ticket, with the booking's answers and its password kept encrypted",
            ms.Count == 1 && ms[0] is { Make: "Lenovo", Model: "X1 Carbon", Serial: "PF3NEW", Accessories: "USB-C charger", PasswordProtected: true, PasswordEnc: "ENC-NEW" });
        Check("its three photos are on this ticket, marked as THAT machine's",
            await db.TicketPhotos.CountAsync(p => p.TicketId == job.Id && p.Kind == PhotoKind.Before && p.MachineId == ms[0].Id) == 3
            && await db.TicketPhotos.CountAsync(p => p.TicketId == job.Id && p.Kind == PhotoKind.Before && p.MachineId == null) == 3);
        var entry = await db.TicketNotes.Where(n => n.TicketId == job.Id && n.FormTitle == "Another machine on this job").Select(n => n.Body).FirstOrDefaultAsync();
        Check("the story has it as its own entry, written like a booking",
            entry != null && entry.Contains("Machine 2 on this job") && entry.Contains("Lenovo X1 Carbon · serial PF3NEW") && entry.Contains("Photos in: 3"),
            entry ?? "none");
    }
    var jobNow = await Get(job.Id);
    Check("it belongs to no booking of separate tickets, and the job stays at Booked in",
        jobNow.BookingId == null && jobNow.Stage == "awaiting-diagnosis");
    Check("a technician on the job may add a third the same way",
        await tickets.ActAsync(1, job.Id, Sue, T, Add, new StageInput { SameJob = true, Details = "External drive with the backup.", PhotoStageId = await Stage(Sue, 3) }) == "");
    var jd = new RemoteSupport.Shared.TicketDetail();
    await tickets.FillWorkflowAsync(jd, jobNow, OA, Olive, enc => enc == "ENC-NEW" ? "hunter2" : "?");
    Check("the console is sent both further machines, in order, with what each is for",
        jd.Machines.Count == 2 && jd.Machines[0].Model == "X1 Carbon" && jd.Machines[1].Purpose == "External drive with the backup.");
    Check("...a password only through the caller's decryption, as the ticket's own",
        jd.Machines[0] is { PasswordProtected: true, Password: "hunter2" } && jd.Machines[1] is { PasswordProtected: false, Password: "" });
    Check("...each one's own photos in", jd.Machines.All(m => m.PhotosIn == 3), string.Join(",", jd.Machines.Select(m => m.PhotosIn)));
    Check("...and photos out needed: three of EACH of the three machines", jd.PhotosOutRequired == 9, jd.PhotosOutRequired.ToString());

    // To where it can leave: inspected, repairable, solved.
    Check("the job moves on as ONE job",
        await tickets.ActAsync(1, job.Id, Sue, T, "inspection", new StageInput { Details = "Both machines boot." }) == ""
        && await tickets.ActAsync(1, job.Id, Sue, T, "repairable") == ""
        && await tickets.ActAsync(1, job.Id, Sue, T, "solved-invoice", new StageInput { Details = "Data moved, checked with Jane.", Minutes = 90, Amount = 650m }) == "",
        (await Get(job.Id)).Stage);
    Check("once work has started no machine can be added", !Keys(await Get(job.Id), OA, Olive).Contains(Add));
    for (int i = 0; i < 3; i++) await photos.AddAsync(1, Jpeg(), $"out-{i}.jpg", PhotoKind.After, Olive, job.Id, "");
    var three_out = await tickets.ActAsync(1, job.Id, Olive, OA, TicketService.CollectedKey);
    Check("three photos out are NOT enough for three machines", three_out.Contains("9 photos") && three_out.Contains("3 of each of the 3 machines"), three_out);
    var sendOut = await tickets.ActAsync(1, job.Id, Olive, OA, TicketService.DeliverKey);
    Check("...nor to send them out for delivery", sendOut.Contains("9 photos"), sendOut);
    for (int i = 3; i < 9; i++) await photos.AddAsync(1, Jpeg(), $"out-{i}.jpg", PhotoKind.After, Olive, job.Id, "");
    Check("with nine, they are collected together", await tickets.ActAsync(1, job.Id, Olive, OA, TicketService.CollectedKey) == "");
    await using (var db = new PlatformDb(options))
        Check("...and the story says all three left",
            await db.TicketNotes.AnyAsync(n => n.TicketId == job.Id && n.Body.StartsWith("All 3 machines collected, with 9 photos")));

    var separate = await tickets.CreateAsync(1, "One machine", "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.Workshop, clientId: clientId);
    Check("a separate job is still the default: no SameJob, no fault, REFUSED as before",
        (await tickets.ActAsync(1, separate.Id, Olive, OA, Add, new StageInput { PhotoStageId = await Stage(Olive, 3) })).Contains("wrong"));
    var one = new RemoteSupport.Shared.TicketDetail();
    await tickets.FillWorkflowAsync(one, separate, OA, Olive);
    Check("...and a one-machine job still needs three photos out", one.PhotosOutRequired == 3 && one.Machines.Count == 0);

    Console.WriteLine();
    Console.WriteLine("== the edges ==");
    var alone = await tickets.CreateAsync(1, "On its own", "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.Workshop);
    Check("a machine booked in alone belongs to no booking", alone.BookingId == null && (await tickets.BookedWithAsync(1, alone)).Count == 0);
    var callout = await tickets.CreateAsync(1, "Call out", "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.CallOut);
    Check("a call out is never offered it", !Keys(callout, OA, Olive).Contains(Add));
    Check("...and pressing it is REFUSED",
        (await tickets.ActAsync(1, callout.Id, Olive, OA, Add, new StageInput { Details = "x", PhotoStageId = await Stage(Olive, 3) })).Length > 0);
    var general = await tickets.CreateAsync(1, "Toner", "", Olive, TicketSource.Manual);
    Check("nor is a General ticket", !Keys(general, OA, Olive).Contains(Add));
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { try { Directory.Delete(dir, true); } catch { } }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

static byte[] Jpeg() => RemoteSupport.Tools.TestImages.Jpeg();

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
