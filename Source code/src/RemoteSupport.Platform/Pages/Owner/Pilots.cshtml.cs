using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// Pilot links (2026-10-15, <see cref="PilotService"/>): make one per pilot company, copy it, send it. Any admin sees the
/// list; a Manager or above makes and cancels. The link is shown ONCE, on the page that made it — only its fingerprint is kept.
/// </summary>
public sealed class PilotsModel : PageModel
{
    private readonly PilotService _pilots;
    public PilotsModel(PilotService pilots) => _pilots = pilots;

    public List<(PilotInvite invite, string company)> Rows { get; private set; } = new();
    public bool MayMake { get; private set; }
    public string Message { get; private set; } = "";
    public bool IsError { get; private set; }
    /// <summary>The link just made — shown this once.</summary>
    public string NewLink { get; private set; } = "";
    [BindProperty] public string CompanyName { get; set; } = "";
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Note { get; set; } = "";

    /// <summary>The pilot companies, with how much each has used the service (2026-10-22).</summary>
    public List<PilotService.PilotCompany> Companies { get; private set; } = new();

    private async Task<IActionResult> Render()
    {
        Rows = await _pilots.ListAsync();
        Companies = await _pilots.CompaniesAsync();
        MayMake = PlatformAdmins.MayManage(AdminIdentity.LevelOf(User));
        return Page();
    }

    /// <summary>The day a notice given today ends on, as the page offers it: the terms' 30 days, in the company's own zone.</summary>
    public static string NoticeDefault(Tenant t) =>
        DateTime.UtcNow.Local(CompanyClock.Find(t.TimeZone)).Date.AddDays(PilotService.NoticeDays).ToString("yyyy-MM-dd");

    /// <summary>End a pilot on a day: suspended then closed, or the standard price (2026-10-22, <see cref="PilotService.GiveNoticeAsync"/>).</summary>
    public async Task<IActionResult> OnPostNoticeAsync(int id, string? day, string? after, string? message)
    {
        DateOnly? d = DateOnly.TryParseExact(day ?? "", "yyyy-MM-dd", out var parsed) ? parsed : null;
        var (error, said) = await _pilots.GiveNoticeAsync(AdminIdentity.ActorOf(User), id, d, after, message);
        IsError = error.Length > 0;
        Message = IsError ? error : said;
        return await Render();
    }

    public async Task<IActionResult> OnPostWithdrawAsync(int id)
    {
        var (error, said) = await _pilots.WithdrawNoticeAsync(AdminIdentity.ActorOf(User), id);
        IsError = error.Length > 0;
        Message = IsError ? error : said;
        return await Render();
    }

    public Task<IActionResult> OnGetAsync() => Render();

    public async Task<IActionResult> OnPostMakeAsync()
    {
        var (token, invite, error) = await _pilots.MakeAsync(AdminIdentity.ActorOf(User), CompanyName, Email, Note);
        if (invite == null) { Message = error; IsError = true; return await Render(); }
        NewLink = _pilots.LinkFor(token);
        Message = $"Link made for {invite.CompanyName}. Copy it now — it is not shown again. It works once, until {invite.ExpiresUtc:d MMMM yyyy}.";
        CompanyName = Email = Note = "";
        return await Render();
    }

    public async Task<IActionResult> OnPostCancelAsync(int id)
    {
        var why = await _pilots.CancelAsync(AdminIdentity.ActorOf(User), id);
        IsError = why.Length > 0;
        Message = why.Length > 0 ? why : "Cancelled — that link no longer works.";
        return await Render();
    }
}
