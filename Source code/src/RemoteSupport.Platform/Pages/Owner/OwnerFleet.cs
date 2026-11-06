using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// What the dashboard's pages share (2026-10-13, stage B): every server read once per page, and one way to change a
/// company on whichever server holds it — a form posting <c>server</c>, <c>id</c> and <c>act</c>, the rest of its fields
/// passed on as they are (<see cref="ControlServers.Dispatch"/> says which are used).
/// </summary>
public abstract class OwnerFleetPage : PageModel
{
    protected readonly ControlServers Servers;
    protected OwnerFleetPage(ControlServers servers) => Servers = servers;

    public List<ControlServers.View> Fleet { get; private set; } = new();
    public string Message { get; protected set; } = "";
    public string Problem { get; protected set; } = "";
    public bool CanManage { get; private set; }
    public bool CanRun { get; private set; }
    /// <summary>More than one server: the pages say which server each company is on.</summary>
    public bool Several => Fleet.Count > 1;

    protected async Task<IActionResult> Render()
    {
        ViewData["Section"] = "owner";
        var level = AdminIdentity.LevelOf(User);
        CanManage = PlatformAdmins.MayManage(level); CanRun = PlatformAdmins.MayRun(level);
        Fleet = await Servers.FleetAsync();
        return Page();
    }

    public virtual Task<IActionResult> OnGetAsync() => Render();

    public async Task<IActionResult> OnPostActAsync(int server, int id, string act)
    {
        var args = Request.Form.Keys.Where(k => k is not ("server" or "id" or "act" or "__RequestVerificationToken"))
            .ToDictionary(k => k, k => (string?)Request.Form[k].ToString());
        var done = await Servers.ActAsync(AdminIdentity.ActorOf(User), server, act ?? "", id, args);
        if (done.Error.Length > 0) Problem = done.Error; else Message = done.Said;
        return await Render();
    }
}
