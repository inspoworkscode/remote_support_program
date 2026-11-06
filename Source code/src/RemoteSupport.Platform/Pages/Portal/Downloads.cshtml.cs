using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

public sealed class DownloadsModel : PortalPage
{
    private readonly PayloadService _payloads;
    private readonly IConfiguration _cfg;
    public DownloadsModel(TenantService t, PayloadService p, IConfiguration cfg) : base(t) { _payloads = p; _cfg = cfg; }
    public string PublicUrl => (_cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
    public bool ClientReady => Tenant.IsServiceEnabled && _payloads.ClientAvailable;
    public bool TechReady => _payloads.TechnicianAvailable;
    public bool TechExeReady => _payloads.TechnicianExeAvailable;
    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : Redirect("/login");
}
