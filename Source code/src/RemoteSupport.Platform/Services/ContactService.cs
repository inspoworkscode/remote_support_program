using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The people at a client, and the one rule the database cannot keep for us: a machine's contact
/// must belong to that machine's client.
///
/// SQLite could not express that as a constraint — it is a pair of foreign keys where one side is
/// nullable — so it lives here instead, and <see cref="LinkDeviceAsync"/> is the only way a device
/// should ever have its owner changed. Writing <c>Device.ContactId</c> directly is how the two
/// halves get to disagree, and a machine that says it belongs to Acme and to Bob at Zenith is a
/// machine whose tickets file in two places.
/// </summary>
public sealed class ContactService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public ContactService(IDbContextFactory<PlatformDb> dbf) => _dbf = dbf;

    public async Task<List<Contact>> ForClientAsync(int tenantId, int clientId, bool includeArchived = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Contacts.Where(c => c.TenantId == tenantId && c.ClientId == clientId);
        if (!includeArchived) q = q.Where(c => !c.Archived);
        return await q.OrderBy(c => c.Name).ToListAsync();
    }

    /// <summary>Every contact the tenant has, for pages that resolve many machines at once.</summary>
    public async Task<List<Contact>> AllAsync(int tenantId, bool includeArchived = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Contacts.Where(c => c.TenantId == tenantId);
        if (!includeArchived) q = q.Where(c => !c.Archived);
        return await q.OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<Contact?> GetAsync(int tenantId, int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Contacts.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId);
    }

    public async Task<List<Contact>> SearchAsync(int tenantId, string term, int take = 12)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Contacts.Where(c => c.TenantId == tenantId && !c.Archived);
        if (!string.IsNullOrWhiteSpace(term))
        {
            // Lower case on both sides, for the reason ClientService.SearchAsync already knows:
            // SQLite's instr() is case-sensitive, so "ro" missed "Roger" and the type-ahead
            // failed at exactly the moment it was supposed to prevent a duplicate.
            var s = term.Trim().ToLower();
            q = q.Where(c => c.Name.ToLower().Contains(s) || c.Email.ToLower().Contains(s)
                          || c.Phone.ToLower().Contains(s) || c.Role.ToLower().Contains(s));
        }
        return await q.OrderBy(c => c.Name).Take(take).ToListAsync();
    }

    public async Task<(Contact? contact, string error)> CreateAsync(int tenantId, int clientId, string name,
        string? phone, string? email, string? role, string? notes)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, "Contact name is required.");
        await using var db = await _dbf.CreateDbContextAsync();
        if (!await db.Clients.AnyAsync(c => c.Id == clientId && c.TenantId == tenantId))
            return (null, "That client does not exist.");

        var n = name.Trim();
        // Reuse rather than duplicate, the same bargain ClientService.CreateAsync strikes: the
        // office administrator adding "Sue" for the second time meant the first one, not a second
        // Sue. Two genuinely different people with one name at one company is rare enough to be
        // worth typing a surname for.
        var existing = await db.Contacts.FirstOrDefaultAsync(c =>
            c.TenantId == tenantId && c.ClientId == clientId && c.Name.ToLower() == n.ToLower());
        if (existing != null)
        {
            if (existing.Archived) { existing.Archived = false; await db.SaveChangesAsync(); }
            return (existing, "");
        }

        var contact = new Contact
        {
            TenantId = tenantId, ClientId = clientId, Name = n,
            Phone = (phone ?? "").Trim(), Email = (email ?? "").Trim(),
            Role = (role ?? "").Trim(), Notes = (notes ?? "").Trim()
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return (contact, "");
    }

    public async Task<string> UpdateAsync(int tenantId, int id, Action<Contact> apply)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var c = await db.Contacts.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (c == null) return "Contact not found.";
        apply(c);
        if (string.IsNullOrWhiteSpace(c.Name)) return "Contact name is required.";
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>
    /// Archives a contact and takes them off any machine they were on, so nothing goes on showing
    /// a person who has left as the owner of a PC. The machine keeps its client — it did not stop
    /// belonging to the company because somebody resigned.
    /// </summary>
    public async Task<string> ArchiveAsync(int tenantId, int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var c = await db.Contacts.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (c == null) return "Contact not found.";
        c.Archived = true;
        foreach (var d in await db.Devices.Where(d => d.TenantId == tenantId && d.ContactId == id).ToListAsync())
            d.ContactId = null;
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>
    /// The result of pointing a machine at an owner: what it now belongs to, and the name to show.
    /// </summary>
    public readonly record struct DeviceOwner(int? ClientId, string ClientName, int? ContactId, string ContactName);

    /// <summary>
    /// Points a machine at a contact, at a client, or at nobody — the one place that may change a
    /// device's owner.
    ///
    /// Passing a contact implies its client: a machine linked to Bob at Zenith belongs to Zenith,
    /// and asking for Bob while naming a different client is a mistake rather than a preference,
    /// so it is refused rather than silently resolved one way or the other.
    /// </summary>
    public async Task<(DeviceOwner owner, string error)> LinkDeviceAsync(int tenantId, string deviceId,
        int? clientId, int? contactId)
    {
        var none = new DeviceOwner(null, "", null, "");
        await using var db = await _dbf.CreateDbContextAsync();
        var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.DeviceId == deviceId);
        if (d == null) return (none, "That machine is not on file.");

        if (clientId is <= 0) clientId = null;
        if (contactId is <= 0) contactId = null;

        Contact? contact = null;
        if (contactId.HasValue)
        {
            contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId.Value && c.TenantId == tenantId);
            if (contact == null) return (none, "That contact does not exist.");
            if (clientId.HasValue && clientId.Value != contact.ClientId)
                return (none, "That contact belongs to a different client.");
            clientId = contact.ClientId;   // the contact decides, because the contact is more specific
        }

        Client? client = null;
        if (clientId.HasValue)
        {
            client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId.Value && c.TenantId == tenantId);
            if (client == null) return (none, "That client does not exist.");
        }

        d.ClientId = clientId;
        d.ContactId = contactId;
        await db.SaveChangesAsync();
        return (new DeviceOwner(clientId, client?.Name ?? "", contactId, contact?.Name ?? ""), "");
    }

    /// <summary>
    /// What the first column of a device list says: the person if we know them, else the company,
    /// else the name they gave when they asked for help (2026-10-18), else whoever is signed in at the machine.
    ///
    /// The fallbacks are the point of the whole thing. A machine nobody has got round to
    /// linking still has somebody sitting at it, and a blank cell where their name should be is
    /// how a device list becomes a list of machine names nobody recognises. The rule is
    /// <see cref="RemoteSupport.Shared.OwnerNames"/>, which the console asks too.
    /// </summary>
    public static string OwnerName(string contactName, string clientName, string requestedBy, string userName) =>
        RemoteSupport.Shared.OwnerNames.For(contactName, clientName, requestedBy, userName);

    /// <summary>
    /// The contacts whose name fits the one somebody gave at a support request, best first — the Link window's and the
    /// Devices page's suggestions (2026-10-18). Never chosen for anybody: two John Smiths are both offered.
    /// </summary>
    public static List<Contact> Suggestions(IEnumerable<Contact> contacts, string given, int take = 5) =>
        contacts.Where(c => !c.Archived)
            .Select(c => (c, score: RemoteSupport.Shared.OwnerNames.Likeness(given, c.Name)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take).Select(x => x.c).ToList();

    /// <summary>
    /// Adds a person at a client and links the machine to them, in that order — the "new contact" half of linking a
    /// machine by the name its person gave (2026-10-18). <see cref="CreateAsync"/> reuses a contact of that name at that
    /// client, so pressing it twice links to one person, not two.
    /// </summary>
    public async Task<(DeviceOwner owner, string error)> LinkDeviceToNewContactAsync(int tenantId, string deviceId,
        int clientId, string name)
    {
        var none = new DeviceOwner(null, "", null, "");
        var clean = RemoteSupport.Shared.OwnerNames.Clean(name);
        if (clean.Length == 0) return (none, "Contact name is required.");
        if (clientId <= 0) return (none, "Choose which client they are at.");
        Contact? contact;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.DeviceId == deviceId);
            if (d == null) return (none, "That machine is not on file.");
            // The email given with THIS name (2026-10-23) — from the machine's record, never from whoever asks, and only
            // while it is still the name being added.
            var email = string.Equals(d.RequestedBy, clean, StringComparison.OrdinalIgnoreCase) ? d.RequestedByEmail : "";
            // The same person by email first: "Jane Smith" and "Jane van der Merwe" with one address are one person, who
            // married. Then by name (CreateAsync). Either way, a blank email on file is filled in — never one overwritten.
            contact = email.Length == 0 ? null : await db.Contacts.FirstOrDefaultAsync(c =>
                c.TenantId == tenantId && c.ClientId == clientId && c.Email.ToLower() == email.ToLower());
            if (contact != null && contact.Archived) { contact.Archived = false; await db.SaveChangesAsync(); }
            if (contact == null)
            {
                string error;
                (contact, error) = await CreateAsync(tenantId, clientId, clean, null, email, null, null);
                if (contact == null) return (none, error);
                if (email.Length > 0 && contact.Email.Length == 0)
                {
                    var row = await db.Contacts.FirstAsync(c => c.Id == contact.Id);
                    row.Email = email;
                    await db.SaveChangesAsync();
                }
            }
        }
        return await LinkDeviceAsync(tenantId, deviceId, clientId, contact.Id);
    }

    /// <summary>
    /// Keeps the name somebody typed into Request Support on their machine (2026-10-18). The latest wins; an empty box
    /// changes nothing. Returns the name and email kept, or empties when nothing changed.
    /// </summary>
    public async Task<(string Name, string Email)> RememberRequestedByAsync(int tenantId, string deviceId, string? typed,
        DateTime whenUtc, string? email = null)
    {
        var name = RemoteSupport.Shared.OwnerNames.Clean(typed);
        if (name.Length == 0) return ("", "");
        await using var db = await _dbf.CreateDbContextAsync();
        var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.DeviceId == deviceId);
        if (d == null) return ("", "");
        Given(d, name, email, whenUtc);
        await db.SaveChangesAsync();
        return (name, d.RequestedByEmail);
    }

    /// <summary>
    /// A name (and email) given for a machine — at a support request or by the installer window (2026-10-23) — kept as its
    /// <see cref="Device.RequestedBy"/>, the latest winning. The email always belongs to the name beside it: a different name
    /// with no email clears the old one (it was somebody else's); the SAME name with none keeps it — an older tray app sends
    /// no email, and the install's should not be lost to it.
    /// </summary>
    public static void Given(Device d, string name, string? email, DateTime whenUtc)
    {
        var clean = RemoteSupport.Shared.GivenPerson.CleanEmail(email);
        if (clean.Length > 0) d.RequestedByEmail = clean;
        else if (!string.Equals(d.RequestedBy, name, StringComparison.OrdinalIgnoreCase)) d.RequestedByEmail = "";
        d.RequestedBy = name;
        d.RequestedByUtc = DateTime.SpecifyKind(whenUtc, DateTimeKind.Utc);
    }

    /// <summary>
    /// The installer window's details from a hello (2026-10-23), kept ONCE per id: true when they were new here and are now
    /// the machine's name. Every hello repeats them, and repeating them back over a later support request's name would undo
    /// "the latest name wins".
    /// </summary>
    public static bool KeepInstalledFor(Device d, string? id, string? name, string? email, DateTime whenUtc)
    {
        var clean = RemoteSupport.Shared.OwnerNames.Clean(name);
        if (string.IsNullOrWhiteSpace(id) || clean.Length == 0 || d.InstalledForId == id) return false;
        d.InstalledForId = id.Length > 64 ? id[..64] : id;
        Given(d, clean, email, whenUtc);
        return true;
    }
}
