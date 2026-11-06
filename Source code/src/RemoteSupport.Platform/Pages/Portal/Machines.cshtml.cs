using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Hub;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// The free version's machines (2026-10-06, docs/NEXT-SESSION-FREE.md Stage F): its two computers, online or
/// not, each with Remove — which frees its place, <see cref="FreePlan.RemovalsPerMonth"/> a calendar month,
/// by the hub's own rule (<c>TenantHub.RemoveDeviceAsync</c>) — and how to add one. A full company has the
/// Devices page instead, and is sent there.
/// </summary>
public sealed class MachinesModel : PortalPage
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly HubRegistry _hubs;
    private readonly IConfiguration _cfg;
    public MachinesModel(TenantService t, IDbContextFactory<PlatformDb> dbf, HubRegistry hubs, IConfiguration cfg) : base(t)
    { _dbf = dbf; _hubs = hubs; _cfg = cfg; }

    public sealed record Row(string DeviceId, string Name, string Os, bool Online, DateTime LastSeenUtc, bool HasPassword, bool Unconfirmed = false);
    public List<Row> Machines { get; private set; } = new();
    public int RemovalsLeft { get; private set; }
    public DateTime NextMonth { get; private set; }
    public string PublicUrl => (_cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
    /// <summary>Sent here from a page that is not in the free version.</summary>
    [BindProperty(SupportsGet = true)] public int Full { get; set; }
    /// <summary>After Remove (post, then redirect, so the address bar never holds the form's post): what was
    /// removed, or why not.</summary>
    [BindProperty(SupportsGet = true)] public string? Removed { get; set; }
    /// <summary>A CODE, never words: words in the address would let anybody put a sentence of their choosing on
    /// this page with a link. "month" is the monthly limit; anything else, the plain refusal.</summary>
    [BindProperty(SupportsGet = true)] public string? Refused { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!FreePlan.Is(Tenant.Plan)) return Redirect("/portal/devices");
        if (Full == 1) Error = $"That is part of the full version, not {FreePlan.TheNameOf(Tenant.Plan)}.";
        await FillAsync();
        if (!string.IsNullOrEmpty(Refused))
            Error = Refused == "month"
                ? $"{(FreePlan.IsFree(Tenant.Plan) ? "The Free version" : "The Basic version")} can remove {FreePlan.RemovalsPerMonth} computers a month, and this month's are used. The next can be removed on {NextMonth:d MMMM}."
                : "That computer could not be removed. It may already have gone — the list below is up to date.";
        // An install id, never a name: the name is read from the removal on record, so a link can only ever
        // say what really happened.
        if (!string.IsNullOrEmpty(Removed))
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var gone = await db.DeviceRemovals.Where(r => r.TenantId == TenantId && r.DeviceId == Removed).Select(r => r.MachineName).FirstOrDefaultAsync();
            if (gone != null) Message = $"Removed {gone}. Its place is free for another computer.";
        }
        // The same rule for Confirm (2026-10-07): an install id, the name read from the record.
        if (!string.IsNullOrEmpty(Confirmed))
        {
            var row = Machines.FirstOrDefault(m => m.DeviceId == Confirmed && !m.Unconfirmed);
            if (row != null) Message = $"{row.Name} confirmed as the same computer. You can connect to it again.";
        }
        if (Refused == "confirm") Error = "That computer could not be confirmed. The list below is up to date.";
        return Page();
    }

    /// <summary>After Confirm: the install id confirmed, never words.</summary>
    [BindProperty(SupportsGet = true)] public string? Confirmed { get; set; }

    /// <summary>
    /// This computer, installed again, IS the one on the list (2026-10-07) — the hub's own rule. On the free
    /// version only the owner, who is its one person, so in practice "yes, I reinstalled it".
    /// </summary>
    public async Task<IActionResult> OnPostConfirmReinstallAsync(string deviceId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!FreePlan.Is(Tenant.Plan)) return Redirect("/portal/devices");
        var hub = _hubs.Get(TenantId);
        var why = hub == null ? "not ready" : await hub.ConfirmReinstallAsync(deviceId ?? "", Me, MyRole);
        return Redirect(why.Length == 0
            ? $"{FreePortal.Home}?confirmed={Uri.EscapeDataString(deviceId ?? "")}"
            : $"{FreePortal.Home}?refused=confirm");
    }

    public async Task<IActionResult> OnPostRemoveAsync(string deviceId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!FreePlan.Is(Tenant.Plan)) return Redirect("/portal/devices");
        var hub = _hubs.Get(TenantId);
        var why = hub == null ? "The server is not ready; try again in a moment." : await hub.RemoveDeviceAsync(deviceId ?? "", Me, MyRole);
        if (why.Length == 0) return Redirect($"{FreePortal.Home}?removed={Uri.EscapeDataString(deviceId ?? "")}");
        await FillAsync();
        return Redirect($"{FreePortal.Home}?refused={(RemovalsLeft == 0 ? "month" : "other")}");
    }

    private async Task FillAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var hub = _hubs.Get(TenantId);
        var online = hub?.OnlineDeviceIds() ?? new HashSet<string>();
        Machines = (await db.Devices.Where(d => d.TenantId == TenantId).ToListAsync())
            .Select(d => new Row(d.DeviceId, d.MachineName, d.Os, online.Contains(d.DeviceId), d.LastSeenUtc, d.UnattendedPasswordHash.Length > 0, d.ReinstallUnconfirmed))
            .OrderByDescending(r => r.Online).ThenBy(r => r.Name).ToList();
        if (hub != null) (RemovalsLeft, NextMonth) = await hub.RemovalsLeftAsync(db);
    }
}
