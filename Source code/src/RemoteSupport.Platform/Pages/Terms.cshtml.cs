using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

/// <summary>
/// Any region's terms, to read at any time (2026-10-05) — linked from signup. What a company
/// actually ACCEPTED is on their Billing page, word for word, whatever this page says today.
/// </summary>
public sealed class TermsModel : PageModel
{
    public Pricing.Region Region { get; private set; } = Pricing.Regions[0];
    public Services.Terms.Text Text { get; private set; } = null!;

    /// <summary>The Free or Basic version's terms (?plan=free, ?plan=basic — 2026-10-06, §163), linked from its signup.</summary>
    public bool Free { get; private set; }
    /// <summary>Which one-person plan, as stored ("free" or "basic"), or empty for the full version's.</summary>
    public string Plan { get; private set; } = "";

    public void OnGet(string? region, string? plan)
    {
        Region = Pricing.Regions.FirstOrDefault(r => r.Code == (region ?? "").Trim().ToUpperInvariant()) ?? Pricing.Regions[0];
        Free = RemoteSupport.Shared.FreePlan.Is(plan);
        Plan = Free ? (RemoteSupport.Shared.FreePlan.IsFree(plan) ? RemoteSupport.Shared.FreePlan.Name : RemoteSupport.Shared.FreePlan.BasicName) : "";
        Text = Services.Terms.For(Region.Code, Free ? Plan : null);
    }
}
