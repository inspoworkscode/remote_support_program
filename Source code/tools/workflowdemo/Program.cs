// =====================================================================================
// workflowdemo - a throwaway platform to LOOK at the Workshop workflow (stage 3.7).
//
// Makes a database in the folder you give it, with one company, one person of each role and
// workshop jobs parked at the stages worth looking at, then prints how to start a platform on it
// and how to sign in. Nothing touches the live server or its database.
//
//   dotnet run -c Debug --project tools/workflowdemo/workflowdemo.csproj -- <empty folder>
//
// Every person's password is "workshop-demo". It is a demo on this PC and nothing else.
// =====================================================================================

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Tools;

if (args.Length < 1) { Console.WriteLine("Give an empty folder for the demo database."); return 1; }
var dataDir = Path.GetFullPath(args[0]);
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "platform.db");
if (File.Exists(dbPath)) { Console.WriteLine($"{dbPath} already exists. Use an empty folder."); return 1; }

var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={dbPath};Pooling=False").Options;
await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
var factory = new Factory(options);
var tenants = new TenantService(factory, new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("workflowdemo"));
var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
var tickets = new TicketService(factory, photos);
var quotes = new QuoteService(factory);

var (tenant, err) = await tenants.CreateAsync("Workshop Demo IT", "workshop-demo", 7);
if (tenant == null) { Console.WriteLine(err); return 1; }
const string Pw = "workshop-demo";
const string Owner = "Olivia Owner", Sam = "Sam Supervisor", Sue = "Sue Tech", Bob = "Bob Tech", Office = "Oscar Office";
await tenants.AddTechnicianAsync(tenant.Id, "owner@demo.test", Pw, Owner, TenantRole.OwnerSupervisor);
await tenants.AddTechnicianAsync(tenant.Id, "sam@demo.test", Pw, Sam, TenantRole.Admin);
await tenants.AddTechnicianAsync(tenant.Id, "sue@demo.test", Pw, Sue, TenantRole.Technician);
await tenants.AddTechnicianAsync(tenant.Id, "bob@demo.test", Pw, Bob, TenantRole.Technician);
await tenants.AddTechnicianAsync(tenant.Id, "office@demo.test", Pw, Office, TenantRole.Billing);

// A real photo, drawn here, so the pages have something to show. PNG, 160x120, one colour.
byte[] Photo(byte r, byte g, byte b) => PngMaker.Solid(160, 120, r, g, b);
int t = tenant.Id;
async Task<Ticket> Book(string subject, string make, string model, string serial)
{
    var job = await tickets.CreateAsync(t, subject, "Booked in at the counter.", Office, TicketSource.Manual,
        assignedTo: Sue, category: TicketCategory.Workshop, extra: x => { x.AssetMake = make; x.AssetModel = model; x.AssetSerial = serial; x.Accessories = "Charger"; });
    for (int i = 0; i < 3; i++) await photos.AddAsync(t, Photo(90, (byte)(110 + i * 30), 200), $"in-{i}.png", PhotoKind.Before, Office, job.Id, "");
    await tickets.AddNoteAsync(t, job.Id, Office, $"Workshop ticket opened, assigned to {Sue}.", true);
    return job;
}
async Task Out(int id) { for (int i = 0; i < 3; i++) await photos.AddAsync(t, Photo(60, 170, (byte)(90 + i * 30)), $"out-{i}.png", PhotoKind.After, Sue, id, ""); }

// The bench in Clint's order: Inspection findings, then Repairable. What was done is written into
// Solved — to be invoiced, below.
async Task Bench(int id, string found)
{
    await tickets.ActAsync(t, id, Sue, TenantRole.Technician, "inspection", new StageInput { Details = found });
    await tickets.ActAsync(t, id, Sue, TenantRole.Technician, "repairable");
}

var fresh = await Book("Laptop will not power on", "Dell", "Latitude 5420", "7XK2Q13");

var bench = await Book("Very slow, fan noise", "HP", "ProBook 450 G8", "5CD1234XYZ");
await Bench(bench.Id, "Fan clogged with dust; running at 95 degrees under load.");
await tickets.AddNoteAsync(t, bench.Id, Office, "@Sue Tech the customer asked whether it will be ready by Friday?", false);

