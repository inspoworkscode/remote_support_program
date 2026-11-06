using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The knowledge base (roadmap 3.8.5, Clint 2026-10-05). EVERY rule is here — who may write, delete
/// and write a ticket up, what a ticket's write-up starts as, which client an article belongs to, the
/// history — and the portal's pages and the console's hub only ask.
///
/// <para>His decisions, through "go with your recommendations": a General section and a Clients
/// section, ONE client per article; everybody may read, write and edit, every save is kept and can be
/// put back, only a supervisor or the owner deletes; a ticket offers "Add to knowledge base" from the
/// moment its work is recorded as done, to the people on it and supervisors and the owner; a ticket
/// for a client with articles says so, as does the console when you connect to that client's machine;
/// pictures pasted straight in; never a password; staff only.</para>
/// </summary>
public sealed class KnowledgeBaseService
{
    public const int MaxTitle = 200;
    /// <summary>Generous — a long procedure with many commands — and still a page a browser draws at once.</summary>
    public const int MaxBody = 200_000;
    /// <summary>A screenshot, after the editor has made it smaller as workshop photos are.</summary>
    public const long MaxImageBytes = 12 * 1024 * 1024;
    private static readonly string[] ImageTypes = { "image/jpeg", "image/png", "image/webp" };

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ILogger<KnowledgeBaseService> _log;
    private readonly string _root;

    public KnowledgeBaseService(IDbContextFactory<PlatformDb> dbf, ILogger<KnowledgeBaseService> log, string dataDir)
    {
        _dbf = dbf; _log = log;
        _root = Path.Combine(dataDir, "knowledge");
        Directory.CreateDirectory(_root);
    }

    /// <summary>Somebody saved, restored or deleted an article. The console's open windows may want to know.</summary>
    public event Action<int /*tenantId*/, int? /*clientId*/>? Changed;

    // ================================================================== reading

    public sealed record Listed(KbArticle Article, string ClientName, string Snippet);

    /// <summary>
    /// The articles, narrowed by every word typed (title, words or client name, any case), and by
    /// section: <paramref name="clientId"/> null is everything, 0 is General, anything else one client.
    /// Titles that match come first; then by title.
    /// </summary>
    public async Task<List<Listed>> SearchAsync(int tenantId, string? search, int? clientId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.KbArticles.Where(a => a.TenantId == tenantId && a.DeletedUtc == null);
        if (clientId == 0) q = q.Where(a => a.ClientId == null);
        else if (clientId > 0) q = q.Where(a => a.ClientId == clientId);
        var words = Words(search);
        foreach (var w in words)
            q = q.Where(a => a.Title.ToLower().Contains(w) || a.Body.ToLower().Contains(w)
                             || (a.Client != null && a.Client.Name.ToLower().Contains(w)));
        var rows = await q.Select(a => new { a, Client = a.Client != null ? a.Client.Name : "" }).ToListAsync();
        return rows
            .OrderByDescending(r => words.Count > 0 && words.All(w => r.a.Title.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(r => r.a.Title, StringComparer.OrdinalIgnoreCase)
            .Select(r => new Listed(r.a, r.Client, KbText.Snippet(r.a.Body)))
            .ToList();
    }

    private static List<string> Words(string? s) =>
        (s ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().Take(8).ToList();

    public async Task<int> CountForClientAsync(int tenantId, int clientId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.KbArticles.CountAsync(a => a.TenantId == tenantId && a.ClientId == clientId && a.DeletedUtc == null);
    }

    public async Task<KbArticle?> GetAsync(int tenantId, int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.KbArticles.Include(a => a.Client)
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId && a.DeletedUtc == null);
    }

    /// <summary>Every save, newest first.</summary>
    public async Task<List<KbRevision>> HistoryAsync(int tenantId, int articleId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.KbRevisions.Where(r => r.TenantId == tenantId && r.ArticleId == articleId)
            .OrderByDescending(r => r.Version).ToListAsync();
    }

    public async Task<KbRevision?> RevisionAsync(int tenantId, int articleId, int version)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.KbRevisions.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.ArticleId == articleId && r.Version == version);
    }

