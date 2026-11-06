using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Who is carrying what: every technician, their open tickets, and the unassigned pool beside them.
///
/// The owner and the office administrator both need this and need it for different reasons — one
/// is supervising, the other is deciding who to give the next job to. It is one page because the
/// question is the same question.
///
/// The pool is ON this page rather than somewhere else on purpose. A ticket nobody has been given
/// is the thing most likely to be forgotten, and a board that shows six people's work and hides
/// the work nobody has is a board that reads as "everything is in hand".
/// </summary>
public sealed class WorkloadModel : PortalPage
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public WorkloadModel(TenantService t, IDbContextFactory<PlatformDb> dbf) : base(t) => _dbf = dbf;

    public sealed class Load
    {
        public string Name = "";
        public bool Disabled;
        public List<Ticket> Open = new();
        public int Urgent => Open.Count(t => t.Priority >= TicketPriority.High);
        public int InProgress => Open.Count(t => t.Status == TicketStatus.InProgress);
        public int OnHold => Open.Count(t => t.Status == TicketStatus.OnHold);
        /// <summary>The oldest thing they are still carrying, in days. What a board is really for.</summary>
        public int OldestDays => Open.Count == 0 ? 0 : Open.Max(t => (int)(DateTime.UtcNow - t.CreatedUtc).TotalDays);
        public int ClosedThisMonth;
    }

    public List<Load> Loads { get; private set; } = new();
    public List<Ticket> Pool { get; private set; } = new();
    /// <summary>
    /// Every open ticket ONCE — the same number as the Tickets page. It used to add up the people's lists,
    /// and a job with two people on it is in both (2026-09-28), so Clint saw 44 here against 29 there
    /// (2026-10-21). Each person's own count still includes every shared job they are on.
    /// </summary>
    public int TotalOpen { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        // The page policy refuses a technician already; this is the second lock on the same door,
        // because a policy in Program.cs and a page are edited by different people on different days.
        if (!Permissions.CanSeeWorkload(MyRole)) return Forbid();

        await using var db = await _dbf.CreateDbContextAsync();

        var people = await db.Technicians
            .Where(t => t.TenantId == TenantId && (t.Role & TenantRoles.Seated) != 0)
            .OrderBy(t => t.DisplayName)
            .Select(t => new { t.DisplayName, t.Disabled })
            .ToListAsync();

        var open = await db.Tickets
            .Where(t => t.TenantId == TenantId && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed)
            .ToListAsync();

        var monthStart = CompanyClock.ToUtc(new DateTime(CompanyClock.Now.Year, CompanyClock.Now.Month, 1));
        var closed = await db.Tickets
            .Where(t => t.TenantId == TenantId && t.ClosedUtc != null && t.ClosedUtc >= monthStart)
            .Select(t => t.AssignedTo).ToListAsync();

        (Loads, Pool, TotalOpen) = Board(people.Select(p => (p.DisplayName, p.Disabled)).ToList(), open, closed);
        return Page();
    }

    /// <summary>
    /// The board, from the people, the open tickets and who closed what this month — here so
    /// tools/assigncheck can ask it without a server.
    /// </summary>
    public static (List<Load> Loads, List<Ticket> Pool, int TotalOpen) Board(
        List<(string Name, bool Disabled)> people, List<Ticket> open, List<string> closedAssignedTo)
    {
        // A ticket with several people on it counts for each of them (2026-09-28): it is on each
        // person's plate, and a board that credited only the first name would hide the others' load.
        var closedBy = closedAssignedTo.SelectMany(a => Assignees.Parse(a))
            .GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count());

        var loads = people.Select(p => new Load
        {
            Name = p.Name,
            Disabled = p.Disabled,
            Open = open.Where(t => Assignees.Has(t.AssignedTo, p.Name))
                       .OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedUtc).ToList(),
            ClosedThisMonth = closedBy.TryGetValue(p.Name, out var c) ? c : 0
        }).ToList();

        // Work assigned to somebody who is no longer on the list — they left, or were renamed.
        // It would otherwise vanish from this page entirely while still being open, which is the
        // worst possible way for a board to be wrong.
        var known = people.Select(p => p.Name).ToHashSet();
        foreach (var orphan in open.SelectMany(t => Assignees.Parse(t.AssignedTo).Where(n => !known.Contains(n)).Select(n => (n, t)))
                                   .GroupBy(x => x.n))
            loads.Add(new Load { Name = orphan.Key, Disabled = true, Open = orphan.Select(x => x.t).ToList() });

        var pool = open.Where(t => t.AssignedTo.Length == 0)
                       .OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedUtc).ToList();

        // Busiest first: a board is read from the top, and the person in trouble should be there.
        loads = loads.OrderByDescending(l => l.Open.Count).ThenBy(l => l.Name).ToList();
        // Every open ticket is on somebody's list or in the pool, but a shared one is on several lists —
        // so the total is the tickets themselves, never the lists added up.
        return (loads, pool, open.Count);
    }

    public static string StatusName(TicketStatus s) => s switch
    { TicketStatus.InProgress => "In progress", TicketStatus.OnHold => "On hold", _ => s.ToString() };

    public static string AgePill(int days) => days >= 14 ? "pill-bad" : days >= 7 ? "pill-warn" : "pill-muted";
    public static int Age(DateTime utc) => (int)(DateTime.UtcNow - utc).TotalDays;
}