var toInvoice = await Book("Cracked DC jack", "Lenovo", "ThinkPad E14", "PF2ABC12");
await Bench(toInvoice.Id, "DC jack cracked; charges only at an angle.");
await tickets.ActAsync(t, toInvoice.Id, Sue, TenantRole.Technician, "solved-invoice", new StageInput { Details = "Replaced DC jack, tested on charge for an hour.", Minutes = 75, Amount = 850 });

var quote = await Book("Needs more memory and an SSD", "Asus", "VivoBook 15", "M1NRKD0123");
await Bench(quote.Id, "4GB soldered + one free slot; 1TB HDD failing SMART.");
var (ram, _, _) = await quotes.AddItemAsync(t, "DDR4 memory 8GB SO-DIMM", Sue);
var (ssd, _, _) = await quotes.AddItemAsync(t, "SSD 512GB SATA 2.5", Sue);
await quotes.AddItemAsync(t, "Laptop screen 15.6 FHD", Bob);
await quotes.AddItemAsync(t, "Laptop battery", Bob);
var (label, _) = await photos.AddAsync(t, Photo(200, 160, 60), "ram-label.png", PhotoKind.Part, Sue, quote.Id, "");
await quotes.AddLineAsync(t, quote.Id, ram!.Id, "KVR26S19S8/8", 1, new[] { label!.Id }, Sue, TenantRole.Technician);
await quotes.AddLineAsync(t, quote.Id, ssd!.Id, "SA400S37/480G", 1, null, Sue, TenantRole.Technician);
await tickets.ActAsync(t, quote.Id, Sue, TenantRole.Technician, "quote-needed");

var dni = await Book("Warranty return — our fault", "Acer", "Aspire 5", "NXHSP123");
await Bench(dni.Id, "Loose ribbon cable from our repair last week.");
await tickets.ActAsync(t, dni.Id, Sue, TenantRole.Technician, "do-not-invoice", new StageInput { Reason = "Our own workmanship from last week." });

var ready = await Book("Screen flicker", "Dell", "Inspiron 3511", "9QWERTY");
await Bench(ready.Id, "Display cable not seated.");
await tickets.ActAsync(t, ready.Id, Sue, TenantRole.Technician, "solved-invoice", new StageInput { Details = "Reseated display cable.", Minutes = 30, Amount = 450 });
await tickets.ActAsync(t, ready.Id, Office, TenantRole.Billing, "client-invoiced", new StageInput { Amount = 450, Reference = "INV-2041" });
await Out(ready.Id);

// Call outs (2026-09-27): one just booked, one with its Work done and three ways to finish.
async Task<Ticket> CallOut(string subject, string site)
    => await tickets.CreateAsync(t, subject, "Reported by phone.", Office, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.CallOut,
        extra: x => { x.SiteAddress = site; x.AppointmentUtc = DateTime.UtcNow.AddHours(3); });
var visit = await CallOut("Printer jams on every page", "12 Long Street, Cape Town");
var visited = await CallOut("Office Wi-Fi drops every hour", "4 Bree Street, Cape Town");
await tickets.ActAsync(t, visited.Id, Sue, TenantRole.Technician, "callout-work-done",
    new StageInput { Details = "Moved the access point off the microwave's channel; tested for an hour.", Minutes = 90 });

