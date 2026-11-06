using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Outside help (roadmap 3.9, step 1, 2026-10-06): whether the people at this company's machines may
/// give somebody OUTSIDE it a code — an accounting firm, another company on the platform. Clint: off by
/// default, a switch for the company, and changeable per machine. The owner and supervisors' page
/// (Program.cs); every change goes through the hub, which tells the tray apps and stops any code it no
/// longer allows.
/// </summary>
public sealed class OutsidersModel : PortalPage
{
    private readonly Hub.HubRegistry _hubs;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public OutsidersModel(TenantService t, Hub.HubRegistry hubs, IDbContextFactory<PlatformDb> dbf) : base(t) { _hubs = hubs; _dbf = dbf; }

    public sealed record MachineRow(string DeviceId, string Machine, string Who, int Mode, bool Allowed);
    public sealed record CodeRow(string Machine, string For, string MadeBy, DateTime CreatedUtc, string State);

    public List<MachineRow> Machines { get; private set; } = new();
    public List<CodeRow> Codes { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        await FillAsync();
        return Page();
    }

    private async Task FillAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var clients = await db.Clients.Where(c => c.TenantId == TenantId).ToDictionaryAsync(c => c.Id, c => c.Name);
        var devices = await db.Devices.Where(d => d.TenantId == TenantId).ToListAsync();
        Machines = devices
            .Select(d => new MachineRow(d.DeviceId, d.MachineName,
                d.ClientId is int c && clients.TryGetValue(c, out var n) ? n : d.UserName,
                OutsiderAccess.Normalise(d.OutsiderMode), OutsiderAccess.Allowed(Tenant.OutsidersAllowed, d.OutsiderMode)))
            // The ones set for themselves first: they are the exceptions somebody chose.
            .OrderBy(m => m.Mode == OutsiderAccess.AsCompany).ThenBy(m => m.Who).ThenBy(m => m.Machine).ToList();
        var names = devices.ToDictionary(d => d.DeviceId, d => d.MachineName);
        var now = DateTime.UtcNow;
        Codes = (await db.OutsiderCodes.Where(c => c.TenantId == TenantId).OrderByDescending(c => c.Id).Take(25).ToListAsync())
            .Select(c => new CodeRow(names.TryGetValue(c.DeviceId, out var m) ? m : c.DeviceId, c.ForWhom,
                // A technician's code, made in a session (§155), says so: theirs is not the customer's door.
                c.SessionId.Length > 0 ? $"{c.MadeBy} (technician, in a session)" : c.MadeBy, c.CreatedUtc,
                c.UsedUtc != null ? $"Used by {c.UsedBy}"
                : c.EndedUtc != null ? $"Stopped — {c.EndedWhy}"
                : c.ExpiresUtc <= now ? "Ran out unused"
                : $"Live until {c.ExpiresUtc.Local():HH:mm}"))
            .ToList();
    }

    public async Task<IActionResult> OnPostCompanyAsync(bool enabled)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanAllowOutsiders(MyRole)) return Forbid();
        var err = await Tenants.UpdateBrandingAsync(TenantId, t => t.OutsidersAllowed = enabled);
        if (err.Length > 0) Error = err;
        else
        {
            if (_hubs.Get(TenantId) is { } hub) await hub.RefreshOutsidersAsync(enabled, Me);
            Message = enabled
                ? "Switched on. The people at your machines can now make a code in the support app — except on machines set Off below."
                : "Switched off. No machine can make a code now except those set On below, and every other live code has stopped.";
        }
        await LoadAsync();
        await FillAsync();
        return Page();
    }

    /// <summary>Technicians bringing somebody into a session with a code (2026-10-22, §155). On by default; off stops every
    /// such code and removes anybody let in that way, through the hub.</summary>
    public async Task<IActionResult> OnPostInvitesAsync(bool enabled)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanAllowOutsiders(MyRole)) return Forbid();
        var err = await Tenants.UpdateBrandingAsync(TenantId, t => t.TechnicianInvitesAllowed = enabled);
        if (err.Length > 0) Error = err;
        else
        {
            if (_hubs.Get(TenantId) is { } hub) await hub.RefreshInvitesAsync(enabled, Me);
            Message = enabled
                ? "Switched on. A technician in a session can bring somebody in with Tools → Get outside help."
                : "Switched off. No technician can bring anybody in now; every such code has stopped, and anybody let in that way has been removed.";
        }
        await LoadAsync();
        await FillAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostMachineAsync(string deviceId, int mode)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanAllowOutsiders(MyRole)) return Forbid();
        var hub = _hubs.Get(TenantId);
        var err = hub == null ? "The server is not ready. Try again." : await hub.SetDeviceOutsiderModeAsync(deviceId ?? "", mode, Me);
        if (err.Length > 0) Error = err; else Message = "Saved.";
        await FillAsync();
        return Page();
    }
}
