using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Hub;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Removing a machine from the company's list, on the full version (2026-10-06). Clint: "for when machines get
/// upgraded, replaced or a person has been using their own private device." A supervisor or the owner (page
/// policy AND the hub's own check); a reason from <see cref="RemovalReasons"/>; the history kept; an online
/// machine uninstalls itself. The hub's <c>RemoveDeviceAsync</c> is the one rule, as the console's Remove is.
/// </summary>
public sealed class RemoveDeviceModel : PortalPage
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly HubRegistry _hubs;
    public RemoveDeviceModel(TenantService t, IDbContextFactory<PlatformDb> dbf, HubRegistry hubs) : base(t) { _dbf = dbf; _hubs = hubs; }

    [BindProperty(SupportsGet = true)] public string Id { get; set; } = "";
    [BindProperty] public string Reason { get; set; } = "";
    [BindProperty] public string Note { get; set; } = "";
    public Device? Machine { get; private set; }
    public bool Online { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await FindAsync()) return Redirect("/portal/devices");
        return Page();
    }

    /// <summary>No handler name: the form posts to this page's own address, so the light/dark switch's way back is a page.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await FindAsync()) return Redirect("/portal/devices");
        var hub = _hubs.Get(TenantId);
        var why = hub == null ? "The server is not ready; try again in a moment." : await hub.RemoveDeviceAsync(Id, Me, MyRole, Reason, Note);
        if (why.Length == 0) return Redirect($"/portal/devices?removed={Uri.EscapeDataString(Id)}");
        Error = why;
        return Page();
    }

    private async Task<bool> FindAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        Machine = await db.Devices.FirstOrDefaultAsync(d => d.TenantId == TenantId && d.DeviceId == Id);
        Online = Machine != null && (_hubs.Get(TenantId)?.OnlineDeviceIds().Contains(Id) ?? false);
        return Machine != null;
    }
}