// Walk in, remote support and General (2026-09-27). The remote session belongs to a client with
// another open job, so Link to parent ticket has something to offer; the General one has no client.
int zenith;
await using (var db = new PlatformDb(options))
{
    var z = new Client { TenantId = t, Name = "Zenith Accounting" };
    db.Clients.Add(z);
    await db.SaveChangesAsync();
    zenith = z.Id;

    // Enough clients that a picker has something to narrow, some people under them, and machines
    // nobody has linked yet (2026-10-02: the client picker, and the portal on a phone).
    var more = new[] { "Acme Plumbing", "Bayside Dental", "Cape Coastal Properties", "Delta Logistics", "Eagle Eye Security",
                       "Fairview Primary School", "Green Leaf Florist", "Harbour View Hotel", "Ironwood Furniture", "Jacaranda Vets",
                       "Kloof Street Bakery", "Lighthouse Church", "Morne Pienaar", "Northgate Motors", "Oakridge Estates" };
    foreach (var name in more) db.Clients.Add(new Client { TenantId = t, Name = name, Phone = "021 555 " + (1000 + name.Length * 37), Email = name.Split(' ')[0].ToLower() + "@example.test" });
    await db.SaveChangesAsync();
    var bayside = db.Clients.First(c => c.Name == "Bayside Dental").Id;
    db.Contacts.Add(new Contact { TenantId = t, ClientId = zenith, Name = "Thandi Mokoena", Role = "Accounts" });
    db.Contacts.Add(new Contact { TenantId = t, ClientId = zenith, Name = "Pieter van Wyk", Role = "Reception" });
    db.Contacts.Add(new Contact { TenantId = t, ClientId = bayside, Name = "Dr Sarah Naidoo", Role = "Practice owner" });
    var n = 0;
    foreach (var (machine, user, client) in new[] { ("ZEN-PC-07", "thandi", (int?)zenith), ("RECEPTION-01", "front", (int?)null), ("DESKTOP-4K2J9", "sarah", (int?)null), ("LAPTOP-HV22", "manager", (int?)null) })
        db.Devices.Add(new Device { TenantId = t, DeviceId = $"dev-demo-{++n}", DeviceKey = Guid.NewGuid().ToString("N"), MachineName = machine, UserName = user,
            Os = "Windows 11 Pro", ClientId = client, LastSeenUtc = DateTime.UtcNow.AddHours(-n) });
    await db.SaveChangesAsync();
}
var walkIn = await tickets.CreateAsync(t, "Screen flickers when moved", "Customer waiting at the counter.", Office, TicketSource.Manual,
    assignedTo: Sue, category: TicketCategory.WalkIn, clientId: zenith);
var officeMove = await tickets.CreateAsync(t, "Office move — all PCs", "Move and reconnect 12 PCs.", Office, TicketSource.Manual,
    assignedTo: Sue, category: TicketCategory.General, clientId: zenith);
var session = await tickets.CreateAsync(t, "Mapped drives missing after the move", "Mapped drives missing after the move", Sue, TicketSource.Unattended,
    "dev-demo", "ZEN-PC-07", "", TicketPriority.Normal, Sue, TicketCategory.RemoteSupport, zenith);
var toner = await tickets.CreateAsync(t, "Order more toner for the office", "Two black cartridges for the HP in reception.", Office, TicketSource.Manual,
    assignedTo: Sue, category: TicketCategory.General);

Console.WriteLine($"Demo database: {dbPath}");
Console.WriteLine();
Console.WriteLine("Jobs:");
foreach (var j in new[] { fresh, bench, toInvoice, quote, dni, ready, visit, visited, walkIn, officeMove, session, toner })
{
    var now = await tickets.GetAsync(t, j.Id);
    Console.WriteLine($"  #{now!.Number}  {Workflow.Label(now.Stage),-45} {now.Subject}");
}
Console.WriteLine();
Console.WriteLine("Sign in with any of these; the password for all of them is " + Pw);
Console.WriteLine("  owner@demo.test (Owner, no seat)  sam@demo.test (Supervisor)  sue@demo.test and bob@demo.test (Technicians)  office@demo.test (Office administrator)");
return 0;

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}

/// <summary>Just enough PNG to draw a solid rectangle — no image library for a demo.</summary>
file static class PngMaker
{
    public static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var raw = new byte[h * (1 + w * 3)];
        for (int y = 0; y < h; y++)
        {
            int row = y * (1 + w * 3);
            for (int x = 0; x < w; x++) { raw[row + 1 + x * 3] = r; raw[row + 2 + x * 3] = g; raw[row + 3 + x * 3] = b; }
        }
        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true)) zs.Write(raw);
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); ms.Write(len);
            var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            ms.Write(td);
            var crc = BitConverter.GetBytes(Crc(td)); Array.Reverse(crc); ms.Write(crc);
        }
        var ihdr = new byte[13];
        var bw = BitConverter.GetBytes(w); Array.Reverse(bw); bw.CopyTo(ihdr, 0);
        var bh = BitConverter.GetBytes(h); Array.Reverse(bh); bh.CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr); Chunk("IDAT", z.ToArray()); Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }
    static uint Crc(byte[] d)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in d) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        return c ^ 0xFFFFFFFF;
    }
}
