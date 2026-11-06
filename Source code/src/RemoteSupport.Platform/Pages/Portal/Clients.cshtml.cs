using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>Client list and creation. Reception and administrators both use this.</summary>
public sealed class ClientsModel : PortalPage
{
    private readonly ClientService _clients;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public ClientsModel(TenantService t, ClientService c, IDbContextFactory<PlatformDb> dbf) : base(t) { _clients = c; _dbf = dbf; }

    public List<Client> Clients { get; private set; } = new();
    public string Search { get; private set; } = "";
    public bool CanImport => Permissions.CanImportClients(MyRole);
    private Dictionary<int, (int all, int open)> _counts = new();
    public (int all, int open) CountsFor(int id) => _counts.TryGetValue(id, out var c) ? c : (0, 0);

    private async Task<IActionResult> RenderAsync()
    {
        Clients = string.IsNullOrWhiteSpace(Search)
            ? await _clients.AllAsync(TenantId)
            : await _clients.SearchAsync(TenantId, Search, 200);

        await using var db = await _dbf.CreateDbContextAsync();
        var ids = Clients.Select(c => c.Id).ToList();
        var rows = await db.Tickets.Where(t => t.TenantId == TenantId && t.ClientId != null && ids.Contains(t.ClientId!.Value))
            .Select(t => new { t.ClientId, t.Status }).ToListAsync();
        _counts = rows.GroupBy(r => r.ClientId!.Value).ToDictionary(
            g => g.Key,
            g => (g.Count(), g.Count(x => x.Status != TicketStatus.Resolved && x.Status != TicketStatus.Closed)));
        return Page();
    }

    public async Task<IActionResult> OnGetAsync(string? search, int? removed)
    {
        if (!await LoadAsync()) return Redirect("/login");
        Search = search ?? "";
        if (removed == 1) Message = "Client removed.";
        return await RenderAsync();
    }

    public async Task<IActionResult> OnPostAddAsync(string name, string? contact, string? phone, string? email, string? address, string? notes)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var (c, err) = await _clients.CreateAsync(TenantId, name, contact ?? "", phone ?? "", email ?? "", address ?? "", notes ?? "");
        if (c == null) Error = err; else Message = $"{c.Name} saved.";
        return await RenderAsync();
    }
}
