using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// Companies' reviews, waiting first (2026-10-14). Nothing is on the website until it is approved here (Clint: "after approval").
/// Any admin sees them; a Manager or above approves, declines or takes one down — <see cref="ReviewService.DecideAsync"/>.
/// </summary>
public sealed class ReviewsModel : PageModel
{
    private readonly ReviewService _reviews;
    public ReviewsModel(ReviewService reviews) => _reviews = reviews;

    public List<ReviewService.Row> Rows { get; private set; } = new();
    public bool MayDecide { get; private set; }
    public string Message { get; private set; } = "";
    public bool IsError { get; private set; }

    private async Task<IActionResult> Render()
    {
        Rows = await _reviews.AllAsync();
        MayDecide = PlatformAdmins.MayManage(AdminIdentity.LevelOf(User));
        return Page();
    }

    public Task<IActionResult> OnGetAsync() => Render();

    public async Task<IActionResult> OnPostAsync(int id, string? act)
    {
        var status = act switch { "publish" => ReviewStatus.Published, "decline" => ReviewStatus.Declined, _ => (ReviewStatus?)null };
        if (status == null) { Message = "Choose what to do."; IsError = true; return await Render(); }
        var why = await _reviews.DecideAsync(AdminIdentity.ActorOf(User), id, status.Value);
        IsError = why.Length > 0;
        Message = why.Length > 0 ? why : status == ReviewStatus.Published ? "Published — it is on the website now." : "Declined — it is not on the website.";
        return await Render();
    }
}
