using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Somebody from outside — a browser support caller (3.8.2) or a website chat visitor (3.8.3) —
/// becoming a Contact. One rule for both, so a person who chats on Monday and asks for browser
/// support on Tuesday is the same contact, not two.
///
/// Clint, for browser support: "yes definitely" — they become a Contact. Found by their email or
/// phone if the company already knows them (their name and client on file win); otherwise under the
/// company they named, made if it is new, or under their own name when they named none. Never
/// duplicated: the services reuse a client or contact of the same name.
/// </summary>
public sealed class Visitors
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ClientService _clients;
    private readonly ContactService _contacts;
    public Visitors(IDbContextFactory<PlatformDb> dbf, ClientService clients, ContactService contacts)
    { _dbf = dbf; _clients = clients; _contacts = contacts; }

    /// <param name="clientNote">Written on a client this makes.</param>
    /// <param name="contactNote">Written on a contact this makes.</param>
    public async Task<(int? clientId, int? contactId, string contactName)> FindOrCreateAsync(int tenantId,
        string name, string company, string phone, string email, string clientNote, string contactNote)
    {
        name = name.Trim(); company = company.Trim(); phone = phone.Trim(); email = email.Trim();
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var mail = email.ToLower();
            var known = mail.Length == 0 ? null : await db.Contacts.Where(c => c.TenantId == tenantId && !c.Archived && c.Email.ToLower() == mail)
                .OrderByDescending(c => c.Id).FirstOrDefaultAsync();
            // A phone is compared by its DIGITS, and by the last nine when both have that many:
            // "082 555 1234", "0825551234" and "+27 82 555 1234" are one person. Comparing the text
            // (the first version, and browser support's before it) made a second contact for each
            // way of typing the same number — found by webchatcheck.
            var mine = Digits(phone);
            if (known == null && mine.Length >= 7)
                known = (await db.Contacts.Where(c => c.TenantId == tenantId && !c.Archived && c.Phone != "").OrderByDescending(c => c.Id).ToListAsync())
                    .FirstOrDefault(c => SamePhone(Digits(c.Phone), mine));
            if (known != null) return (known.ClientId, known.Id, known.Name);
        }
        var (client, _) = await _clients.CreateAsync(tenantId, company.Length > 0 ? company : name, name, phone, email, "", clientNote);
        if (client == null) return (null, null, name);
        var (contact, _) = await _contacts.CreateAsync(tenantId, client.Id, name, phone, email, "", contactNote);
        return (client.Id, contact?.Id, contact?.Name ?? name);
    }

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    /// <summary>The same number however it was typed: equal digits, or the same last nine.</summary>
    public static bool SamePhone(string a, string b) =>
        a.Length > 0 && (a == b || (a.Length >= 9 && b.Length >= 9 && a[^9..] == b[^9..]));
}
