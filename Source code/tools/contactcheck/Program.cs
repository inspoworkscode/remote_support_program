// =====================================================================================
// contactcheck - proves stage 3.6.1 (contacts) against the real model, the real migrations
// and the real ContactService, on a throwaway SQLite database.
//
// What it is here to catch, in order of how much it would cost to find later:
//
//   1. The migration's data step. Every client that already had a contact person must come
//      out with that person as a Contact, ONCE. Run it twice and you must still have one.
//      A migration that quietly duplicates people on a replay is the kind of thing nobody
//      notices until a customer has four of everybody.
//
//   2. The first column never being blank. Contact, else client, else the logged-on user.
//      An unlinked machine still has somebody sitting at it, and a device list full of
//      machine names and empty owner cells is the thing this stage exists to stop.
//
//   3. The invariant SQLite cannot keep: a machine's contact must belong to its client.
//      ContactService.LinkDeviceAsync is the only place that may write a device's owner,
//      and it has to refuse the mismatch rather than pick a side.
//
// Run it with no arguments. It creates its own database under the temp folder, deletes it
// on the way out, and touches nothing else.
// =====================================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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

var dir = Path.Combine(Path.GetTempPath(), "contactcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var dbPath = Path.Combine(dir, "platform.db");
var conn = $"Data Source={dbPath}";

Console.WriteLine("contactcheck - stage 3.6.1, contacts");
Console.WriteLine($"Database : {dbPath}");
Console.WriteLine();

// The migration this stage added. Everything before it is "the old world"; applying it is the
// thing under test.
const string ThisMigration = "20260920083611_ContactsAndDeviceOwner";

var services = new ServiceCollection();
services.AddDbContextFactory<PlatformDb>(o => o.UseHarnessDb(conn));
services.AddSingleton<ContactService>();
var sp = services.BuildServiceProvider();
var dbf = sp.GetRequiredService<IDbContextFactory<PlatformDb>>();
var contacts = sp.GetRequiredService<ContactService>();

try
{
    int tenantId = 1, withContact, withoutContact;
    if (!HarnessDb.SqliteUpgradePart("the contacts migration"))
    {
        // What the migration leaves, through today's model: Zenith with Bob as its contact, Acme with nobody.
        await using var db = await dbf.CreateDbContextAsync();
        await HarnessDb.MigrateAsync(db);
        await Seed.Tenant(db, tenantId, "check", "Check IT", Guid.NewGuid().ToString("N"));
        var zenith = new Client { TenantId = tenantId, Name = "Zenith Motors", ContactName = "Bob Smith", Phone = "011 555 0100", Email = "bob@zenith.test" };
        var acme = new Client { TenantId = tenantId, Name = "Acme Holdings" };
        db.Clients.AddRange(zenith, acme);
        await db.SaveChangesAsync();
        db.Contacts.Add(new Contact { TenantId = tenantId, ClientId = zenith.Id, Name = "Bob Smith", Phone = "011 555 0100", Email = "bob@zenith.test" });
        await db.SaveChangesAsync();
        (withContact, withoutContact) = (zenith.Id, acme.Id);
    }
    else
    {
    // ---------------------------------------------------------------- the old world
    // Migrate to the migration BEFORE this one, so the database genuinely looks like a
    // customer's did yesterday: clients with one inline contact and no Contacts table.
    string previous;
    await using (var db = await dbf.CreateDbContextAsync())
    {
        var all = db.Database.GetMigrations().ToList();
        var here = all.IndexOf(ThisMigration);
        if (here <= 0)
        {
            Console.Error.WriteLine($"Cannot find {ThisMigration} in the migration list, or it is first.");
            Console.Error.WriteLine("If the migration was renamed, update ThisMigration in this file.");
            return 2;
        }
        previous = all[here - 1];
        db.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(previous);
    }

    await using (var db = await dbf.CreateDbContextAsync())
    {
        // By SQL, like the clients below: today's Tenant has columns this table does not (§59).
        await Seed.OldTenant(db, tenantId, "check", "Check IT", Guid.NewGuid().ToString("N"));

        // Raw SQL, because the C# model already knows about Contacts and we are pretending it
        // does not yet. This is exactly the row shape the old world produced.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Clients (TenantId, Name, ContactName, Phone, Email, Address, Notes, Archived, CreatedUtc) " +
            "VALUES ({0}, 'Zenith Motors', 'Bob Smith', '011 555 0100', 'bob@zenith.test', '', '', 0, {1})",
            tenantId, DateTime.UtcNow);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Clients (TenantId, Name, ContactName, Phone, Email, Address, Notes, Archived, CreatedUtc) " +
            "VALUES ({0}, 'Acme Holdings', '', '', '', '', '', 0, {1})",
            tenantId, DateTime.UtcNow);

        withContact = await db.Clients.Where(c => c.Name == "Zenith Motors").Select(c => c.Id).FirstAsync();
        withoutContact = await db.Clients.Where(c => c.Name == "Acme Holdings").Select(c => c.Id).FirstAsync();
    }

    // ---------------------------------------------------------------- the migration
    Console.WriteLine($"Migrating {previous} -> {ThisMigration}");
    await using (var db = await dbf.CreateDbContextAsync()) await HarnessDb.MigrateAsync(db);

    await using (var db = await dbf.CreateDbContextAsync())
    {
        var made = await db.Contacts.Where(c => c.ClientId == withContact).ToListAsync();
        Check("a client's inline contact becomes a Contact", made.Count == 1, $"got {made.Count}");
        Check("the migrated contact keeps the name", made.Count == 1 && made[0].Name == "Bob Smith",
            made.Count == 1 ? made[0].Name : "-");
        Check("the migrated contact keeps the phone and email",
            made.Count == 1 && made[0].Phone == "011 555 0100" && made[0].Email == "bob@zenith.test");
        Check("a client with no contact person gets none",
            !await db.Contacts.AnyAsync(c => c.ClientId == withoutContact));
        Check("Clients.ContactName is kept, not moved",
            await db.Clients.Where(c => c.Id == withContact).Select(c => c.ContactName).FirstAsync() == "Bob Smith");
    }

    // Replaying the data step must not produce a second Bob. The migration history already stops
    // this, so we run the statement itself rather than the migration.
    await using (var db = await dbf.CreateDbContextAsync())
    {
        await db.Database.ExecuteSqlRawAsync(@"
            INSERT INTO Contacts (TenantId, ClientId, Name, Phone, Email, Role, Notes, Archived, CreatedUtc)
            SELECT c.TenantId, c.Id, TRIM(c.ContactName), COALESCE(c.Phone, ''), COALESCE(c.Email, ''), '', '', 0, c.CreatedUtc
            FROM Clients c
            WHERE TRIM(COALESCE(c.ContactName, '')) <> ''
              AND NOT EXISTS (
                  SELECT 1 FROM Contacts x
                  WHERE x.ClientId = c.Id AND LOWER(x.Name) = LOWER(TRIM(c.ContactName)));");
        Check("replaying the data step adds nobody",
            await db.Contacts.CountAsync(c => c.ClientId == withContact) == 1);
    }
    }

    // ---------------------------------------------------------------- contacts
    var (bob, bobErr) = await contacts.CreateAsync(tenantId, withContact, "Bob Smith", null, null, null, null);
    Check("adding a contact who already exists returns the same one", bobErr == "" && bob != null
        && await CountContacts(withContact) == 1);

    var (sue, sueErr) = await contacts.CreateAsync(tenantId, withContact, "Sue Jones", "082 555 0111", "sue@zenith.test", "Accounts", "");
    Check("a second person at the same client is added", sueErr == "" && sue != null && await CountContacts(withContact) == 2);

    var (dave, daveErr) = await contacts.CreateAsync(tenantId, withoutContact, "Dave at Acme", "", "", "", "");
    Check("a contact at another client is added there", daveErr == "" && dave != null);

    var (_, noName) = await contacts.CreateAsync(tenantId, withContact, "   ", null, null, null, null);
    Check("a nameless contact is refused", noName.Length > 0, noName);

    var (_, noClient) = await contacts.CreateAsync(tenantId, 999999, "Ghost", null, null, null, null);
    Check("a contact at a client that does not exist is refused", noClient.Length > 0, noClient);

    // ---------------------------------------------------------------- linking machines
    await using (var db = await dbf.CreateDbContextAsync())
    {
        db.Devices.Add(new Device { TenantId = tenantId, DeviceId = "dev-person", DeviceKey = "k1", MachineName = "SUE-PC", UserName = "sue" });
        db.Devices.Add(new Device { TenantId = tenantId, DeviceId = "dev-company", DeviceKey = "k2", MachineName = "ZENITH-SRV", UserName = "Administrator" });
        db.Devices.Add(new Device { TenantId = tenantId, DeviceId = "dev-loose", DeviceKey = "k3", MachineName = "RECEPTION", UserName = "frontdesk" });
        await db.SaveChangesAsync();
    }

    var (toSue, e1) = await contacts.LinkDeviceAsync(tenantId, "dev-person", null, sue!.Id);
    Check("linking to a person succeeds", e1 == "", e1);
    Check("linking to a person implies their client", toSue.ClientId == withContact, $"{toSue.ClientId}");

    var (toCompany, e2) = await contacts.LinkDeviceAsync(tenantId, "dev-company", withContact, null);
    Check("linking to a company succeeds with no person", e2 == "" && toCompany.ClientId == withContact && toCompany.ContactId == null);

    // The invariant SQLite cannot keep.
    var (_, mismatch) = await contacts.LinkDeviceAsync(tenantId, "dev-loose", withoutContact, sue.Id);
    Check("a contact belonging to a different client is refused", mismatch.Length > 0, mismatch);
    await using (var db = await dbf.CreateDbContextAsync())
    {
        var loose = await db.Devices.FirstAsync(d => d.DeviceId == "dev-loose");
        Check("the refused link changed nothing", loose.ClientId == null && loose.ContactId == null);
    }

    var (_, e3) = await contacts.LinkDeviceAsync(tenantId, "dev-person", null, null);
    Check("a machine can be unlinked from everybody", e3 == "");
    await using (var db = await dbf.CreateDbContextAsync())
    {
        var d = await db.Devices.FirstAsync(x => x.DeviceId == "dev-person");
        Check("unlinking clears both halves", d.ClientId == null && d.ContactId == null);
    }
    await contacts.LinkDeviceAsync(tenantId, "dev-person", null, sue.Id);   // put it back for later checks

    var (_, noSuchDevice) = await contacts.LinkDeviceAsync(tenantId, "nope", withContact, null);
    Check("linking a machine that is not on file is refused", noSuchDevice.Length > 0, noSuchDevice);

    var (_, noSuchContact) = await contacts.LinkDeviceAsync(tenantId, "dev-loose", null, 999999);
    Check("linking to a contact that does not exist is refused", noSuchContact.Length > 0, noSuchContact);

    // ---------------------------------------------------------------- the first column
    // The whole reason the stage exists: this cell is never empty.
    Check("a machine with a person shows the person",
        ContactService.OwnerName("Sue Jones", "Zenith Motors", "", "sue") == "Sue Jones");
    Check("a machine with only a company shows the company",
        ContactService.OwnerName("", "Zenith Motors", "", "Administrator") == "Zenith Motors");
    Check("a machine with neither shows who is signed in",
        ContactService.OwnerName("", "", "", "frontdesk") == "frontdesk");
    Check("a machine with nothing at all still shows something",
        ContactService.OwnerName("", "", "", "") == "—");
    Check("whitespace does not count as a name",
        ContactService.OwnerName("   ", "  ", "  ", "frontdesk") == "frontdesk");

    // The console resolves the same three-step fallback over the wire. If these two ever disagree
    // the portal and the device list will name a machine's owner differently on the same screen.
    var wire = new RemoteSupport.Shared.DeviceInfo { ContactName = "", ClientName = "", UserName = "frontdesk" };
    Check("the console's OwnerName agrees with the server's",
        wire.OwnerName == ContactService.OwnerName("", "", "", "frontdesk"));

    // ---------------------------------------------------------------- archiving
    var archiveErr = await contacts.ArchiveAsync(tenantId, sue.Id);
    Check("archiving a contact succeeds", archiveErr == "", archiveErr);
    await using (var db = await dbf.CreateDbContextAsync())
    {
        var d = await db.Devices.FirstAsync(x => x.DeviceId == "dev-person");
        Check("archiving takes the person off their machine", d.ContactId == null);
        Check("archiving leaves the machine with its client", d.ClientId == withContact);
        Check("an archived contact is out of the client's list",
            !(await contacts.ForClientAsync(tenantId, withContact)).Any(c => c.Id == sue.Id));
        Check("an archived contact is still on file",
            await db.Contacts.AnyAsync(c => c.Id == sue.Id && c.Archived));
    }

    var (revived, revivedErr) = await contacts.CreateAsync(tenantId, withContact, "Sue Jones", null, null, null, null);
    Check("adding an archived contact again brings them back rather than duplicating",
        revivedErr == "" && revived!.Id == sue.Id && await CountContacts(withContact) == 2);

    // ---------------------------------------------------------------- search
    var found = await contacts.SearchAsync(tenantId, "sue");
    Check("search finds a contact by lower-case fragment", found.Any(c => c.Id == sue.Id));
    var foundUpper = await contacts.SearchAsync(tenantId, "SUE");
    Check("search is case-insensitive", foundUpper.Any(c => c.Id == sue.Id));

    async Task<int> CountContacts(int clientId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Contacts.CountAsync(c => c.ClientId == clientId && !c.Archived);
    }
}
finally
{
    // SQLite keeps the file handle in the pool; drop it so the delete succeeds.
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;
