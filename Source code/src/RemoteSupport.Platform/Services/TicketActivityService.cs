using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Latest activity (2026-10-26, HANDOVER §164). Clint: "if one clicks on latest activity, it will show any updates on all
/// tickets as they are posted or when stages change." Every entry on every ticket the person may open, newest first —
/// comments, steps, the customer's replies, Back, assignments and the like, and new tickets — narrowed to their own
/// tickets, a client, or who posted it; and how many are new since they last looked, which the console's button and the
/// portal's tab carry live.
///
/// <para>The entries are the tickets' own stories (<see cref="TicketNote"/>) and the tickets themselves: nothing extra is
/// written to make this list, so nothing written anywhere can be missing from it. What is left out unless asked is what a
/// ticket writes by itself while something else happens — <see cref="TicketNote.Automatic"/>.</para>
///
/// <para>Who sees what is <see cref="TicketService.VisibleTo"/>, asked first, exactly as the ticket list asks it: this is a
/// new way to look at tickets, never a way to see more of them. A technician's "All" is their own work and the unassigned
/// pool, as their ticket list is.</para>
/// </summary>
public sealed class TicketActivityService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ClientService _clients;

    /// <summary>A page of the list: enough to read down without a wait, and "Show older" for the rest.</summary>
    public const int PageSize = 50;
    /// <summary>The longest a line's words are shown in the list; the ticket has the rest.</summary>
    public const int TextLength = 220;
    /// <summary>Before somebody has ever looked, their count starts this far back — enough to be worth opening, not a year.</summary>
    public static readonly TimeSpan FirstLook = TimeSpan.FromDays(1);
    /// <summary>A burst of entries (a step writes two or three) is told once — a count recomputed per entry is wasted work.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(700);

    public TicketActivityService(IDbContextFactory<PlatformDb> dbf, ClientService clients)
    {
        _dbf = dbf; _clients = clients;
        PlatformDb.TicketActivitySaved += Poke;
    }

    // ---- live ----

    /// <summary>
    /// Something new for a company — every count in it may have moved. Raised at most once per <see cref="Settle"/> per
    /// company, after the save. The hub pushes each console its count; the portal's live stream each open page its own.
    /// </summary>
    public event Action<int>? Changed;
    /// <summary>One person looked, or changed All/Mine: their own count (on every console and page of theirs) moved.</summary>
    public event Action<int, string>? SeenChanged;

    private readonly ConcurrentDictionary<int, byte> _pending = new();

    private void Poke(int tenantId)
    {
        if (!_pending.TryAdd(tenantId, 0)) return;
        _ = Task.Delay(Settle).ContinueWith(_ =>
        {
            _pending.TryRemove(tenantId, out byte _);
            try { Changed?.Invoke(tenantId); } catch { /* a listener's fault is its own */ }
        }, TaskScheduler.Default);
    }

    // ---- the list ----

    public sealed record Filter(bool Mine = false, string Client = "", int? ClientId = null, string Poster = "",
        bool Automatic = false, bool Own = false, DateTime? BeforeUtc = null, bool MarkSeen = false);

    public sealed class Page
    {
        public List<ActivityEntry> Entries { get; init; } = new();
        public bool More { get; init; }
        /// <summary>Everybody enabled in the company, for "Posted by".</summary>
        public List<string> People { get; init; } = new();
        /// <summary>When they had last looked, BEFORE this look — what <see cref="ActivityEntry.IsNew"/> was measured against.</summary>
        public DateTime SeenUtc { get; init; }
        /// <summary>A client was asked for by name and nothing matched it: say so rather than show an empty list as "nothing new".</summary>
        public bool NoSuchClient { get; init; }
    }

    /// <summary>
    /// One page, newest first. On the newest page with <see cref="Filter.MarkSeen"/>, this is the person looking: their
    /// count goes to nought from the moment the list was read (an entry saved while it was being read stays new), and
    /// their All/Mine is remembered.
    /// </summary>
    public async Task<Page> ListAsync(int tenantId, int technicianId, TenantRole role, string me, Filter f)
    {
        var asOf = DateTime.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        var person = technicianId > 0 ? await db.Technicians.FirstOrDefaultAsync(t => t.Id == technicianId && t.TenantId == tenantId) : null;
        var seen = person?.ActivitySeenUtc ?? asOf - FirstLook;

        // Which client, if any: by id (the portal's picker), or by words (the console) — the Clients page's own matching.
        List<int>? clientIds = null;
        if (f.ClientId is int cid) clientIds = new() { cid };
        else if (!string.IsNullOrWhiteSpace(f.Client))
        {
            clientIds = (await _clients.SearchAsync(tenantId, f.Client.Trim(), take: 500)).Select(c => c.Id).ToList();
            if (clientIds.Count == 0)
                return new Page { People = await PeopleAsync(db, tenantId), SeenUtc = seen, NoSuchClient = true };
        }

        var tickets = Scope(db, tenantId, role, me, f.Mine);
        if (clientIds != null) tickets = tickets.Where(t => t.ClientId != null && clientIds.Contains(t.ClientId.Value));
        var poster = (f.Poster ?? "").Trim();
        bool own = f.Own || poster == me;   // "Posted by: me" is asking for my own

        var notes = from n in db.TicketNotes
                    join t in tickets on n.TicketId equals t.Id
                    where n.TenantId == tenantId
                    select new { n, t.Number, t.Subject, t.ClientId, t.Id };
        if (!f.Automatic) notes = notes.Where(x => !x.n.Automatic);
        if (poster.Length > 0) notes = notes.Where(x => x.n.Author == poster);
        if (!own && me.Length > 0) notes = notes.Where(x => x.n.Author != me);
        if (f.BeforeUtc is DateTime before) notes = notes.Where(x => x.n.CreatedUtc < before);
        var noteRows = await notes.OrderByDescending(x => x.n.CreatedUtc).ThenByDescending(x => x.n.Id).Take(PageSize + 1).ToListAsync();

        var opened = tickets;
        if (poster.Length > 0) opened = opened.Where(t => t.OpenedBy == poster);
        if (!own && me.Length > 0) opened = opened.Where(t => t.OpenedBy != me);
        if (f.BeforeUtc is DateTime before2) opened = opened.Where(t => t.CreatedUtc < before2);
        var ticketRows = await opened.OrderByDescending(t => t.CreatedUtc).ThenByDescending(t => t.Id).Take(PageSize + 1).ToListAsync();

        // The two lists, merged newest first, one page of them.
        var merged = noteRows.Select(x => (At: x.n.CreatedUtc, Note: (TicketNote?)x.n, Ticket: (Ticket?)null, x.Number, x.Subject, x.ClientId, x.Id))
            .Concat(ticketRows.Select(t => (At: t.CreatedUtc, Note: (TicketNote?)null, Ticket: (Ticket?)t, t.Number, t.Subject, t.ClientId, t.Id)))
            .OrderByDescending(r => r.At).ToList();
        bool more = merged.Count > PageSize;
        var page = merged.Take(PageSize).ToList();

        var ids = page.Where(r => r.ClientId != null).Select(r => r.ClientId!.Value).Distinct().ToList();
        var names = ids.Count == 0 ? new Dictionary<int, string>()
            : await db.Clients.Where(c => c.TenantId == tenantId && ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
        var noteIds = page.Where(r => r.Note != null).Select(r => r.Note!.Id).ToList();
        var files = noteIds.Count == 0 ? new Dictionary<long, int>()
            : await db.TicketAttachments.Where(a => a.TenantId == tenantId && a.NoteId != null && noteIds.Contains(a.NoteId.Value))
                .GroupBy(a => a.NoteId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N);
        var askedMe = noteIds.Count == 0 || me.Length == 0 ? new HashSet<long>()
            : (await db.TicketMentions.Where(m => m.TenantId == tenantId && m.Person == me && noteIds.Contains(m.NoteId))
                .Select(m => m.NoteId).ToListAsync()).ToHashSet();
        // A new ticket shows who is on it NOW (nothing else records who it was booked to). Given to me only later, the
        // later hand-off line is the one that says "assigned it to you" — the walk found a ticket opened for Tim saying it.
        var openedIds = page.Where(r => r.Ticket != null).Select(r => r.Id).ToList();
        var handedLater = openedIds.Count == 0 || me.Length == 0 ? new HashSet<int>()
            : (await db.TicketNotes.Where(n => n.TenantId == tenantId && openedIds.Contains(n.TicketId) && n.IsSystem && !n.Automatic)
                .Select(n => new { n.TicketId, n.Body }).ToListAsync())
                .Where(n => AssignsTo(n.Body, me)).Select(n => n.TicketId).ToHashSet();

        var entries = page.Select(r =>
        {
            var e = r.Note != null ? FromNote(r.Note, me, askedMe.Contains(r.Note.Id), files.TryGetValue(r.Note.Id, out var nf) ? nf : 0)
                                   : FromTicket(r.Ticket!, me, handedLater.Contains(r.Id));
            e.TicketId = r.Id; e.TicketNumber = r.Number; e.Subject = r.Subject;
            e.Client = r.ClientId is int c && names.TryGetValue(c, out var cn) ? cn : "";
            e.IsNew = r.At > seen;
            return e;
        }).ToList();

        if (f.MarkSeen && f.BeforeUtc == null && person != null)
        {
            person.ActivitySeenUtc = asOf;
            person.ActivityMine = f.Mine;
            await db.SaveChangesAsync();
            try { SeenChanged?.Invoke(tenantId, person.DisplayName); } catch { /* the look is saved; the badge catches up */ }
        }
        return new Page { Entries = entries, More = more, People = await PeopleAsync(db, tenantId), SeenUtc = seen };
    }

    /// <summary>
    /// How many entries are new since this person last looked, in what they chose (All or Mine) — never their own posts,
    /// never automatic lines. At most <see cref="ActivityWords.MostCounted"/> + 1, which the badge says as "99+".
    /// </summary>
    public async Task<(int Count, bool Mine)> NewCountAsync(int tenantId, int technicianId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var p = await db.Technicians.AsNoTracking().FirstOrDefaultAsync(t => t.Id == technicianId && t.TenantId == tenantId && !t.Disabled);
        if (p == null) return (0, false);
        var since = p.ActivitySeenUtc ?? DateTime.UtcNow - FirstLook;
        var me = p.DisplayName;
        var tickets = Scope(db, tenantId, p.Role, me, p.ActivityMine);
        int cap = ActivityWords.MostCounted + 1;
        int notes = await (from n in db.TicketNotes
                           join t in tickets on n.TicketId equals t.Id
                           where n.TenantId == tenantId && n.CreatedUtc > since && !n.Automatic && n.Author != me
                           select n.Id).Take(cap).CountAsync();
        int opened = notes >= cap ? 0 : await tickets.Where(t => t.CreatedUtc > since && t.OpenedBy != me).Take(cap).CountAsync();
        return (Math.Min(cap, notes + opened), p.ActivityMine);
    }

    /// <summary>
    /// The tickets this person may see — <see cref="TicketService.VisibleTo"/>, the boundary — and, for Mine, the ones that
    /// are theirs: on it, worked on it, opened it, or asked something on it. Mine narrows; it never widens.
    /// </summary>
    public static IQueryable<Ticket> Scope(PlatformDb db, int tenantId, TenantRole role, string me, bool mine)
    {
        var q = TicketService.VisibleTo(db.Tickets.Where(t => t.TenantId == tenantId), role, me);
        if (!mine) return q;
        if (string.IsNullOrWhiteSpace(me)) return q.Where(t => false);
        var tag = Assignees.Tag(me);
        return q.Where(t => (Assignees.Separator + t.AssignedTo + Assignees.Separator).Contains(tag)
                         || (Assignees.Separator + t.WorkedBy + Assignees.Separator).Contains(tag)
                         || t.OpenedBy == me
                         || t.Mentions.Any(m => m.Person == me));
    }

    private static Task<List<string>> PeopleAsync(PlatformDb db, int tenantId) =>
        db.Technicians.Where(t => t.TenantId == tenantId && !t.Disabled).OrderBy(t => t.DisplayName).Select(t => t.DisplayName).ToListAsync();

    // ---- what one line says ----

    /// <summary>One entry of a ticket's story, as a line of the list. The one place that decides what kind it is.</summary>
    public static ActivityEntry FromNote(TicketNote n, string me, bool askedMe, int files)
    {
        var e = new ActivityEntry
        {
            Key = "n" + n.Id, NoteId = n.Id, AtUtc = n.CreatedUtc, Who = n.Author, Files = files,
            Text = Shorten(n.Body), Undone = n.UndoneUtc != null
        };
        if (n.FromCustomer) { e.Kind = ActivityKinds.Customer; e.Headline = "Customer replied"; }
        else if (n.WentBack) { e.Kind = ActivityKinds.Back; e.Headline = n.MovedTo.Length > 0 ? $"Went back to {StageName(n.MovedTo)}" : "Went back a step"; }
        else if (n.Automatic) { e.Kind = ActivityKinds.Automatic; e.Headline = "Automatic"; }
        else if (n.MovedTo.Length > 0)
        {
            e.Kind = ActivityKinds.Step;
            e.Headline = n.Stage.Length > 0 && n.Stage != n.MovedTo ? $"{StageName(n.Stage)} → {StageName(n.MovedTo)}" : $"Moved to {StageName(n.MovedTo)}";
        }
        else if (n.FormTitle.Length > 0) { e.Kind = ActivityKinds.Step; e.Headline = n.FormTitle; }
        else if (n.IsWorkDone) { e.Kind = ActivityKinds.Step; e.Headline = "Work done"; }
        else if (n.IsSystem) { e.Kind = ActivityKinds.Change; e.Headline = "Updated"; }
        else { e.Kind = ActivityKinds.Comment; e.Headline = files > 0 && n.Body.Trim().Length == 0 ? "Files" : "Comment"; }

        if (askedMe) e.ForYou = "asked you";
        else if (e.Kind == ActivityKinds.Change && AssignsTo(n.Body, me)) e.ForYou = "assigned it to you";
        return e;
    }

    /// <summary>A new ticket, as a line of the list.</summary>
    /// <param name="handedToMeLater">A hand-off line on this ticket gave it to <paramref name="me"/>: that line says so, not this one.</param>
    public static ActivityEntry FromTicket(Ticket t, string me, bool handedToMeLater = false)
    {
        // Who is on it NOW — what the ticket holds; who it was booked to is in its own "opened" line.
        var on = Assignees.Parse(t.AssignedTo);
        var words = TicketService.CategoryName(t.Category) + " ticket" + (on.Count > 0 ? $", on it now: {Assignees.Say(on)}" : ", nobody on it yet");
        return new ActivityEntry
        {
            Key = "t" + t.Id, AtUtc = t.CreatedUtc, Who = t.OpenedBy, Kind = ActivityKinds.Opened, Headline = "New ticket",
            Text = Shorten(words + (t.Description.Trim().Length > 0 ? ". " + t.Description : "")),
            ForYou = me.Length > 0 && t.OpenedBy != me && !handedToMeLater && Assignees.Has(t.AssignedTo, me) ? "assigned it to you" : ""
        };
    }

    private static string StageName(string key) => Workflow.Label(key) is { Length: > 0 } l ? l : key;

    /// <summary>The first lines of what was written, joined, cut to <see cref="TextLength"/> at a word.</summary>
    public static string Shorten(string? body)
    {
        var lines = (body ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3);
        var s = string.Join(" · ", lines);
        if (s.Length <= TextLength) return s;
        int cut = s.LastIndexOf(' ', TextLength);
        return s[..(cut > TextLength / 2 ? cut : TextLength)].TrimEnd(' ', '·', ',') + "…";
    }

    // The ticket's own words for a hand-off (TicketService.UpdateAsync): "reassigned from Sue to Ryan", "assigned to Ryan
    // (was the billing queue)", "added Ryan and Sue to the ticket". Clint, 2026-10-26: "i assign a ticket to Ryan, it shows
    // up in his recent activity?" — it does, and it says so. Read from the words because those ARE the record of it; a
    // display name may not contain a comma (§49), so ", " always ends a name list.
    private static readonly Regex HandedTo = new(
        @"(?:^|, )(?:reassigned from .+? to (?<n>[^,]+?)|assigned to (?<n>.+?) \(was [^)]*\)|added (?<n>.+?) to the ticket)(?=, |$)",
        RegexOptions.CultureInvariant);

    /// <summary>Whether a ticket's change line gave the ticket to this person.</summary>
    public static bool AssignsTo(string? body, string me)
    {
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(me)) return false;
        foreach (Match m in HandedTo.Matches(body))
        {
            var names = m.Groups["n"].Value.Split(new[] { ", ", " and " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Contains(me.Trim())) return true;
        }
        return false;
    }
}
