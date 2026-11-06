using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>A job that has just come into the billing queue: what the office's pop-up says.</summary>
public sealed record BillingArrival(int TicketId, int Number, string Subject, string Stage, string By);

/// <summary>
/// What changed in the billing queue, worked out ONCE by the service and handed to every listener:
/// who is told, how many jobs are now waiting for them, and — when a job has just arrived — which.
/// </summary>
public sealed record BillingNews(int Count, IReadOnlyList<string> Notify, BillingArrival? Arrived);

/// <summary>
/// The office is told when a job lands in the billing queue (Clint, 2026-10-05: "when something is
/// assigned to billing, all office administrators should be notified" — a badge and a pop-up, his
/// choice). The pop-up is the moment it arrives; the badge is how many are waiting for the office to
/// DO something, and it stays until they have.
///
/// <para>Who is told is <see cref="Permissions.HearsOfBilling"/>: every office administrator, or —
/// in a company that has none — the supervisors and the owner, who work the queue then (answer 16).</para>
/// </summary>
public sealed partial class TicketService
{
    /// <summary>
    /// Raised AFTER the change is saved. The hub and the portal's live stream listen; the service
    /// knows nothing about sockets. Never raised for a move that does not touch the billing queue.
    /// </summary>
    public event Action<int, BillingNews>? BillingChanged;

    /// <summary>
    /// How many jobs are waiting for the office to act: in the billing queue and not on hold. A job
    /// invoiced and awaiting payment, waiting on a supplier or on the customer to collect is in the
    /// queue but waiting on somebody else — counting those would leave the badge lit for weeks, and
    /// a badge that never goes out stops being read.
    /// </summary>
    public async Task<int> BillingWaitingAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await BillingWaitingAsync(db, tenantId);
    }

    private static Task<int> BillingWaitingAsync(PlatformDb db, int tenantId) =>
        db.Tickets.CountAsync(t => t.TenantId == tenantId && t.Lane == (int)WorkLane.Office && t.Status != TicketStatus.OnHold);

    /// <summary>Everybody told when a job reaches the billing queue, by display name.</summary>
    public async Task<List<string>> BillingNotifiedAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await BillingNotifiedAsync(db, tenantId);
    }

    private static async Task<List<string>> BillingNotifiedAsync(PlatformDb db, int tenantId)
    {
        var people = await db.Technicians.Where(t => t.TenantId == tenantId && !t.Disabled)
            .Select(t => new { t.DisplayName, t.Role }).ToListAsync();
        bool hasOffice = people.Any(p => p.Role.Has(TenantRole.Billing));
        return people.Where(p => Permissions.HearsOfBilling(p.Role, hasOffice)).Select(p => p.DisplayName).Distinct().ToList();
    }

    /// <summary>
    /// The badge for one person: how many jobs are waiting for the office, or null when they are not
    /// one of the people told — the portal's header and the console's sign-in ask this.
    /// </summary>
    public async Task<int?> BillingBadgeForAsync(int tenantId, string person)
    {
        if (string.IsNullOrEmpty(person)) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        if (!(await BillingNotifiedAsync(db, tenantId)).Contains(person)) return null;
        return await BillingWaitingAsync(db, tenantId);
    }

    private async Task RaiseBillingChangedAsync(int tenantId, BillingArrival? arrived)
    {
        if (BillingChanged == null) return;
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var news = new BillingNews(await BillingWaitingAsync(db, tenantId), await BillingNotifiedAsync(db, tenantId), arrived);
            BillingChanged.Invoke(tenantId, news);
        }
        catch { /* a listener's fault must not undo a step that has been saved */ }
    }
}
