using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

public sealed class SessionsModel : PortalPage
{
    public SessionsModel(TenantService t) : base(t) { }
    public List<SessionLog> List { get; private set; } = new();
    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        List = await Tenants.SessionsAsync(TenantId);
        return Page();
    }
}
