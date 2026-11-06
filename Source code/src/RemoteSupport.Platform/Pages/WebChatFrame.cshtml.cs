using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

/// <summary>
/// What is inside the website chat bubble (3.8.3): /webchat/{slug}/frame, opened in an iframe by
/// wwwroot/webchat-embed.js on the company's own website. Ours, so the visitor's chat never touches
/// the host page. The page draws; wwwroot/webchat.js does the talking.
/// </summary>
public sealed class WebChatFrameModel : PageModel
{
    private readonly TenantService _tenants;
    private readonly WebChatService _chat;
    private readonly IConfiguration _cfg;
    public WebChatFrameModel(TenantService tenants, WebChatService chat, IConfiguration cfg) { _tenants = tenants; _chat = chat; _cfg = cfg; }

    public Tenant? Tenant { get; private set; }
    /// <summary>The name the visitor sees: the company's, or ours on our own site (SiteChat).</summary>
    public string Company { get; private set; } = "";
    public string Mode { get; private set; } = "off";
    public string Fill { get; private set; } = "#2E9BFF";
    public List<string> Hours { get; private set; } = new();
    /// <summary>The page of their website the visitor is on, from the bubble.</summary>
    public string Visited { get; private set; } = "";

    // "from", not "page": page is Razor Pages' own route value, and ?page= arrived as "/WebChatFrame"
    // — every visitor would have seemed to be on that page. Found by webchatcheck.
    public async Task OnGetAsync(string slug, string? from, string? site)
    {
        var t = await _tenants.BySlugAsync(slug);
        Tenant = t != null && t.Status is not (TenantStatus.Pending or TenantStatus.Rejected or TenantStatus.Closed) ? t : null;
        if (Tenant == null) return;
        Mode = _chat.ModeFor(Tenant, DateTime.UtcNow).ToString().ToLowerInvariant();
        var ours = SiteChat.Ours(_cfg, slug, site);
        Company = ours ? SiteChat.Name : Tenant.CompanyName;
        Fill = ours ? SiteChat.Fill : BrandPalette.FillForWhiteText(Tenant.AccentColor);
        Hours = WebSupportHours.Describe(Tenant.WebChatHours);
        Visited = (from ?? "").Length <= 400 ? from ?? "" : from![..400];
    }
}
