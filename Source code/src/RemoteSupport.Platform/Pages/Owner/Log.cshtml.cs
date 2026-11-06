using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>What every admin did, newest first (2026-10-13) — every level may read it.</summary>
public sealed class LogModel : PageModel
{
    private readonly PlatformAdmins _admins;
    public LogModel(PlatformAdmins admins) => _admins = admins;
    public List<AdminLogEntry> Entries { get; private set; } = new();
    public async Task OnGetAsync() { ViewData["Section"] = "owner"; Entries = await _admins.LogPageAsync(); }
}
