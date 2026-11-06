using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// Free and Basic accounts made by us (2026-10-23, <see cref="FreeCompanies"/>; Free or Basic chosen since 2026-10-25, §163): their name and email, a technician to copy if
/// one will help, and the links go out from here. Any admin sees the list; a Manager or above makes one and sends the links.
/// </summary>
public sealed class FreeModel : PageModel
{
    private readonly FreeCompanies _free;
    public FreeModel(FreeCompanies free) => _free = free;

    public List<FreeCompanies.Row> Rows { get; private set; } = new();
    public bool MayMake { get; private set; }
    public string Message { get; private set; } = "";
    public bool IsError { get; private set; }
    /// <summary>A set-up link whose email did not go — shown this once, to pass on by hand.</summary>
    public string Link { get; private set; } = "";
    public int Highlight { get; private set; }
    public Pricing.Region Region => _free.Region;

    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Company { get; set; } = "";
    [BindProperty] public string Phone { get; set; } = "";
    [BindProperty] public string TimeZone { get; set; } = "";
    [BindProperty] public string Country { get; set; } = "";
    [BindProperty] public string Cc { get; set; } = "";
    /// <summary>"free" or "basic" (§163). The Free version unless Basic is chosen.</summary>
    [BindProperty] public string Plan { get; set; } = "";

    private async Task<IActionResult> Render()
    {
        Rows = await _free.ListAsync();
        MayMake = PlatformAdmins.MayManage(AdminIdentity.LevelOf(User));
        return Page();
    }

    public Task<IActionResult> OnGetAsync() => Render();

    public async Task<IActionResult> OnPostMakeAsync()
    {
        var r = await _free.CreateAsync(AdminIdentity.ActorOf(User), Name, Email, Company, Phone, TimeZone, Country, Cc, Plan);
        Show(r);
        // Made: the form empties for the next one. Refused: what was typed stays.
        if (r.TenantId > 0) { Name = Email = Company = Phone = Cc = ""; Highlight = r.TenantId; }
        return await Render();
    }

    /// <summary>The links again, with the copy typed beside the row (<paramref name="cc"/>), not the form's.</summary>
    public async Task<IActionResult> OnPostSendAsync(int id, string? cc)
    {
        Show(await _free.SendLinksAsync(AdminIdentity.ActorOf(User), id, cc));
        Highlight = id;
        return await Render();
    }

    private void Show(FreeCompanies.Done r)
    {
        IsError = r.Error.Length > 0;
        Message = r.Error.Length > 0 ? r.Error : r.Said;
        Link = r.Link;
    }
}
