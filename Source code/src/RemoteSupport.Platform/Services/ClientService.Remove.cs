using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Removing a client (2026-10-27, HANDOVER §172). Clint: <i>"One of my staff created a client with the
/// incorrect spelling and the proper client already exists."</i> So removing is really MOVING: everything on
/// the wrong client — its tickets, machines, people, knowledge base, recurring tasks, website chats and health
/// check history — goes onto the right one, and only then is the wrong one deleted. Nothing is lost, so nothing
/// needs to be kept to undo it. A client with nothing on it is simply deleted.
///
/// <para>Rules, all here:</para>
/// <list type="bullet">
/// <item>Who: <see cref="Permissions.CanRemoveClients"/>, asked here as well as on the page.</item>
/// <item>A client with anything on it is refused unless the client it goes to is named — never deleted with
///       its work, and never left with its machines unlinked.</item>
/// <item>The right client keeps its own details; the wrong one only fills its BLANKS (the import's rule), and
///       its notes are added under the right one's.</item>
/// <item>Its health check tick is the right client's: the machines moved follow it. Changing what is installed
///       on a customer's PCs because of a spelling mistake would be a surprise either way.</item>
/// <item>A person on both (same name, or the same email) becomes ONE person; their machines, tickets and chats
///       follow. Anybody else moves as they are, archived ones too.</item>
/// <item>A knowledge base folder at the top with a name the right client already has says where it came from,
///       as removing a folder does.</item>
/// <item>Each moved ticket's story says so, as an automatic line — not in Latest activity, and not "updated".</item>
/// <item>One save: it all moves or none of it does.</item>
/// </list>
/// </summary>
public sealed partial class ClientService
{
    /// <summary>What is on a client — what would move if it were removed.</summary>
    public sealed record OnClient(int Tickets, int Machines, int People, int Articles, int Folders, int Schedules, int Chats, int HealthChecks)
    {
        public bool Any => Tickets + Machines + People + Articles + Folders + Schedules + Chats + HealthChecks > 0;

        /// <summary>"3 tickets, 2 machines and 1 person", or "" for nothing. Only what there is.</summary>
        public string Say()
        {
            var bits = new List<string>();
            void Add(int n, string one, string many) { if (n > 0) bits.Add($"{n} {(n == 1 ? one : many)}"); }
            Add(Tickets, "ticket", "tickets");
            Add(Machines, "machine", "machines");
            Add(People, "person", "people");
            Add(Articles, "knowledge base article", "knowledge base articles");
            Add(Folders, "knowledge base folder", "knowledge base folders");
            Add(Schedules, "recurring task", "recurring tasks");
            Add(Chats, "website chat", "website chats");
            Add(HealthChecks, "health check report", "health check reports");
            return bits.Count switch
            {
                0 => "",
                1 => bits[0],
                _ => string.Join(", ", bits.Take(bits.Count - 1)) + " and " + bits[^1]
            };
        }
    }

    /// <summary>A machine whose owner changed, for the hub to be told (<c>TenantHub.RefreshDeviceOwner</c>).</summary>
    public readonly record struct MovedMachine(string DeviceId, ContactService.DeviceOwner Owner);

    /// <summary>What a removal did. <see cref="Into"/> is null when the client had nothing on it.</summary>
    public sealed record Removal(string Name, Client? Into, OnClient Moved, List<MovedMachine> Machines);

    public async Task<OnClient> WhatIsOnAsync(int tenantId, int clientId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await CountAsync(db, tenantId, clientId);
    }

    private static async Task<OnClient> CountAsync(PlatformDb db, int tenantId, int id) => new(
        await db.Tickets.CountAsync(t => t.TenantId == tenantId && t.ClientId == id),
        await db.Devices.CountAsync(d => d.TenantId == tenantId && d.ClientId == id),
        await db.Contacts.CountAsync(c => c.TenantId == tenantId && c.ClientId == id),
        await db.KbArticles.CountAsync(a => a.TenantId == tenantId && a.ClientId == id && a.DeletedUtc == null),
        await db.KbFolders.CountAsync(f => f.TenantId == tenantId && f.ClientId == id),
        await db.RecurringTasks.CountAsync(r => r.TenantId == tenantId && r.ClientId == id),
        await db.WebChats.CountAsync(w => w.TenantId == tenantId && w.ClientId == id),
        await db.HealthCheckRuns.CountAsync(h => h.TenantId == tenantId && h.ClientId == id));

    /// <summary>
    /// Removes <paramref name="clientId"/>, moving everything on it to <paramref name="intoId"/> first. Error is
    /// empty on success.
    /// </summary>
    public async Task<(Removal? done, string error)> RemoveAsync(int tenantId, TenantRole role, int clientId, int? intoId, string who)
    {
        if (!Permissions.CanRemoveClients(role))
            return (null, "Only the owner, a supervisor or an office administrator can remove a client.");
        await using var db = await _dbf.CreateDbContextAsync();
        var gone = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId && c.TenantId == tenantId);
        if (gone == null) return (null, "That client is not on file any more.");
        var on = await CountAsync(db, tenantId, clientId);

        Client? into = null;
        if (intoId is int iid && iid > 0)
        {
            if (iid == clientId) return (null, $"Choose a different client to move {gone.Name}'s work to — not {gone.Name} itself.");
            into = await db.Clients.FirstOrDefaultAsync(c => c.Id == iid && c.TenantId == tenantId && !c.Archived);
            if (into == null) return (null, "The client you chose to move it to is not on file.");
        }
        else if (on.Any)
            return (null, $"{gone.Name} has {on.Say()}. Choose the client to move them to, and then remove it.");

