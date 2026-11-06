using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// The people who help run the platform (2026-10-13). Everybody signed in sees the list; a Full admin adds,
/// changes and removes Viewers and Managers; an Owner does Full admins and Owners too. Every rule is
/// <see cref="PlatformAdmins"/> — this page only asks it.
/// </summary>
public sealed class AdminsModel : PageModel
{
    private readonly PlatformAdmins _admins;
    public AdminsModel(PlatformAdmins admins) => _admins = admins;

    public List<PlatformAdmin> List { get; private set; } = new();
    public PlatformAdmins.Actor Me { get; private set; } = null!;
    public string Message { get; private set; } = "";
    public string Problem { get; private set; } = "";
    /// <summary>A link the mail could not deliver, shown once to pass on by hand.</summary>
    public string ShowLink { get; private set; } = "";

    private async Task<IActionResult> Render()
    {
        ViewData["Section"] = "owner";
        Me = AdminIdentity.ActorOf(User);
        List = await _admins.AllAsync();
        return Page();
    }

    public Task<IActionResult> OnGetAsync() => Render();

    public async Task<IActionResult> OnPostAddAsync(string? name, string? email, AdminLevel level)
    {
        var r = await _admins.AddAsync(AdminIdentity.ActorOf(User), name, email, level);
        Linked(r, $"{name?.Trim()} added — a set-up link has gone to {email?.Trim()}.");
        return await Render();
    }

    public async Task<IActionResult> OnPostLinkAsync(int id)
    {
        var r = await _admins.NewLinkAsync(AdminIdentity.ActorOf(User), id);
        Linked(r, "A new set-up link has been emailed. Their old password and authenticator no longer work.");
        return await Render();
    }

    public async Task<IActionResult> OnPostLevelAsync(int id, AdminLevel level)
    {
        var e = await _admins.SetLevelAsync(AdminIdentity.ActorOf(User), id, level);
        if (e.Length > 0) Problem = e; else Message = "Level changed. They sign in again to use it.";
        return await Render();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id)
    {
        var e = await _admins.RemoveAsync(AdminIdentity.ActorOf(User), id);
        if (e.Length > 0) Problem = e; else Message = "Removed. They are signed out at once.";
        return await Render();
    }

    private void Linked(PlatformAdmins.Linked r, string ok)
    {
        if (r.Error.Length > 0) { Problem = r.Error; return; }
        if (r.Mailed) { Message = ok; return; }
        Problem = $"The email did not go ({r.Why}). Give them this link yourself — it works once, for 72 hours:";
        ShowLink = r.Link;
    }
}