    /// <summary>Several clients' names at once, by id — this company's only.</summary>
    public async Task<Dictionary<int, string>> ClientNamesAsync(int tenantId, IEnumerable<int> clientIds)
    {
        var ids = clientIds.Distinct().ToList();
        if (ids.Count == 0) return new();
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Clients.Where(c => c.TenantId == tenantId && ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
    }

    public async Task<string> ClientNameAsync(int tenantId, int clientId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Clients.Where(c => c.TenantId == tenantId && c.Id == clientId).Select(c => c.Name).FirstOrDefaultAsync() ?? "";
    }

    /// <summary>The client an address names by number (HANDOVER §159): 0 is General, −1 is none of this company's.</summary>
    public async Task<int> ClientIdOfNumberAsync(int tenantId, int number)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await PortalAddresses.ClientIdOrNoneAsync(db, tenantId, number);
    }

    /// <summary>Clients' numbers by id, for the links a page draws (HANDOVER §159). This company's only.</summary>
    public async Task<Dictionary<int, int>> ClientNumbersAsync(int tenantId, IEnumerable<int> clientIds)
    {
        var ids = clientIds.Where(i => i > 0).Distinct().ToList();
        if (ids.Count == 0) return new();
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Clients.Where(c => c.TenantId == tenantId && ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number);
    }

    // ================================================================== a ticket, written up

    /// <summary>
    /// Whether the work on a ticket is recorded as done: a Work done entry, the technician's decision
    /// on how the job ends (any <see cref="Ticket.Outcome"/>), or closed. Clint's answer 1 — a
    /// technician never closes anything, so waiting for Closed would hide the button from the very
    /// person who did the work.
    /// </summary>
    public static bool WorkIsDone(Ticket t, bool hasWorkDone) =>
        hasWorkDone || t.Outcome.Length > 0 || t.Status is TicketStatus.Resolved or TicketStatus.Closed;

    /// <summary>
    /// Whether this person is offered "Add to knowledge base" on this ticket: its work is done, and
    /// they are on it, did the work on it, or are a supervisor or the owner.
    /// </summary>
    public static bool Offered(Ticket t, bool hasWorkDone, TenantRole role, string me) =>
        WorkIsDone(t, hasWorkDone)
        && Permissions.CanWriteKnowledge(role)
        && (Permissions.CanWriteUpAnyTicket(role)
            || (me.Length > 0 && (Assignees.Has(t.AssignedTo, me) || Assignees.Has(t.WorkedBy, me))));

    /// <summary>What a ticket shows about the knowledge base. Drawn by the portal and sent to the console.</summary>
    public sealed record TicketKnowledge(int ClientId, string ClientName, int ClientCount,
        List<KbArticle> FromTicket, bool Offered, int ClientNumber = 0);

