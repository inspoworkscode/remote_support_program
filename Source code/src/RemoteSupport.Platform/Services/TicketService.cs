using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>Per-tenant ticket storage. Technician-only: customers never see or touch these.</summary>
public sealed partial class TicketService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private static readonly SemaphoreSlim NumberLock = new(1, 1);
    private readonly TicketPhotoService? _photos;

    public TicketService(IDbContextFactory<PlatformDb> dbf) => _dbf = dbf;

    /// <summary>
    /// With the photo service, so the workshop close gate is enforced on every path that changes a
    /// status — the browser, the console and anything added later. The nullable one above is what
    /// the harnesses use when they are not asking about photos at all.
    /// </summary>
    public TicketService(IDbContextFactory<PlatformDb> dbf, TicketPhotoService photos)
    { _dbf = dbf; _photos = photos; }

    /// <summary>
    /// Why the last update refused to move a status, or empty. Read straight after
    /// <see cref="UpdateAsync"/> by whichever page called it.
    ///
    /// Returned this way rather than thrown because a refused STATUS must not lose the note that
    /// was typed in the same form — the rest of the update still applies, exactly as it does when
    /// the office administrator is refused.
    /// </summary>
    public string LastBlockedReason { get; private set; } = "";

    public async Task<Ticket> CreateAsync(int tenantId, string subject, string description, string openedBy,
        TicketSource source, string deviceId = "", string machineName = "", string contactName = "",
        TicketPriority priority = TicketPriority.Normal, string assignedTo = "",
        TicketCategory category = TicketCategory.General, int? clientId = null,
        Action<Ticket>? extra = null)
    {
        await NumberLock.WaitAsync();
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            int next = await db.Tickets.Where(t => t.TenantId == tenantId).Select(t => (int?)t.Number).MaxAsync() ?? 0;
            var ticket = new Ticket
            {
                TenantId = tenantId, Number = next + 1,
                Subject = string.IsNullOrWhiteSpace(subject) ? "Support request" : subject.Trim(),
                Description = description ?? "", OpenedBy = openedBy, Source = source,
                DeviceId = deviceId, MachineName = machineName, ContactName = contactName,
                Priority = priority, AssignedTo = Assignees.Normalise(assignedTo),
                Category = category, ClientId = clientId,
                Status = TicketStatus.Open,
                // The company's Job types as they are today: this job follows them to its end (HANDOVER §166).
                // Which CATEGORY may be opened is the caller's to ask (CategoryRefusalAsync) — the ones opened
                // by themselves, Remote support and General, cannot be switched off.
                RouteOff = await CompanyOffAsync(db, tenantId)
            };
            extra?.Invoke(ticket);   // category-specific fields (call-out address, workshop asset, …)
            // A category with a route starts on its first stage. Everything else keeps Stage empty
            // and the plain statuses it has always had.
            if (Workflow.Has(ticket.Category)) MoveTo(ticket, Workflow.Stage(Workflow.FirstFor(ticket.Category))!);
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            return ticket;
        }
        finally { NumberLock.Release(); }
    }

    /// <summary>
    /// Narrows a ticket query to what one person is allowed to see. Applied BEFORE any filter the
    /// page offers, so it is a boundary and not a choice.
    ///
    /// A technician sees their own work and the unassigned pool. The pool half is deliberate and
    /// is the one hole in "their own only": a ticket nobody has been given must not be work nobody
    /// knows about, and the office administrator is not always looking. They still cannot see a
    /// ticket that is assigned to a colleague.
    ///
    /// The owner and the office administrator both see everything. What separates them is what
    /// they may CHANGE, which is a different question and lives in <see cref="UpdateAsync"/>.
    ///
    /// Comparing on the display name rather than the technician's id is not an oversight:
    /// <see cref="Ticket.AssignedTo"/> has always been a name, the console assigns by name, and
    /// changing that is a migration of every ticket in every tenant. It is recorded here because
    /// it is the thing that breaks the day two people share a display name.
    ///
    /// <para>Since workflows (2026-09-24) the pool is the BENCH's unassigned work only: a job in the
    /// billing queue is also assigned to nobody, and it is not a technician's to pick up. What a
    /// technician gains instead is the jobs they did the work on, which they can follow through
    /// billing and which come back to them for Parts fitted.</para>
    ///
    /// <para>Since 2026-09-28 a ticket may have several people on it, and "their own" means any
    /// ticket they are one of — see <see cref="Assignees"/> for why it is asked this way.</para>
    /// </summary>
    public static IQueryable<Ticket> VisibleTo(IQueryable<Ticket> q, TenantRole role, string me)
    {
        // Asked as the question, not as "is not a technician": a technician who is also the office
        // administrator sees everything, and a person with no box at all sees the least.
        if (Permissions.CanSeeAllTickets(role)) return q;
        var tag = Assignees.Tag(me);
        return q.Where(t => (me != "" && (Assignees.Separator + t.AssignedTo + Assignees.Separator).Contains(tag))
                         || (t.AssignedTo == "" && t.Lane == (int)WorkLane.Bench)
                         || (me != "" && (Assignees.Separator + t.WorkedBy + Assignees.Separator).Contains(tag))
                         // Asked something on a colleague's job: they must be able to open it to answer.
                         || t.Mentions.Any(m => m.Person == me));
    }

    /// <summary>Tickets this person is one of the people on. For "my work" lists.</summary>
    public static IQueryable<Ticket> OnIt(IQueryable<Ticket> q, string me)
    {
        if (string.IsNullOrWhiteSpace(me)) return q.Where(t => false);
        var tag = Assignees.Tag(me);
        return q.Where(t => (Assignees.Separator + t.AssignedTo + Assignees.Separator).Contains(tag));
    }

    /// <summary>
    /// What a booking must say before it may be written, beyond the photos. Empty means yes.
    /// A workshop machine is booked in to a named technician — Clint, answer 1: "the person who
    /// books it in assigns it to the relevant technician immediately."
    /// </summary>
    public static string CheckBookIn(TicketCategory category, string? assignedTo) =>
        category == TicketCategory.Workshop && string.IsNullOrWhiteSpace(assignedTo)
            ? "Choose the technician this machine is booked in to."
            : "";

    public static string CategoryName(TicketCategory c) => c switch
    {
        TicketCategory.RemoteSupport => "Remote support",
        TicketCategory.WalkIn => "Walk in",
        TicketCategory.Workshop => "Workshop",
        TicketCategory.CallOut => "Call out",
        TicketCategory.Recurring => "Recurring task",
        _ => "General"
    };

    /// <summary>
    /// Turns the number a form or the console posted into a category.
    ///
    /// Anything that is not a defined value becomes General. It used to be clamped into range,
    /// which quietly turned a number nobody recognised into a CALL OUT — the neighbour of the
    /// highest value rather than the harmless one. General is now both the default and the
    /// landing place for nonsense, which is the only reading of "unknown" that is safe.
    /// </summary>
    public static TicketCategory CategoryOf(int raw) =>
        Enum.IsDefined(typeof(TicketCategory), raw) ? (TicketCategory)raw : TicketCategory.General;

    /// <param name="mentions">False for words that are not a colleague's — a CUSTOMER's emailed reply
    /// (2026-10-05) is shown as theirs, but an "@Jason" in it must not flag Jason as asked something.</param>
    /// <returns>The note written — its id is what "email this to the client" sends — or null when nothing was.</returns>
    /// <param name="attachments">Files and photos picked for this comment (2026-10-13), by id: the loose ones on THIS
    /// ticket that <paramref name="author"/> picked are given to it (<see cref="TicketAttachmentService.ClaimAsync"/>).
    /// A comment may be files alone, with no words — progress photos need no caption.</param>
    /// <param name="automatic">A line written by itself while something else happened — a session's, a ticket's own "opened" line —
    /// which Latest activity leaves out unless asked (<see cref="TicketNote.Automatic"/>).</param>
    public async Task<TicketNote?> AddNoteAsync(int tenantId, int ticketId, string author, string body, bool isSystem, bool mentions = true,
        IEnumerable<long>? attachments = null, bool automatic = false)
    {
        var files = (attachments ?? Enumerable.Empty<long>()).Distinct().ToList();
        if (string.IsNullOrWhiteSpace(body) && files.Count == 0) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
        if (t == null) return null;
        // Files alone: only if one of them is really this person's, loose on this ticket — never an empty comment.
        if (string.IsNullOrWhiteSpace(body)
            && !await db.TicketAttachments.AnyAsync(a => a.TenantId == tenantId && a.TicketId == ticketId && a.NoteId == null && a.UploadedBy == author && files.Contains(a.Id)))
            return null;
        var note = new TicketNote { TenantId = tenantId, TicketId = ticketId, Author = author, Body = (body ?? "").Trim(), IsSystem = isSystem, Automatic = automatic, Stage = t.Stage };
        db.TicketNotes.Add(note);
        // A person's own words may ask somebody something, and may answer what they were asked.
        var touched = isSystem || !mentions ? new List<string>() : await ApplyMentionsAsync(db, tenantId, t, note);
        t.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (files.Count > 0 && await TicketAttachmentService.ClaimAsync(db, tenantId, ticketId, note.Id, files, author) > 0)
            await db.SaveChangesAsync();
        RaiseMentionsChanged(tenantId, touched);
        return note;
    }

    /// <summary>
    /// How long after a device's last ticket a new piece of activity is treated as the same one.
    ///
    /// Clint, 2026-09-20. It exists to absorb a dropped connection and a customer double-clicking
    /// Request Support, and nothing else: past it, every session is its own ticket with its own
    /// number. Work that genuinely carries on beyond it is linked with
    /// <see cref="SetParentAsync"/> rather than merged.
    /// </summary>
    public static readonly TimeSpan ReuseWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The ticket a device's activity should attach to: its newest still-open one, but ONLY if it was
    /// last worked on within <see cref="ReuseWindow"/> — opened, or a session on it ended (2026-10-09).
    ///
    /// This used to have no time limit, and that was right when a ticket meant "this machine has
    /// an open problem". Since 3.6.6 a ticket means "this was a piece of work somebody did", so a
    /// session tomorrow on a machine whose ticket is still open is a second piece of work and gets
    /// its own number. **This is the function whose old behaviour stage F exists to undo — do not
    /// quietly widen it back.**
    ///
    /// The 30 minutes were counted from when the ticket was OPENED until 2026-10-09, so a technician
    /// reconnecting five minutes after a two-hour session got a new ticket and was asked why all over
    /// again. Clint: "Reconnecting within 30 minutes should automatically be part of the same ticket
    /// with no reason necessary." Now they count from the end of its last session. Still 30 minutes,
    /// still only a ticket nobody has decided on: the window moved, it did not widen. A session with no
    /// end recorded (the server stopped under it) counts from its start, never as "still going".
    /// </summary>
    public async Task<Ticket?> ActiveForDeviceAsync(int tenantId, string deviceId)
    {
        var since = DateTime.UtcNow - ReuseWindow;
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tickets
            .Where(t => t.TenantId == tenantId && t.DeviceId == deviceId
                     && t.Status != TicketStatus.Closed && t.Status != TicketStatus.Resolved
                     // Only while nobody has decided how it ends (2026-09-27): a ticket already sent
                     // to be invoiced is finished work — a new session is new.
                     && (t.Stage == "" || t.Stage == Workflow.RemoteFirst))
            .OrderByDescending(t => t.CreatedUtc).FirstOrDefaultAsync();
        if (t == null) return null;
        if (t.CreatedUtc >= since) return t;
        var lastSession = await db.Sessions.Where(s => s.TenantId == tenantId && s.TicketId == t.Id)
            .Select(s => (DateTime?)(s.EndedUtc ?? s.StartedUtc)).MaxAsync();
        return lastSession >= since ? t : null;
    }

    // ---------------------------------------------------------------- one job, many sessions

    /// <summary>A ticket's children — the sessions filed under it.</summary>
    public async Task<List<Ticket>> ChildrenAsync(int tenantId, int ticketId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Tickets.Where(t => t.TenantId == tenantId && t.ParentTicketId == ticketId)
            .OrderBy(t => t.CreatedUtc).ToListAsync();
    }

    /// <summary>
    /// Files one ticket under another, or clears the link when <paramref name="parentId"/> is null.
    ///
    /// Refuses everything that would turn this into a tree: itself, a ticket that already has
    /// children, and a parent that is itself a child. One level, so nobody ever has to decide
    /// where in a hierarchy a job belongs.
    /// </summary>
    public async Task<string> SetParentAsync(int tenantId, int ticketId, int? parentId, string by)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
        if (t == null) return "That ticket is not on file.";

        if (parentId == null)
        {
            if (t.ParentTicketId == null) return "";
            var was = await db.Tickets.Where(x => x.Id == t.ParentTicketId).Select(x => x.Number).FirstOrDefaultAsync();
            t.ParentTicketId = null;
            t.UpdatedUtc = DateTime.UtcNow;
            db.TicketNotes.Add(new TicketNote { TenantId = tenantId, TicketId = t.Id, Author = by, IsSystem = true, Body = $"No longer part of #{was}." });
            await db.SaveChangesAsync();
            return "";
        }

        if (parentId == ticketId) return "A ticket cannot be part of itself.";

        var parent = await db.Tickets.FirstOrDefaultAsync(x => x.Id == parentId.Value && x.TenantId == tenantId);
        if (parent == null) return "That ticket is not on file.";
        if (parent.ParentTicketId != null)
            return $"#{parent.Number} is already part of another job. File this under that one instead.";
        if (await db.Tickets.AnyAsync(x => x.TenantId == tenantId && x.ParentTicketId == ticketId))
            return "This ticket already has others filed under it, so it cannot become part of one itself.";

        t.ParentTicketId = parent.Id;
        t.UpdatedUtc = DateTime.UtcNow;
        db.TicketNotes.Add(new TicketNote { TenantId = tenantId, TicketId = t.Id, Author = by, IsSystem = true, Body = $"Filed as part of #{parent.Number}." });
        db.TicketNotes.Add(new TicketNote { TenantId = tenantId, TicketId = parent.Id, Author = by, IsSystem = true, Body = $"#{t.Number} filed under this job." });
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>
    /// Whether a parent may be closed: not while anything filed under it is still open.
    ///
    /// Otherwise "the job is finished" and "somebody is still working on it" would both be true at
    /// once, which is exactly the confusion the parent was meant to remove.
    /// </summary>
    public async Task<string> CheckChildrenClosedAsync(int tenantId, Ticket parent, TicketStatus wanted)
    {
        if (wanted is not (TicketStatus.Resolved or TicketStatus.Closed)) return "";
        if (parent.Status == wanted) return "";
        await using var db = await _dbf.CreateDbContextAsync();
        var open = await db.Tickets
            .Where(t => t.TenantId == tenantId && t.ParentTicketId == parent.Id
                     && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed)
            .Select(t => new { t.Number, t.Subject }).ToListAsync();
        // Which ones, by number AND subject, and what to do (2026-10-09) — the words the pop-ups use too.
        return RemoteSupport.Shared.JobParts.Refusal(open.Select(o => (o.Number, o.Subject))) ?? "";
    }

    public async Task<Ticket?> GetAsync(int tenantId, int ticketId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId && t.TenantId == tenantId);
    }

    /// <summary>Sessions worked under a ticket, for the billing view.</summary>
    public async Task<List<SessionLog>> SessionsForTicketAsync(int tenantId, int ticketId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Sessions.Where(s => s.TenantId == tenantId && s.TicketId == ticketId)
            .OrderBy(s => s.StartedUtc).ToListAsync();
    }

    /// <summary>Total minutes of remote session time booked to a ticket.</summary>
    public static int TotalMinutes(IEnumerable<SessionLog> sessions) =>
        sessions.Where(s => s.EndedUtc.HasValue)
                .Sum(s => Math.Max(1, (int)(s.EndedUtc!.Value - s.StartedUtc).TotalMinutes));

    public async Task<List<TicketNote>> NotesAsync(int tenantId, int ticketId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TicketNotes.Where(n => n.TenantId == tenantId && n.TicketId == ticketId)
            .OrderBy(n => n.CreatedUtc).ToListAsync();
    }

    public async Task<List<Ticket>> ListAsync(int tenantId, string filter, string search, string me,
        TenantRole role = TenantRole.Admin, int take = 300, string sort = "")
    {
        await using var db = await _dbf.CreateDbContextAsync();
        // Scope first, filter second. The filter is what they chose; this is what they are allowed,
        // so asking for "all" asks within it rather than instead of it.
        var q = VisibleTo(db.Tickets.Where(t => t.TenantId == tenantId), role, me);
        q = filter switch
        {
            "mine" => OnIt(q, me).Where(t => t.Status != TicketStatus.Closed),
            // The bench's pool. A job in the billing queue is assigned to nobody too, but it is
            // not waiting for a technician to pick it up.
            "unassigned" => q.Where(t => t.AssignedTo == "" && t.Lane == (int)WorkLane.Bench && t.Status != TicketStatus.Closed),
            // Everything waiting on the office administrator or a supervisor, from any category.
            "billing" => q.Where(t => t.Lane == (int)WorkLane.Office || t.Lane == (int)WorkLane.Supervisor),
            // The @mentions still waiting for this person to reply.
            "asked" => q.Where(t => t.Mentions.Any(m => m.Person == me && m.AnsweredUtc == null)),
            "closed" => q.Where(t => t.Status == TicketStatus.Closed || t.Status == TicketStatus.Resolved),
            "all" => q,
            _ => q.Where(t => t.Status != TicketStatus.Closed && t.Status != TicketStatus.Resolved),  // "open"
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(t => t.Subject.Contains(s) || t.Description.Contains(s) || t.MachineName.Contains(s) || t.ContactName.Contains(s));
        }
        return await Sorted(q, sort).Take(take).ToListAsync();
    }

    /// <summary>
    /// Which of these tickets have the CUSTOMER's emailed reply as their newest entry (2026-10-26, HANDOVER §165, SuperOps'
    /// "Cust. Reply"): the list says "Customer replied" on them until somebody answers — anybody's comment or step after
    /// it. Automatic lines (a session's) do not count as an answer.
    /// </summary>
    public static async Task<HashSet<int>> CustomerRepliedAsync(PlatformDb db, int tenantId, IReadOnlyCollection<int> ticketIds)
    {
        if (ticketIds.Count == 0) return new();
        var newest = await db.TicketNotes.Where(n => n.TenantId == tenantId && ticketIds.Contains(n.TicketId) && !n.Automatic)
            .GroupBy(n => n.TicketId).Select(g => g.Max(n => n.Id)).ToListAsync();
        if (newest.Count == 0) return new();
        return (await db.TicketNotes.Where(n => newest.Contains(n.Id) && n.FromCustomer).Select(n => n.TicketId).ToListAsync()).ToHashSet();
    }

    /// <summary>
    /// A ticket list in the order chosen (2026-10-13, <see cref="RemoteSupport.Shared.TicketSort"/>). BEFORE the take: the
    /// list is the first few hundred, and they must be the first few hundred in THIS order. Ties broken by the number,
    /// so two tickets opened in one second never swap places between two looks.
    /// <para>Every column (2026-10-26, HANDOVER §165) either way round. Text is compared in lower case, as every comparison
    /// here is, so SQLite and PostgreSQL agree. Blanks — no client, nobody on it, no due date — go LAST whichever way
    /// round: turning a list round to see the other end must not bring the empty ones to the top.</para>
    /// </summary>
    public static IQueryable<Ticket> Sorted(IQueryable<Ticket> q, string? sort)
    {
        var key = RemoteSupport.Shared.TicketSort.Of(sort);
        // The four from before, exactly as they were (a console from before asks for them).
        switch (key)
        {
            case RemoteSupport.Shared.TicketSort.Newest: return q.OrderByDescending(t => t.CreatedUtc).ThenByDescending(t => t.Number);
            case RemoteSupport.Shared.TicketSort.Oldest: return q.OrderBy(t => t.CreatedUtc).ThenBy(t => t.Number);
            case RemoteSupport.Shared.TicketSort.Appointment:
                return q.OrderBy(t => t.AppointmentUtc == null).ThenBy(t => t.AppointmentUtc).ThenByDescending(t => t.UpdatedUtc);
        }
        var (col, desc) = RemoteSupport.Shared.TicketSort.Parse(key);
        IOrderedQueryable<Ticket> o = col switch
        {
            RemoteSupport.Shared.TicketSort.Number => By(q, t => t.Number, desc),
            RemoteSupport.Shared.TicketSort.Type => By(q, t =>
                // By the NAME, as the column reads — the stored numbers are in the order categories were added.
                t.Category == TicketCategory.CallOut ? 0 : t.Category == TicketCategory.General ? 1 : t.Category == TicketCategory.Recurring ? 2
                : t.Category == TicketCategory.RemoteSupport ? 3 : t.Category == TicketCategory.WalkIn ? 4 : 5, desc),
            RemoteSupport.Shared.TicketSort.Subject => By(q, t => t.Subject.ToLower(), desc),
            RemoteSupport.Shared.TicketSort.Client => Then(q.OrderBy(t => t.ClientId == null), t => t.Client!.Name.ToLower(), desc),
            RemoteSupport.Shared.TicketSort.Assigned => Then(q.OrderBy(t => t.AssignedTo == ""), t => t.AssignedTo.ToLower(), desc),
            RemoteSupport.Shared.TicketSort.Status => Then(By(q, t => t.Status, desc), t => t.Stage, desc),
            RemoteSupport.Shared.TicketSort.Priority => By(q, t => t.Priority, desc),
            RemoteSupport.Shared.TicketSort.Due => Then(q.OrderBy(t => t.DueDate == null && t.AppointmentUtc == null), t => t.DueDate ?? t.AppointmentUtc, desc),
            _ => By(q, t => t.UpdatedUtc, desc)
        };
        return col == RemoteSupport.Shared.TicketSort.Number ? o : o.ThenByDescending(t => t.UpdatedUtc).ThenByDescending(t => t.Number);

        static IOrderedQueryable<Ticket> By<TK>(IQueryable<Ticket> q, System.Linq.Expressions.Expression<Func<Ticket, TK>> k, bool desc) =>
            desc ? q.OrderByDescending(k) : q.OrderBy(k);
        static IOrderedQueryable<Ticket> Then<TK>(IOrderedQueryable<Ticket> q, System.Linq.Expressions.Expression<Func<Ticket, TK>> k, bool desc) =>
            desc ? q.ThenByDescending(k) : q.ThenBy(k);
    }

    /// <summary>Applies only the fields the technician actually changed, and logs each change as a note.</summary>
    public async Task<(Ticket? ticket, List<string> changes)> UpdateAsync(int tenantId, int ticketId, string by,
        TicketStatus? status, TicketPriority? priority, string? assignedTo, string? subject)
        => await UpdateAsync(tenantId, ticketId, by, TenantRole.Admin, status, priority, assignedTo, subject);

    /// <summary>
    /// The same, with the asker's role, which decides what of it is allowed to happen.
    ///
    /// Enforced HERE and not by disabling a dropdown. A control that is not on the page is a
    /// courtesy to the person using it; the rule is the thing that runs when somebody posts the
    /// form anyway. A field they may not change is dropped silently rather than refused outright,
    /// because the rest of what they sent is legitimate and rejecting the lot would lose a note
    /// they had just typed.
    ///
    /// <paramref name="role"/> is passed in rather than read, and the checks go through
    /// <see cref="Permissions"/>, so roadmap 3.6.8 changes what these answers are without touching
    /// this method.
    /// </summary>
    public async Task<(Ticket? ticket, List<string> changes)> UpdateAsync(int tenantId, int ticketId, string by,
        TenantRole role, TicketStatus? status, TicketPriority? priority, string? assignedTo, string? subject)
    {
        var changes = new List<string>();
        LastBlockedReason = "";
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
        if (t == null) return (null, changes);

        // The office administrator may not declare a machine fixed. They did not touch it.
        if (!Permissions.CanChangeStatus(role)) { status = null; priority = null; subject = null; }

        // A job with a workflow moves by its stage buttons and by nothing else — not by the old
        // status dropdown in a stale tab or an old console, which would walk straight past every
        // gate the route has. The rest of the update (priority, assignment, the note) still applies.
        if (status.HasValue && status.Value != t.Status && t.Stage.Length > 0)
        {
            LastBlockedReason = $"This job moves through its stages. It is at \"{Workflow.Label(t.Stage)}\" — use the buttons for that stage.";
            status = null;
        }

        // A workshop job cannot be closed without photographs of the finished machine. Checked
        // here rather than on the page so that the console, the browser and anything added later
        // all meet the same gate — the whole value of the photos is that they cannot be skipped.
        if (status.HasValue && _photos != null)
        {
            var why = await _photos.CheckCloseAsync(tenantId, t, status.Value);
            if (why.Length > 0) { LastBlockedReason = why; status = null; }
        }

        // And a job cannot be finished while a session filed under it is still running.
        if (status.HasValue)
        {
            var why = await CheckChildrenClosedAsync(tenantId, t, status.Value);
            if (why.Length > 0) { LastBlockedReason = why; status = null; }
        }

        // assignedTo is the WHOLE list wanted afterwards ("Jason, Ryan"), or null to leave it alone.
        var had = Assignees.Parse(t.AssignedTo);
        var want = assignedTo == null ? had : Assignees.Parse(assignedTo);
        var added = want.Where(n => !had.Contains(n)).ToList();
        var removed = had.Where(n => !want.Contains(n)).ToList();
        if (added.Count == 0 && removed.Count == 0) assignedTo = null;
        else if (!Permissions.CanAssignToAnyone(role))
        {
            // Deciding who works a job is the office administrator's, a supervisor's and the owner's.
            // A technician has two narrower rights, and they are the whole of it:
            //
            // JOIN — put THEMSELVES on a bench job, taking nobody off. On an unassigned job that is
            // claiming from the pool, as it always was; on a colleague's it is "I'm helping on this"
            // (2026-09-28). They can only reach a colleague's job they may see — asked about it, or
            // worked on it — because every caller checks VisibleTo first.
            //
            // THEIR OWN JOB — add colleagues to it, and take themselves off it ("if that technician
            // can't do it, they have the ability to reassign to someone else", 2026-09-24). Never take
            // a colleague off, and never leave it with nobody: passing on means to somebody.
            bool joining = added.SequenceEqual([by]) && removed.Count == 0 && t.Lane == (int)WorkLane.Bench;
            bool theirOwn = had.Contains(by) && removed.All(n => n == by) && want.Count > 0;
            if (!(Permissions.CanClaim(role) && joining) && !(Permissions.CanPassOn(role) && theirOwn))
                assignedTo = null;
        }

        if (status.HasValue && status.Value != t.Status)
        {
            changes.Add($"status → {status.Value}");
            t.Status = status.Value;
            t.ClosedUtc = status.Value is TicketStatus.Closed or TicketStatus.Resolved ? DateTime.UtcNow : null;
        }
        if (priority.HasValue && priority.Value != t.Priority) { changes.Add($"priority → {priority.Value}"); t.Priority = priority.Value; }
        if (assignedTo != null)
        {
            // Spell out the hand-off so the trail reads clearly months later. One person to one
            // person keeps the words it always had; anything with more says who came and went.
            // A job nobody is on because a billing step took them off says so (2026-10-02):
            // "from unassigned" read as a fault to people who had seen Jason on it an hour before.
            var lane = (WorkLane)t.Lane;
            if (had.Count == 0 && want.Count > 0 && lane is WorkLane.Office or WorkLane.Supervisor)
                changes.Add($"assigned to {Assignees.Say(want)} (was {QueuePhrase(lane)})");
            else if (had.Count <= 1 && want.Count <= 1)
            {
                var from = had.Count == 0 ? "unassigned" : had[0];
                changes.Add(want.Count == 0 ? $"unassigned (was {from})" : $"reassigned from {from} to {want[0]}");
            }
            else
            {
                if (added.Count > 0) changes.Add($"added {Assignees.Say(added)} to the ticket");
                if (removed.Count > 0) changes.Add($"took {Assignees.Say(removed)} off the ticket");
            }
            t.AssignedTo = Assignees.Join(want);
        }
        if (!string.IsNullOrWhiteSpace(subject) && subject.Trim() != t.Subject) { changes.Add("subject updated"); t.Subject = subject.Trim(); }

        if (changes.Count > 0)
        {
            t.UpdatedUtc = DateTime.UtcNow;
            db.TicketNotes.Add(new TicketNote { TenantId = tenantId, TicketId = t.Id, Author = by, Body = string.Join(", ", changes), IsSystem = true });
            await db.SaveChangesAsync();
        }
        return (t, changes);
    }

    // ---- protocol mapping ----
    public static TicketSummary ToSummary(Ticket t, string clientName = "") => new()
    {
        Id = t.Id, Number = t.Number, Subject = t.Subject,
        Status = (int)t.Status, Priority = (int)t.Priority,
        AssignedTo = t.AssignedTo, ContactName = t.ContactName, MachineName = t.MachineName,
        CreatedUtc = t.CreatedUtc, UpdatedUtc = t.UpdatedUtc,
        Category = (int)t.Category, ClientId = t.ClientId ?? 0, ClientName = clientName,
        AppointmentUtc = t.AppointmentUtc,
        StageLabel = t.Stage.Length > 0 ? Workflow.Label(t.Stage) : "",
        DueOn = t.DueOn, DueDate = t.DueDate, Overdue = TicketDue.IsOverdue(t)
    };

    public static TicketDetail ToDetail(Ticket t, List<TicketNote> notes, string clientName = "", string devicePassword = "") => new()
    {
        Ticket = ToSummary(t, clientName), Description = t.Description, DeviceId = t.DeviceId,
        Source = t.Source.ToString(), OpenedBy = t.OpenedBy,
        SiteAddress = t.SiteAddress, ClientId = t.ClientId ?? 0,
        AssetMake = t.AssetMake, AssetModel = t.AssetModel, AssetSerial = t.AssetSerial,
        Accessories = t.Accessories, DevicePasswordProtected = t.DevicePasswordProtected,
        DevicePassword = devicePassword,
        Notes = notes.Select(n => new TicketNoteInfo { Author = n.Author, Body = n.Body, IsSystem = n.IsSystem, CreatedUtc = n.CreatedUtc,
            StageLabel = n.Stage.Length > 0 ? Workflow.Label(n.Stage) : "", IsWorkDone = n.IsWorkDone,
            FormTitle = n.FormTitle, MovedToLabel = n.MovedTo.Length > 0 ? Workflow.Label(n.MovedTo) : "",
            WorkDoneText = n.IsWorkDone ? WorkDoneText(n.Body) : "",
            Id = n.Id, CanEmail = ClientUpdateService.Sendable(n), FromCustomer = n.FromCustomer }).ToList()
    };
}