        var machines = new List<MovedMachine>();
        if (into != null)
        {
            int to = into.Id;

            // ---- the client's own details: the right one's stand; the wrong one fills blanks ----
            if (into.ContactName.Length == 0) into.ContactName = gone.ContactName;
            if (into.Phone.Length == 0) into.Phone = gone.Phone;
            if (into.Email.Length == 0) into.Email = gone.Email;
            if (into.Address.Length == 0) into.Address = gone.Address;
            if (gone.Notes.Length > 0 && !into.Notes.Contains(gone.Notes, StringComparison.OrdinalIgnoreCase))
                into.Notes = into.Notes.Length == 0 ? gone.Notes : $"{into.Notes}\n\nFrom {gone.Name}: {gone.Notes}";

            // ---- people: one person who is on both is kept once ----
            var theirs = await db.Contacts.Where(c => c.TenantId == tenantId && c.ClientId == to).ToListAsync();
            var same = new Dictionary<int, Contact>();      // the wrong client's contact id → the right client's person
            foreach (var p in await db.Contacts.Where(c => c.TenantId == tenantId && c.ClientId == clientId).ToListAsync())
            {
                var twin = theirs.FirstOrDefault(x => string.Equals(x.Name.Trim(), p.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? (p.Email.Trim().Length == 0 ? null
                            : theirs.FirstOrDefault(x => string.Equals(x.Email.Trim(), p.Email.Trim(), StringComparison.OrdinalIgnoreCase)));
                if (twin == null) { p.ClientId = to; theirs.Add(p); continue; }
                if (twin.Phone.Length == 0) twin.Phone = p.Phone;
                if (twin.Email.Length == 0) twin.Email = p.Email;
                if (twin.Role.Length == 0) twin.Role = p.Role;
                if (twin.Notes.Length == 0) twin.Notes = p.Notes;
                if (twin.Archived && !p.Archived) twin.Archived = false;
                same[p.Id] = twin;
                db.Contacts.Remove(p);
            }
            var merged = same.Keys.ToList();
            int? Person(int? contactId) => contactId is int c && same.TryGetValue(c, out var t) ? t.Id : contactId;

            // ---- machines, each told to the hub afterwards ----
            foreach (var d in await db.Devices.Where(d => d.TenantId == tenantId && d.ClientId == clientId).ToListAsync())
            {
                d.ClientId = to;
                d.ContactId = Person(d.ContactId);
                var person = d.ContactId is int pc ? theirs.FirstOrDefault(x => x.Id == pc) : null;
                machines.Add(new MovedMachine(d.DeviceId, new ContactService.DeviceOwner(to, into.Name, person?.Id, person?.Name ?? "")));
            }
            // ---- tickets, each with a line in its story ----
            foreach (var t in await db.Tickets.Where(t => t.TenantId == tenantId && (t.ClientId == clientId
                         || (t.ContactId != null && merged.Contains(t.ContactId.Value)))).ToListAsync())
            {
                t.ContactId = Person(t.ContactId);
                if (t.ClientId != clientId) continue;
                t.ClientId = to;
                db.TicketNotes.Add(new TicketNote
                {
                    TenantId = tenantId, TicketId = t.Id, Author = who, IsSystem = true, Automatic = true, Stage = t.Stage,
                    Body = $"Moved from {gone.Name} to {into.Name} — {gone.Name} was removed."
                });
            }

            foreach (var w in await db.WebChats.Where(w => w.TenantId == tenantId && (w.ClientId == clientId
                         || (w.ContactId != null && merged.Contains(w.ContactId.Value)))).ToListAsync())
            {
                w.ContactId = Person(w.ContactId);
                if (w.ClientId == clientId) w.ClientId = to;
            }

            // ---- the knowledge base: articles (deleted ones too, so a restore lands right), their history, folders ----
            foreach (var a in await db.KbArticles.Where(a => a.TenantId == tenantId && a.ClientId == clientId).ToListAsync())
                a.ClientId = to;
            foreach (var r in await db.KbRevisions.Where(r => r.TenantId == tenantId && r.ClientId == clientId).ToListAsync())
                r.ClientId = to;
            var tops = (await db.KbFolders.Where(f => f.TenantId == tenantId && f.ClientId == to && f.ParentId == null)
                .Select(f => f.Name).ToListAsync()).Select(n => n.ToLowerInvariant()).ToHashSet();
            var from = $" (from {gone.Name})";
            foreach (var f in await db.KbFolders.Where(f => f.TenantId == tenantId && f.ClientId == clientId).ToListAsync())
            {
                f.ClientId = to;
                if (f.ParentId == null && tops.Contains(f.Name.ToLowerInvariant()))
                    f.Name = (f.Name.Length + from.Length > KnowledgeBaseService.MaxFolderName
                        ? f.Name[..Math.Max(1, KnowledgeBaseService.MaxFolderName - from.Length)].TrimEnd() : f.Name) + from;
            }

            foreach (var r in await db.RecurringTasks.Where(r => r.TenantId == tenantId && r.ClientId == clientId).ToListAsync())
                r.ClientId = to;
            foreach (var h in await db.HealthCheckRuns.Where(h => h.TenantId == tenantId && h.ClientId == clientId).ToListAsync())
                h.ClientId = to;
        }

        db.Clients.Remove(gone);
        await db.SaveChangesAsync();
        return (new Removal(gone.Name, into, on, machines), "");
    }
}
