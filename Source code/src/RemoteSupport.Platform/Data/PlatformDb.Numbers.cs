using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace RemoteSupport.Platform.Data;

/// <summary>
/// A row the portal names by a number that counts within its company (2026-10-23, HANDOVER §159): a client, a
/// knowledge base article, a recurring task. Tickets have had one since the start (<see cref="Ticket.Number"/>,
/// given by <c>TicketService.CreateAsync</c>).
/// </summary>
public interface ICompanyNumbered
{
    int TenantId { get; }
    int Number { get; set; }
}

/// <summary>
/// The address of a stored file in the portal — a photo, a comment's file, a knowledge base picture, a recording
/// (2026-10-23, HANDOVER §159). Random, so an address says nothing about how many the platform holds; it is NOT a
/// secret — every route still asks the signed-in company first and looks the token up within it.
/// </summary>
public static class FileToken
{
    /// <summary>24 lowercase hex characters (96 bits) — the same shape the migration's backfill wrote.</summary>
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>Whether a string from an address could be one, before the database is asked.</summary>
    public static bool Looks(string? s) => s is { Length: 24 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public partial class PlatformDb
{
    // ---- per-company numbers, given on save (2026-10-23, HANDOVER §159) ----
    //
    // Here rather than in each service, because a client is made in four places (Add a client, the import, a ticket's
    // sender, the console) and every harness seeds rows straight into the tables: a number left at 0 would break the
    // unique index on (TenantId, Number) the second time. One lock for the process, as TicketService's NumberLock: the
    // platform is one process per database, and the highest-plus-one must be read and written with nobody between.

    private static readonly SemaphoreSlim CompanyNumberLock = new(1, 1);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var news = ActivityCompanies();
        if (!NeedsNumbers()) return Told(base.SaveChanges(acceptAllChangesOnSuccess), news);
        CompanyNumberLock.Wait();
        try
        {
            GiveNumbers<Client>(false).GetAwaiter().GetResult();
            GiveNumbers<KbArticle>(false).GetAwaiter().GetResult();
            GiveNumbers<RecurringTask>(false).GetAwaiter().GetResult();
            return Told(base.SaveChanges(acceptAllChangesOnSuccess), news);
        }
        finally { CompanyNumberLock.Release(); }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        var news = ActivityCompanies();
        if (!NeedsNumbers()) return Told(await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct), news);
        await CompanyNumberLock.WaitAsync(ct);
        try
        {
            await GiveNumbers<Client>(true, ct);
            await GiveNumbers<KbArticle>(true, ct);
            await GiveNumbers<RecurringTask>(true, ct);
            return Told(await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct), news);
        }
        finally { CompanyNumberLock.Release(); }
    }

    // ---- Latest activity (2026-10-26, HANDOVER §164) ----
    //
    // Something new on a ticket — an entry in its story, or a new ticket — is said HERE, once it is saved, because there are
    // thirty-odd places that write one (comments, steps, the hub's session lines, the intake, recurring tasks, quotes, Back)
    // and a count that missed any of them would be the fault this feature exists to end: an update nobody knew was posted.
    // Static, because a context is made per call and never knows the services; TicketActivityService listens.

    /// <summary>A company got a new ticket or a new entry on one. Raised after the save, never inside it.</summary>
    public static event Action<int>? TicketActivitySaved;

    private List<int> ActivityCompanies() =>
        ChangeTracker.Entries<TicketNote>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.TenantId)
            .Concat(ChangeTracker.Entries<Ticket>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.TenantId))
            .Distinct().ToList();

    private static int Told(int saved, List<int> companies)
    {
        foreach (var c in companies)
            try { TicketActivitySaved?.Invoke(c); } catch { /* a listener's fault must not undo what was saved */ }
        return saved;
    }

    private bool NeedsNumbers() =>
        ChangeTracker.Entries<ICompanyNumbered>().Any(e => e.State == EntityState.Added && e.Entity.Number == 0);

    /// <summary>Each new row of this kind with no number gets its company's highest + 1, in the order they were added.</summary>
    private async Task GiveNumbers<T>(bool async, CancellationToken ct = default) where T : class, ICompanyNumbered
    {
        var added = ChangeTracker.Entries<T>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        foreach (var company in added.Where(r => r.Number == 0).GroupBy(r => r.TenantId))
        {
            int tid = company.Key;
            var q = Set<T>().Where(r => r.TenantId == tid).Select(r => (int?)r.Number);
            int top = (async ? await q.MaxAsync(ct) : q.Max()) ?? 0;
            top = Math.Max(top, added.Where(r => r.TenantId == tid).Select(r => r.Number).DefaultIfEmpty(0).Max());
            foreach (var r in company) r.Number = ++top;
        }
    }
}
