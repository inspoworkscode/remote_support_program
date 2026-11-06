using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Everything the portal dashboard shows, gathered in one pass so the page model stays readable.
/// All of it comes off relationships that already exist — Ticket.ClientId, Device.ClientId — so
/// nothing here needs a schema change.
/// </summary>
public sealed class DashboardService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public DashboardService(IDbContextFactory<PlatformDb> dbf) => _dbf = dbf;

    /// <summary>A ticket flattened for display, with the client name already resolved.</summary>
    public sealed record Row(
        int Id, int Number, string Subject, TicketStatus Status, TicketPriority Priority,
        TicketCategory Category, string ClientName, string MachineName, string AssignedTo,
        DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? AppointmentUtc, string SiteAddress)
    {
        public int AgeDays => (int)(DateTime.UtcNow - CreatedUtc).TotalDays;
        public int IdleDays => (int)(DateTime.UtcNow - UpdatedUtc).TotalDays;
    }

    /// <summary>A machine that has not been seen for a long time, or is not linked to a client yet.</summary>
    public sealed record DeviceRow(int Id, string DeviceId, string MachineName, string UserName,
        DateTime LastSeenUtc, bool Online, int? ClientId, string ClientName, string RequestedBy = "");

    public sealed class Data
    {
        // ---- open work ----
        public int Open, InProgress, OnHold;
        public int OpenTotal => Open + InProgress + OnHold;
        public int Urgent, High;
        public int Unassigned, Mine;

        // ---- ageing ----
        public Row? Oldest;
        /// <summary>Open tickets nobody has touched for a week or more, oldest first.</summary>
        public List<Row> Stale = new();

        // ---- time-critical ----
        public List<Row> Today = new();
        public List<Row> Week = new();
        public List<Row> Workshop = new();

        // ---- machines ----
        public int DevicesTotal, DevicesOnline;
        public List<DeviceRow> Unlinked = new();
        public List<DeviceRow> LongUnseen = new();

        // ---- the day so far ----
        public int OpenedToday, ClosedToday;
    }

    /// <summary>Statuses that count as work still on the go.</summary>
    private static bool IsOpen(TicketStatus s) => s is TicketStatus.Open or TicketStatus.InProgress or TicketStatus.OnHold;

    /// <param name="online">Device ids with a live connection, from the tenant hub.</param>
    public async Task<Data> LoadAsync(int tenantId, string me, HashSet<string> online)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var d = new Data();

        // Day boundaries are the technician's local day, converted to the UTC the database stores.
        var dayStart = CompanyClock.ToUtc(CompanyClock.Now.Date);
        var dayEnd = CompanyClock.ToUtc(CompanyClock.Now.Date.AddDays(1));
        var weekEnd = CompanyClock.ToUtc(CompanyClock.Now.Date.AddDays(8));   // today plus the next seven days

        var names = await db.Clients.Where(c => c.TenantId == tenantId).ToDictionaryAsync(c => c.Id, c => c.Name);
        string ClientName(int? id) => id.HasValue && names.TryGetValue(id.Value, out var n) ? n : "";

        var openTickets = await db.Tickets
            .Where(t => t.TenantId == tenantId && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed)
            .ToListAsync();

        var rows = openTickets.Select(t => new Row(t.Id, t.Number, t.Subject, t.Status, t.Priority, t.Category,
            ClientName(t.ClientId), t.MachineName, t.AssignedTo, t.CreatedUtc, t.UpdatedUtc, t.AppointmentUtc, t.SiteAddress)).ToList();

        d.Open = rows.Count(r => r.Status == TicketStatus.Open);
        d.InProgress = rows.Count(r => r.Status == TicketStatus.InProgress);
        d.OnHold = rows.Count(r => r.Status == TicketStatus.OnHold);
        d.Urgent = rows.Count(r => r.Priority == TicketPriority.Urgent);
        d.High = rows.Count(r => r.Priority == TicketPriority.High);
        // The bench's pool — the SAME rule as the list the tile opens (filter=unassigned) and the Tickets page's "N waiting
        // for somebody". It counted every open job with no name on it, and a job in the billing queue or with a supervisor
        // has none either (a billing step takes the technician off), so the tile said 6 and the list it opened was empty
        // (Clint, 2026-10-13). Those are the office's and the supervisors': the Billing queue filter shows them.
        d.Unassigned = openTickets.Count(t => string.IsNullOrWhiteSpace(t.AssignedTo) && t.Lane == (int)WorkLane.Bench);
        d.Mine = rows.Count(r => Assignees.Has(r.AssignedTo, me));

        d.Oldest = rows.OrderBy(r => r.CreatedUtc).FirstOrDefault();
        d.Stale = rows.Where(r => r.IdleDays >= 7).OrderBy(r => r.UpdatedUtc).Take(8).ToList();

        // Appointments are the only hard commitments in the system, so they lead the page.
        var appts = rows.Where(r => r.AppointmentUtc.HasValue).OrderBy(r => r.AppointmentUtc).ToList();
        d.Today = appts.Where(r => r.AppointmentUtc! >= dayStart && r.AppointmentUtc! < dayEnd).ToList();
        d.Week = appts.Where(r => r.AppointmentUtc! >= dayEnd && r.AppointmentUtc! < weekEnd).ToList();
        d.Workshop = rows.Where(r => r.Category == TicketCategory.Workshop).OrderBy(r => r.CreatedUtc).ToList();

        d.OpenedToday = await db.Tickets.CountAsync(t => t.TenantId == tenantId && t.CreatedUtc >= dayStart && t.CreatedUtc < dayEnd);
        d.ClosedToday = await db.Tickets.CountAsync(t => t.TenantId == tenantId && t.ClosedUtc != null && t.ClosedUtc >= dayStart && t.ClosedUtc < dayEnd);

        var devices = await db.Devices.Where(x => x.TenantId == tenantId).ToListAsync();
        d.DevicesTotal = devices.Count;
        d.DevicesOnline = devices.Count(x => online.Contains(x.DeviceId));

        DeviceRow ToRow(Device x) => new(x.Id, x.DeviceId, x.MachineName, x.UserName, x.LastSeenUtc,
            online.Contains(x.DeviceId), x.ClientId, ClientName(x.ClientId), x.RequestedBy);

        // Machines nobody has filed under a customer yet — until they are linked, their tickets
        // do not appear on any client page.
        d.Unlinked = devices.Where(x => x.ClientId == null).OrderByDescending(x => x.LastSeenUtc).Select(ToRow).ToList();

        // A machine that has not called home in a month is either retired or broken. An uninstall
        // and reinstall also leaves the old row behind, so these are usually duplicates.
        var cutoff = DateTime.UtcNow.AddDays(-30);
        d.LongUnseen = devices.Where(x => !online.Contains(x.DeviceId) && x.LastSeenUtc < cutoff)
            .OrderBy(x => x.LastSeenUtc).Take(10).Select(ToRow).ToList();

        return d;
    }
}