    public async Task<TicketKnowledge> ForTicketAsync(int tenantId, Ticket t, TenantRole role, string me)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        int count = 0, number = 0; string name = "";
        if (t.ClientId is int cid)
        {
            count = await db.KbArticles.CountAsync(a => a.TenantId == tenantId && a.ClientId == cid && a.DeletedUtc == null);
            name = await db.Clients.Where(c => c.Id == cid && c.TenantId == tenantId).Select(c => c.Name).FirstOrDefaultAsync() ?? "";
            number = await PortalAddresses.ClientNumberAsync(db, tenantId, cid);
        }
        var from = await db.KbArticles.Where(a => a.TenantId == tenantId && a.FromTicketId == t.Id && a.DeletedUtc == null)
            .OrderBy(a => a.Id).ToListAsync();
        var workDone = await db.TicketNotes.AnyAsync(n => n.TicketId == t.Id && n.IsWorkDone);
        return new TicketKnowledge(t.ClientId ?? 0, name, count, from, Offered(t, workDone, role, me), number);
    }

    /// <summary>The same, as the console's ticket window draws it.</summary>
    public async Task<KnowledgeInfo> InfoForTicketAsync(int tenantId, Ticket t, TenantRole role, string me)
    {
        var k = await ForTicketAsync(tenantId, t, role, me);
        return new KnowledgeInfo
        {
            ClientId = k.ClientId, ClientNumber = k.ClientNumber, ClientName = k.ClientName, ClientCount = k.ClientCount, CanAdd = k.Offered,
            FromTicket = k.FromTicket.Select(a => new KnowledgeRefInfo { Id = a.Id, Number = a.Number, Title = a.Title }).ToList()
        };
    }

    public sealed record Draft(string Title, string Body, int? ClientId, string ClientName, int? FromTicketId, int FromTicketNumber);

    /// <summary>
    /// What "Add to knowledge base" opens with: the ticket's subject as the title, its client, what
    /// was asked for, and what was recorded as done — each line of it a numbered step, because the
    /// point (Clint) is that it is then "edited with step by step instructions". Nothing is saved
    /// until they press Save, so pressing the button twice makes nothing twice.
    /// </summary>
    public async Task<(Draft? draft, string error)> DraftFromTicketAsync(int tenantId, int ticketId, TenantRole role, string me)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        // Scoped like every other read of a ticket: one they may not see is one that does not exist.
        var t = await TicketService.VisibleTo(db.Tickets.Where(x => x.TenantId == tenantId), role, me)
            .FirstOrDefaultAsync(x => x.Id == ticketId);
        if (t == null) return (null, "That ticket was not found.");
        var notes = await db.TicketNotes.Where(n => n.TicketId == t.Id && n.TenantId == tenantId).OrderBy(n => n.CreatedUtc).ToListAsync();
        if (!Offered(t, notes.Any(n => n.IsWorkDone), role, me))
            return (null, WorkIsDone(t, notes.Any(n => n.IsWorkDone))
                ? $"Only the people on #{t.Number}, a supervisor or the owner can write it up from the ticket. You can still write a new article."
                : $"#{t.Number} can be written up once its work is recorded as done.");

        string clientName = t.ClientId is int cid
            ? await db.Clients.Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync() ?? "" : "";

        var body = new System.Text.StringBuilder();
        var asked = t.Description.Trim();
        body.Append("# What was needed\n").Append(asked.Length > 0 ? asked : t.Subject).Append("\n\n");
        body.Append("# Step by step\n");
        int step = 1;
        foreach (var n in notes.Where(n => n.IsWorkDone))
            foreach (var line in TicketService.WorkDoneText(n.Body).Replace("\r", "").Split('\n'))
            {
                var s = System.Text.RegularExpressions.Regex.Replace(line.Trim(), @"^(\d{1,3}[.)]|[-*•])\s+", "");
                if (s.Length > 0) body.Append(step++).Append(". ").Append(s).Append('\n');
            }
        if (step == 1) body.Append("1. \n");
        return (new Draft(t.Subject, body.ToString().TrimEnd() + "\n", t.ClientId, clientName, t.Id, t.Number), "");
    }

    // ================================================================== writing

    /// <summary>The checks a save shares with a new article. Empty when it may go ahead.</summary>
    private static async Task<string> CheckAsync(PlatformDb db, int tenantId, string title, string body, int? clientId)
    {
        if (title.Length == 0) return "Give it a title — it is how people will find it.";
        if (title.Length > MaxTitle) return $"Keep the title under {MaxTitle} characters.";
        if (body.Trim().Length == 0) return "Write something in it first.";
        if (body.Length > MaxBody) return "That is too long for one article. Split it into two.";
        if (clientId is int cid && !await db.Clients.AnyAsync(c => c.Id == cid && c.TenantId == tenantId))
            return "That client was not found. Choose one from the list, or leave it empty for General.";
        return "";
    }

    private static int? ClientOrGeneral(int? clientId) => clientId is > 0 ? clientId : null;

    public async Task<(KbArticle? article, string error)> CreateAsync(int tenantId, string? title, string? body, int? clientId, int? folderId,
        int? fromTicketId, string who, TenantRole role)
    {
        if (!Permissions.CanWriteKnowledge(role)) return (null, "You cannot write in the knowledge base.");
        title = (title ?? "").Trim(); body = (body ?? "").Replace("\r\n", "\n").TrimEnd();
        clientId = ClientOrGeneral(clientId); folderId = folderId is > 0 ? folderId : null;
        await using var db = await _dbf.CreateDbContextAsync();
        var why = await CheckAsync(db, tenantId, title, body, clientId);
        if (why.Length == 0) why = await CheckFolderAsync(db, tenantId, folderId, clientId);
        if (why.Length > 0) return (null, why);

        Ticket? t = null;
        if (fromTicketId is > 0)
        {
            // Written up from a ticket: the same people the button is offered to, and the ticket is told.
            t = await TicketService.VisibleTo(db.Tickets.Where(x => x.TenantId == tenantId), role, who)
                .FirstOrDefaultAsync(x => x.Id == fromTicketId);
            if (t == null) return (null, "That ticket was not found.");
            if (!Offered(t, await db.TicketNotes.AnyAsync(n => n.TicketId == t.Id && n.IsWorkDone), role, who))
                return (null, $"#{t.Number} cannot be written up from the ticket by you. Save it as a new article instead.");
        }

        var a = new KbArticle
        {
            TenantId = tenantId, ClientId = clientId, FolderId = folderId, Title = title, Body = body,
            FromTicketId = t?.Id, FromTicketNumber = t?.Number ?? 0,
            CreatedBy = who, UpdatedBy = who
        };
        db.KbArticles.Add(a);
        await db.SaveChangesAsync();
        db.KbRevisions.Add(Revision(a, who, t != null ? $"Written up from #{t.Number}" : ""));
        if (t != null)
            db.TicketNotes.Add(new TicketNote
            {
                TenantId = tenantId, TicketId = t.Id, Author = who, IsSystem = true, Stage = t.Stage,
                Body = $"Written up in the knowledge base: {title}"
            });
        await db.SaveChangesAsync();
        _log.LogInformation("[{Tenant}] knowledge base: {Who} wrote #{Id} \"{Title}\"", tenantId, who, a.Id, title);
        Changed?.Invoke(tenantId, a.ClientId);
        return (a, "");
    }

    /// <summary>
    /// Saves an edit. <paramref name="version"/> is the one the editor opened: if somebody else has
    /// saved since, this is refused and nothing is lost — the editor keeps what was typed.
    /// </summary>
    public async Task<(KbArticle? article, string error)> SaveAsync(int tenantId, int id, int version, string? title, string? body,
        int? clientId, int? folderId, string who, TenantRole role)
    {
        if (!Permissions.CanWriteKnowledge(role)) return (null, "You cannot write in the knowledge base.");
        title = (title ?? "").Trim(); body = (body ?? "").Replace("\r\n", "\n").TrimEnd();
        clientId = ClientOrGeneral(clientId); folderId = folderId is > 0 ? folderId : null;
        await using var db = await _dbf.CreateDbContextAsync();
        var a = await db.KbArticles.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.DeletedUtc == null);
        if (a == null) return (null, "That article is not there any more — somebody may have deleted it.");
        if (a.Version != version) return (null, Clash(a));
        var why = await CheckAsync(db, tenantId, title, body, clientId);
        if (why.Length == 0) why = await CheckFolderAsync(db, tenantId, folderId, clientId);
        if (why.Length > 0) return (null, why);
        if (a.Title == title && a.Body == body && a.ClientId == clientId && a.FolderId == folderId) return (a, "");

        int? before = a.ClientId;
        a.Title = title; a.Body = body; a.ClientId = clientId; a.FolderId = folderId;
        a.Version++; a.UpdatedBy = who; a.UpdatedUtc = DateTime.UtcNow;
        db.KbRevisions.Add(Revision(a, who, ""));
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            // Two saves of the same version in the same instant: Version is a concurrency token, so
            // the second one lands here rather than over the first.
            var now = await GetAsync(tenantId, id);
            return (null, now == null ? "That article is not there any more." : Clash(now));
        }
        Changed?.Invoke(tenantId, a.ClientId);
        if (before != a.ClientId) Changed?.Invoke(tenantId, before);
        return (a, "");
    }

    private static string Clash(KbArticle now) =>
        $"{now.UpdatedBy} saved a change to this article at {now.UpdatedUtc.Local():HH:mm} while you were editing. " +
        "What you typed is still below: copy what you need, then open the article again and add it.";

    /// <summary>Puts an earlier version back, as a new version — so putting back is itself undoable.</summary>
    public async Task<string> RestoreAsync(int tenantId, int id, int version, string who, TenantRole role)
    {
        if (!Permissions.CanWriteKnowledge(role)) return "You cannot write in the knowledge base.";
        await using var db = await _dbf.CreateDbContextAsync();
        var a = await db.KbArticles.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.DeletedUtc == null);
        if (a == null) return "That article is not there any more.";
        var r = await db.KbRevisions.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ArticleId == id && x.Version == version);
        if (r == null) return "That version was not found.";
        if (r.Version == a.Version) return "That is the version it already has.";
        int? clientId = r.ClientId is int c && await db.Clients.AnyAsync(x => x.Id == c && x.TenantId == tenantId) ? c : null;
        // Back in the folder it was in, if that is still there and still in the same section; else
        // at the top of the section. Nothing older than folders had one: the top.
        int? folderId = r.FolderId is int f && await db.KbFolders.AnyAsync(x => x.Id == f && x.TenantId == tenantId && x.ClientId == clientId) ? f : null;
        int? before = a.ClientId;
        a.Title = r.Title; a.Body = r.Body; a.ClientId = clientId; a.FolderId = folderId;
        a.Version++; a.UpdatedBy = who; a.UpdatedUtc = DateTime.UtcNow;
        db.KbRevisions.Add(Revision(a, who, $"Put back version {r.Version}"));
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return "Somebody saved a change at the same moment. Open the history again."; }
        Changed?.Invoke(tenantId, a.ClientId);
        if (before != a.ClientId) Changed?.Invoke(tenantId, before);
        return "";
    }

    public async Task<string> DeleteAsync(int tenantId, int id, string who, TenantRole role)
    {
        if (!Permissions.CanDeleteKnowledge(role)) return "Only a supervisor or the owner can delete an article. You can edit it, and every version is kept.";
        await using var db = await _dbf.CreateDbContextAsync();
        var a = await db.KbArticles.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.DeletedUtc == null);
        if (a == null) return "That article is not there any more.";
        a.DeletedUtc = DateTime.UtcNow; a.DeletedBy = who;
        await db.SaveChangesAsync();
        _log.LogInformation("[{Tenant}] knowledge base: {Who} deleted #{Id} \"{Title}\"", tenantId, who, a.Id, a.Title);
        Changed?.Invoke(tenantId, a.ClientId);
        return "";
    }

    private static KbRevision Revision(KbArticle a, string who, string note) => new()
    {
        TenantId = a.TenantId, ArticleId = a.Id, Version = a.Version, Title = a.Title, ClientId = a.ClientId,
        FolderId = a.FolderId, Body = a.Body, SavedBy = who, SavedUtc = a.UpdatedUtc, Note = note
    };

    // ================================================================== folders

    // Clint, 2026-10-06: "on the knowledgebase, it would be nice to be able to create folders and
    // subfolders ... This must be under general and clients." A folder belongs to ONE section —
    // General, or one client — as does everything in it, so an article for Acme can never sit in a
    // folder of General's. Anybody who writes may make, rename and move one; nothing is ever lost by
    // removing one — what was in it moves up to where the folder was.

    public const int MaxFolderName = 80;
    /// <summary>Folders inside folders, this deep at most. Past that, nobody finds anything anyway.</summary>
    public const int MaxFolderDepth = 6;

    /// <summary>Every folder of the company, for drawing paths. A small table: one query.</summary>
    public async Task<List<KbFolder>> AllFoldersAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.KbFolders.Where(f => f.TenantId == tenantId).ToListAsync();
    }

    /// <summary>A section's folders — <paramref name="clientId"/> null or 0 is General.</summary>
    public async Task<List<KbFolder>> FoldersAsync(int tenantId, int? clientId)
    {
        var c = ClientOrGeneral(clientId);
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.KbFolders.Where(f => f.TenantId == tenantId && f.ClientId == c).ToListAsync();
    }

    public async Task<KbFolder?> GetFolderAsync(int tenantId, int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.KbFolders.FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId);
    }

    /// <summary>The folder and the ones it is inside, outermost first: General › Printers › HP.</summary>
    public static List<KbFolder> PathOf(IReadOnlyCollection<KbFolder> all, int? folderId)
    {
        var path = new List<KbFolder>();
        var byId = all.ToDictionary(f => f.Id);
        for (int? id = folderId; id is int i && byId.TryGetValue(i, out var f) && path.Count <= MaxFolderDepth * 2; id = f.ParentId)
        {
            if (path.Contains(f)) break;   // never loop, whatever the rows say
            path.Insert(0, f);
        }
        return path;
    }

    /// <summary>"Printers › HP" — the folder's path within its section, or empty at the top.</summary>
    public static string PathText(IReadOnlyCollection<KbFolder> all, int? folderId) =>
        string.Join(" › ", PathOf(all, folderId).Select(f => f.Name));

    /// <summary>The folder and every folder inside it, however deep.</summary>
    public static HashSet<int> WithInside(IReadOnlyCollection<KbFolder> all, int folderId)
    {
        var set = new HashSet<int> { folderId };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var f in all)
                if (f.ParentId is int p && set.Contains(p) && set.Add(f.Id)) grew = true;
        }
        return set;
    }

    /// <summary>
    /// A section's folders as one list in the order they nest, each with its depth — what the editor's
    /// Folder box and the article page's Move box offer. Siblings by name.
    /// </summary>
    public static List<(KbFolder folder, int depth)> Tree(IReadOnlyCollection<KbFolder> section)
    {
        var list = new List<(KbFolder, int)>();
        var ids = section.Select(f => f.Id).ToHashSet();
        void Add(int? parent, int depth)
        {
            foreach (var f in section.Where(f => f.ParentId == parent || (parent == null && f.ParentId is int p && !ids.Contains(p)))
                         .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (list.Any(x => x.Item1.Id == f.Id) || depth > MaxFolderDepth * 2) continue;
                list.Add((f, depth));
                Add(f.Id, depth + 1);
            }
        }
        Add(null, 0);
        return list;
    }

    /// <summary>A folder's name as a dropdown shows it, indented under the folder it is in. Non-breaking
    /// spaces, because a browser drops ordinary leading spaces from an option.</summary>
    public static string TreeLabel(string name, int depth) =>
        depth == 0 ? name : new string(' ', depth * 3) + "└ " + name;

    /// <summary>Whether a folder may hold this article: it is in the same section. Null always may.</summary>
    private static async Task<string> CheckFolderAsync(PlatformDb db, int tenantId, int? folderId, int? clientId)
    {
        if (folderId is not int fid) return "";
        var f = await db.KbFolders.FirstOrDefaultAsync(x => x.Id == fid && x.TenantId == tenantId);
        if (f == null) return "That folder is not there any more — somebody may have removed it. Choose another.";
        if (f.ClientId != clientId)
            return clientId == null ? $"The folder {f.Name} is one client's, and this article is for General. Choose a General folder, or none."
                                    : $"The folder {f.Name} is not in this client's section. Choose one of theirs, or none.";
        return "";
    }

    private static string FolderNameProblem(string name) =>
        name.Length == 0 ? "Give the folder a name."
        : name.Length > MaxFolderName ? $"Keep a folder's name under {MaxFolderName} characters."
        : name.Contains('›') ? "A folder's name cannot have › in it." : "";

    private static async Task<bool> NameTakenAsync(PlatformDb db, int tenantId, int? clientId, int? parentId, string name, int exceptId)
    {
        var lower = name.ToLowerInvariant();
        return await db.KbFolders.AnyAsync(f => f.TenantId == tenantId && f.ClientId == clientId && f.ParentId == parentId
                                                && f.Id != exceptId && f.Name.ToLower() == lower);
    }

    public async Task<(KbFolder? folder, string error)> CreateFolderAsync(int tenantId, string? name, int? clientId, int? parentId,
        string who, TenantRole role)
    {
        if (!Permissions.CanWriteKnowledge(role)) return (null, "You cannot write in the knowledge base.");
        name = Tidy(name);
        var why = FolderNameProblem(name);
        if (why.Length > 0) return (null, why);
        clientId = ClientOrGeneral(clientId);
        parentId = parentId is > 0 ? parentId : null;
        await using var db = await _dbf.CreateDbContextAsync();
        if (clientId is int cid && !await db.Clients.AnyAsync(c => c.Id == cid && c.TenantId == tenantId))
            return (null, "That client was not found.");
        if (parentId is int pid)
        {
            var parent = await db.KbFolders.FirstOrDefaultAsync(f => f.Id == pid && f.TenantId == tenantId);
            if (parent == null) return (null, "The folder it was to go in is not there any more.");
            // The parent decides the section: a subfolder is always where its folder is.
            clientId = parent.ClientId;
            var all = await db.KbFolders.Where(f => f.TenantId == tenantId && f.ClientId == clientId).ToListAsync();
            if (PathOf(all, pid).Count >= MaxFolderDepth) return (null, $"Folders go {MaxFolderDepth} deep at most. Put this one higher up.");
        }
        if (await NameTakenAsync(db, tenantId, clientId, parentId, name, 0)) return (null, $"There is already a folder called {name} there.");
        var folder = new KbFolder { TenantId = tenantId, ClientId = clientId, ParentId = parentId, Name = name, CreatedBy = who };
        db.KbFolders.Add(folder);
        await db.SaveChangesAsync();
        _log.LogInformation("[{Tenant}] knowledge base: {Who} made folder #{Id} \"{Name}\"", tenantId, who, folder.Id, name);
        return (folder, "");
    }

    /// <summary>
    /// Renames a folder and/or moves it inside another of the SAME section (or to its top, with
    /// <paramref name="parentId"/> null). Never into itself or a folder inside it.
    /// </summary>
    public async Task<string> EditFolderAsync(int tenantId, int id, string? name, int? parentId, string who, TenantRole role)
    {
        if (!Permissions.CanWriteKnowledge(role)) return "You cannot write in the knowledge base.";
        name = Tidy(name);
        var why = FolderNameProblem(name);
        if (why.Length > 0) return why;
        parentId = parentId is > 0 ? parentId : null;
        await using var db = await _dbf.CreateDbContextAsync();
        var f = await db.KbFolders.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (f == null) return "That folder is not there any more.";
        var section = await db.KbFolders.Where(x => x.TenantId == tenantId && x.ClientId == f.ClientId).ToListAsync();
        if (parentId is int pid)
        {
            if (!section.Any(x => x.Id == pid)) return "Choose a folder in the same section — General's for General, the client's own for a client.";
            if (WithInside(section, id).Contains(pid)) return "A folder cannot go inside itself, or inside a folder that is in it.";
            // The deepest folder inside it must still be within the limit once it has moved.
            int below = WithInside(section, id).Max(x => PathOf(section, x).Count) - PathOf(section, id).Count;
            if (PathOf(section, pid).Count + 1 + below > MaxFolderDepth) return $"Folders go {MaxFolderDepth} deep at most. Move it somewhere higher up.";
        }
        if (await NameTakenAsync(db, tenantId, f.ClientId, parentId, name, f.Id)) return $"There is already a folder called {name} there.";
        if (f.Name == name && f.ParentId == parentId) return "";
        f.Name = name; f.ParentId = parentId;
        await db.SaveChangesAsync();
        Changed?.Invoke(tenantId, f.ClientId);
        return "";
    }

    /// <summary>
    /// Removes a folder. Nothing in it is lost: its articles and folders move up to where it was.
    /// An empty one anybody who writes may remove; one with something in it, a supervisor or the
    /// owner — the same people who may delete an article, since it changes where everybody looks.
    /// </summary>
    public async Task<(int? parentId, string error)> RemoveFolderAsync(int tenantId, int id, string who, TenantRole role)
    {
        if (!Permissions.CanWriteKnowledge(role)) return (null, "You cannot write in the knowledge base.");
        await using var db = await _dbf.CreateDbContextAsync();
        var f = await db.KbFolders.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (f == null) return (null, "That folder is not there any more.");
        var articles = await db.KbArticles.Where(a => a.TenantId == tenantId && a.FolderId == id).ToListAsync();
        var folders = await db.KbFolders.Where(x => x.TenantId == tenantId && x.ParentId == id).ToListAsync();
        bool empty = folders.Count == 0 && articles.All(a => a.DeletedUtc != null);
        if (!empty && !Permissions.CanDeleteKnowledge(role))
            return (null, "Only a supervisor or the owner can remove a folder with something in it. Move what is in it out first, and then remove it.");
        foreach (var a in articles) a.FolderId = f.ParentId;     // deleted ones too, so a restore finds a real folder
        // The folders already where these are going (the one being removed is going, so it does not count).
        var there = await db.KbFolders.Where(x => x.TenantId == tenantId && x.ClientId == f.ClientId && x.ParentId == f.ParentId && x.Id != f.Id)
            .Select(x => x.Name.ToLower()).ToListAsync();
        foreach (var sub in folders)
        {
            // Two folders of the same name side by side would be two of the same; the moved one says where it came from.
            if (there.Contains(sub.Name.ToLowerInvariant()))
            {
                var from = $" (from {f.Name})";
                sub.Name = (sub.Name.Length + from.Length > MaxFolderName ? sub.Name[..Math.Max(1, MaxFolderName - from.Length)].TrimEnd() : sub.Name) + from;
            }
            sub.ParentId = f.ParentId;
        }
        db.KbFolders.Remove(f);
        await db.SaveChangesAsync();
        _log.LogInformation("[{Tenant}] knowledge base: {Who} removed folder #{Id} \"{Name}\"", tenantId, who, f.Id, f.Name);
        Changed?.Invoke(tenantId, f.ClientId);
        return (f.ParentId, "");
    }

    /// <summary>Moves an article into a folder of its own section, or to the section's top (null). A new version.</summary>
    public async Task<string> MoveArticleAsync(int tenantId, int id, int? folderId, string who, TenantRole role)
    {
        if (!Permissions.CanWriteKnowledge(role)) return "You cannot write in the knowledge base.";
        folderId = folderId is > 0 ? folderId : null;
        await using var db = await _dbf.CreateDbContextAsync();
        var a = await db.KbArticles.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.DeletedUtc == null);
        if (a == null) return "That article is not there any more.";
        var why = await CheckFolderAsync(db, tenantId, folderId, a.ClientId);
        if (why.Length > 0) return why;
        if (a.FolderId == folderId) return "";
        var name = folderId is int fid ? await db.KbFolders.Where(f => f.Id == fid).Select(f => f.Name).FirstAsync() : "";
        a.FolderId = folderId;
        a.Version++; a.UpdatedBy = who; a.UpdatedUtc = DateTime.UtcNow;
        db.KbRevisions.Add(Revision(a, who, name.Length > 0 ? $"Moved to {name}" : "Moved out of its folder"));
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return "Somebody saved a change at the same moment. Try again."; }
        Changed?.Invoke(tenantId, a.ClientId);
        return "";
    }

    /// <summary>A name as typed, with its spaces tidied: "  HP   printers " is "HP printers".</summary>
    private static string Tidy(string? s) => System.Text.RegularExpressions.Regex.Replace((s ?? "").Trim(), @"\s+", " ");

    // ================================================================== pictures

    public string TenantDir(int tenantId)
    {
        var d = Path.Combine(_root, tenantId.ToString());
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// Stores one picture for an article. What it IS is read from its first bytes, never its name —
    /// the same rule as workshop photos (<see cref="TicketPhotoService.SniffType"/>), because this
    /// folder is served back to browsers.
    /// </summary>
    public async Task<(KbImage? image, string error)> AddImageAsync(int tenantId, byte[] bytes, string who)
    {
        if (bytes.Length == 0) return (null, "That picture was empty.");
        if (bytes.Length > MaxImageBytes) return (null, $"That picture is larger than {MaxImageBytes / (1024 * 1024)} MB.");
        var type = TicketPhotoService.WholeImageType(bytes);
        if (!ImageTypes.Contains(type)) return (null, "That is not a picture this page can show. Use a screenshot, JPEG or PNG.");
        var name = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..40] + TicketPhotoService.ExtensionFor(type);
        await File.WriteAllBytesAsync(Path.Combine(TenantDir(tenantId), name), bytes);
        await using var db = await _dbf.CreateDbContextAsync();
        var img = new KbImage { TenantId = tenantId, FileName = name, ContentType = type, Bytes = bytes.Length, UploadedBy = who };
        db.KbImages.Add(img);
        await db.SaveChangesAsync();
        return (img, "");
    }

    /// <summary>A picture's file, for this company only — another company's id is "not found".</summary>
    public async Task<(string path, string type)?> ImageAsync(int tenantId, long id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var img = await db.KbImages.FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId);
        if (img == null) return null;
        var path = Path.Combine(TenantDir(tenantId), img.FileName);
        return File.Exists(path) ? (path, img.ContentType) : null;
    }

    /// <summary>A picture by its portal address, /portal/i/{token} (HANDOVER §159) — this company's only.</summary>
    public async Task<(string path, string type)?> ImageByTokenAsync(int tenantId, string token)
    {
        if (!FileToken.Looks(token)) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        var img = await db.KbImages.FirstOrDefaultAsync(i => i.TenantId == tenantId && i.Token == token);
        if (img == null) return null;
        var path = Path.Combine(TenantDir(tenantId), img.FileName);
        return File.Exists(path) ? (path, img.ContentType) : null;
    }

    /// <summary>
    /// Each picture these words use, id → its portal address, for <see cref="KbText.Render"/> (HANDOVER §159): what a page
    /// shows names pictures by token, never by id. A picture of another company's is not in it, so it is drawn as missing.
    /// </summary>
    public async Task<Func<long, string?>> ImageAddressesAsync(int tenantId, params string?[] bodies)
    {
        var ids = bodies.SelectMany(KbText.ImageIds).Distinct().ToList();
        if (ids.Count == 0) return _ => null;
        await using var db = await _dbf.CreateDbContextAsync();
        var tokens = await db.KbImages.Where(i => i.TenantId == tenantId && ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Token);
        return id => tokens.TryGetValue(id, out var t) ? PortalAddresses.KbImage(t) : null;
    }
}
