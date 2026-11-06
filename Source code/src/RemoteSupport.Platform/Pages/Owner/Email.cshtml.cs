using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>An admin writes to a company's owners, on whichever server it is (2026-10-13) — <see cref="AdminCompanies.EmailOwnersAsync"/> there.</summary>
public sealed class EmailModel : OwnerFleetPage
{
    private readonly PlatformAdmins _admins;
    public EmailModel(ControlServers servers, PlatformAdmins admins) : base(servers) => _admins = admins;

    [BindProperty(SupportsGet = true)] public int Server { get; set; }
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    [BindProperty] public string Subject { get; set; } = "";
    [BindProperty] public string Body { get; set; } = "";
    public CompanyRow? Company { get; private set; }

    private async Task<IActionResult> Show()
    {
        var r = await Render();
        var v = Fleet.FirstOrDefault(x => x.Row.Id == Server) ?? (Server == 0 ? Fleet.FirstOrDefault(x => x.Row.IsLocal) : null);
        Company = v?.Report?.Companies.FirstOrDefault(c => c.Id == Id);
        if (Company == null) return NotFound();
        if (Server == 0 && v != null) Server = v.Row.Id;
        return r;
    }

    public override Task<IActionResult> OnGetAsync() => Show();

    public async Task<IActionResult> OnPostAsync()
    {
        var me = AdminIdentity.ActorOf(User);
        // Replies go to the admin who wrote, when they have an address of their own.
        string? replyTo = me.Id is int id ? (await _admins.AllAsync()).FirstOrDefault(a => a.Id == id)?.Email : null;
        var done = await Servers.ActAsync(me, Server, "email", Id, new Dictionary<string, string?>
        { ["subject"] = Subject, ["message"] = Body, ["replyTo"] = replyTo });
        if (done.Error.Length > 0) Problem = done.Error; else { Message = done.Said; Subject = ""; Body = ""; }
        return await Show();
    }
}
