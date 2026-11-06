using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// The company's review (2026-10-14): the owner writes it — stars, words, the name and company to show, a website, and the tick
/// that allows publishing — and it waits for a platform admin's approval before it is on the website. Also where the banner's
/// Later and Don't ask again post. Every rule is <see cref="ReviewService"/>.
/// </summary>
public sealed class ReviewModel : PortalPage
{
    private readonly ReviewService _reviews;
    public ReviewModel(TenantService t, ReviewService reviews) : base(t) => _reviews = reviews;

    public CompanyReview? Existing { get; private set; }
    public bool MayWrite => Permissions.CanAcceptPrice(MyRole);
    [BindProperty] public int Stars { get; set; }
    [BindProperty] public string Words { get; set; } = "";
    [BindProperty] public string ShownName { get; set; } = "";
    [BindProperty] public string BusinessName { get; set; } = "";
    [BindProperty] public string BusinessUrl { get; set; } = "";
    [BindProperty] public bool Consent { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        Existing = await _reviews.ForAsync(TenantId);
        if (Existing != null)
        {
            Stars = Existing.Stars; Words = Existing.Words; ShownName = Existing.ShownName;
            BusinessName = Existing.BusinessName; BusinessUrl = Existing.BusinessUrl;
        }
        else { ShownName = Me; BusinessName = Tenant.CompanyName; }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        var why = await _reviews.WriteAsync(TenantId, MyRole, Me, User.FindFirst(ClaimTypes.Email)?.Value ?? "",
            new ReviewService.Draft(Stars, Words, ShownName, BusinessName, BusinessUrl, Consent));
        if (why.Length > 0) Error = why;
        else Message = "Thank you! Your review is in. We check every review before it goes on our website.";
        Existing = await _reviews.ForAsync(TenantId);
        return Page();
    }

    public async Task<IActionResult> OnPostLaterAsync(string? back)
    {
        if (!await LoadAsync()) return Redirect("/login");
        await _reviews.LaterAsync(TenantId, MyRole);
        return Redirect(Request.Headers.Referer.FirstOrDefault() is { } r && Uri.TryCreate(r, UriKind.Absolute, out var u) && u.Host == Request.Host.Host ? u.PathAndQuery : "/portal");
    }

    public async Task<IActionResult> OnPostNeverAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        await _reviews.NeverAsync(TenantId, MyRole);
        return Redirect(Request.Headers.Referer.FirstOrDefault() is { } r && Uri.TryCreate(r, UriKind.Absolute, out var u) && u.Host == Request.Host.Host ? u.PathAndQuery : "/portal");
    }
}
