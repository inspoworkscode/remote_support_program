using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>Customer records, plus the type-ahead that stops staff creating duplicates.</summary>
public sealed partial class ClientService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly IDataProtector _protector;

    public ClientService(IDbContextFactory<PlatformDb> dbf, IDataProtectionProvider dp)
    {
        _dbf = dbf;
        // device passwords are encrypted at rest; a copy of the database is not a copy of the passwords
        _protector = dp.CreateProtector("RemoteSupport.DevicePassword.v1");
    }

    public string Protect(string plain) => string.IsNullOrEmpty(plain) ? "" : _protector.Protect(plain);
    public string Unprotect(string cipher)
    { if (string.IsNullOrEmpty(cipher)) return ""; try { return _protector.Unprotect(cipher); } catch { return "(cannot decrypt)"; } }

    public async Task<List<Client>> SearchAsync(int tenantId, string term, int take = 12)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Clients.Where(c => c.TenantId == tenantId && !c.Archived);
        if (!string.IsNullOrWhiteSpace(term))
        {
            // SQLite's instr() is case-sensitive, so a plain Contains made "ro" miss "Roger Rabbit"
            // — which defeated the type-ahead's whole purpose of catching the duplicate before it
            // is created. Compare in lower case so what staff type matches what is on file.
            var s = term.Trim().ToLower();
            q = q.Where(c => c.Name.ToLower().Contains(s) || c.ContactName.ToLower().Contains(s)
                          || c.Phone.ToLower().Contains(s) || c.Email.ToLower().Contains(s));
        }
        return await q.OrderBy(c => c.Name).Take(take).ToListAsync();
    }

    /// <summary>
    /// The client picker (wwwroot/clientpick.js, 2026-10-02 — Clint: "a search bar that works the same
    /// as client search so that when typing the client it automatically narrows"). The SAME matching
    /// as <see cref="SearchAsync"/>; with <paramref name="withPeople"/> (whose machine is this) each
    /// company comes with its people, and somebody typed by their own name brings their company.
    /// </summary>
    public async Task<List<(Client client, List<Contact> people)>> PickAsync(int tenantId, string term, bool withPeople, int take)
    {
        var found = await SearchAsync(tenantId, term, take);
        if (!withPeople) return found.Select(c => (c, new List<Contact>())).ToList();
        await using var db = await _dbf.CreateDbContextAsync();
        var ids = found.Select(c => c.Id).ToList();
        var s = (term ?? "").Trim().ToLower();
        var people = await db.Contacts
            .Where(p => p.TenantId == tenantId && !p.Archived && (ids.Contains(p.ClientId) || (s.Length > 0 && p.Name.ToLower().Contains(s))))
            .OrderBy(p => p.Name).ToListAsync();
        var more = people.Select(p => p.ClientId).Where(id => !ids.Contains(id)).Distinct().ToList();
        if (more.Count > 0)
            found.AddRange(await db.Clients.Where(c => c.TenantId == tenantId && !c.Archived && more.Contains(c.Id)).ToListAsync());
        return found.OrderBy(c => c.Name).Take(take)
            .Select(c => (c, people.Where(p => p.ClientId == c.Id).ToList())).ToList();
    }

    public async Task<List<Client>> AllAsync(int tenantId, bool includeArchived = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Clients.Where(c => c.TenantId == tenantId);
        if (!includeArchived) q = q.Where(c => !c.Archived);
        return await q.OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<Client?> GetAsync(int tenantId, int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Clients.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId);
    }

    /// <summary>Finds an existing client by exact name (case-insensitive) so re-entry does not duplicate.</summary>
    public async Task<Client?> ByNameAsync(int tenantId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Clients.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Name.ToLower() == n.ToLower());
    }

    public async Task<(Client? client, string error)> CreateAsync(int tenantId, string name, string contact, string phone, string email, string address, string notes)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, "Client name is required.");
        var existing = await ByNameAsync(tenantId, name);
        if (existing != null) return (existing, "");   // reuse rather than duplicate
        await using var db = await _dbf.CreateDbContextAsync();
        var c = new Client
        {
            TenantId = tenantId, Name = name.Trim(), ContactName = (contact ?? "").Trim(),
            Phone = (phone ?? "").Trim(), Email = (email ?? "").Trim(),
            Address = (address ?? "").Trim(), Notes = (notes ?? "").Trim()
        };
        db.Clients.Add(c);
        await db.SaveChangesAsync();
        return (c, "");
    }

    public async Task<string> UpdateAsync(int tenantId, int id, Action<Client> apply)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var c = await db.Clients.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (c == null) return "Client not found.";
        apply(c);
        await db.SaveChangesAsync();
        return "";
    }

    public async Task<(int tickets, int openTickets)> TicketCountsAsync(int tenantId, int clientId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var all = await db.Tickets.CountAsync(t => t.TenantId == tenantId && t.ClientId == clientId);
        var open = await db.Tickets.CountAsync(t => t.TenantId == tenantId && t.ClientId == clientId
                                                    && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);
        return (all, open);
    }
}
