using RemoteSupport.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

public sealed class GetModel : PageModel
{
    private readonly TenantService _tenants;
    private readonly PayloadService _payloads;
    public GetModel(TenantService tenants, PayloadService payloads) { _tenants = tenants; _payloads = payloads; }

    public Tenant? Tenant { get; private set; }
    public bool HasLogo => Tenant != null && !string.IsNullOrEmpty(Tenant.LogoFile);
    public bool Available => Tenant != null && Tenant.IsServiceEnabled && _payloads.ClientAvailable;

    /// <summary>
    /// The one .exe (2026-09-30) is built and staged. Then it is THE download, and the zip is a small
    /// link for IT staff rolling it out by other means.
    /// </summary>
    public bool ExeAvailable => Available && _payloads.ClientExeAvailable;

    /// <summary>Whether the zip contains the installer, and so whether the "install properly" route can be offered.</summary>
    public bool HasInstaller => Available && _payloads.ClientHasInstaller;

    /// <summary>
    /// Browser support is on and in its hours (3.8.2): offered here to somebody whose PC will not run
    /// the app — the reason Clint thought of it, "to possibly circumvent the app block issue".
    /// </summary>
    public bool WebSupportOpen => WebSupportOffered && WebSupportHours.IsOpen(Tenant!, DateTime.UtcNow);

    /// <summary>Switched on at all: the second option is shown, with its hours when it is closed.</summary>
    public bool WebSupportOffered => Tenant != null && Tenant.IsServiceEnabled && Tenant.WebSupportEnabled && !FreePlan.Is(Tenant.Plan);

    public List<string> BrowserHours => Tenant == null ? new() : WebSupportHours.Describe(Tenant);

    public async Task OnGetAsync(string slug)
    {
        var t = await _tenants.BySlugAsync(slug);
        Tenant = t != null && t.Status is not (TenantStatus.Pending or TenantStatus.Rejected or TenantStatus.Closed) ? t : null;
    }
}
