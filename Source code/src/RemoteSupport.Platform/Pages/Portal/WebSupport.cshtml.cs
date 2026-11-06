using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Browser support's switch and hours (3.8.2, 2026-09-30). Clint: "they must be able to switch it
/// off and limit it to business hours they can set for it, as sometimes they may have longer support
/// hours than office hours." The owner and supervisors' page (Program.cs), like Branding.
/// </summary>
public sealed class WebSupportModel : PortalPage
{
    private readonly Hub.HubRegistry _hubs;
    public WebSupportModel(TenantService t, Hub.HubRegistry hubs) : base(t) { _hubs = hubs; }

    public WebSupportHours.Day?[] Days { get; private set; } = new WebSupportHours.Day?[7];
    public bool OpenNow { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        Fill();
        return Page();
    }

    private void Fill()
    {
        Days = WebSupportHours.Parse(Tenant.WebSupportHours);
        OpenNow = WebSupportHours.IsOpen(Tenant, DateTime.UtcNow);
    }

    /// <summary>The address to give customers, on this server.</summary>
    public string Link => $"{Request.Scheme}://{Request.Host}/help/{Tenant.Slug}";

    public async Task<IActionResult> OnPostAsync(bool enabled, string? zone, bool[]? open, string[]? from, string[]? to)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanManageCompany(MyRole)) return Forbid();
        var days = new WebSupportHours.Day?[7];
        var problems = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            if (open == null || open.Length <= i || !open[i]) continue;
            var f = from != null && from.Length > i ? from[i] : "";
            var u = to != null && to.Length > i ? to[i] : "";
            if (!WebSupportHours.TryTime(f, out var a) || !WebSupportHours.TryTime(u, out var b) || a == b)
            { problems.Add(WebSupportHours.Days[i]); continue; }
            days[i] = new WebSupportHours.Day(a, b);
        }
        if (problems.Count > 0)
        {
            Error = $"Check the hours for {string.Join(", ", problems)}: an opening and a closing time, like 08:00 and 17:00.";
            Fill();
            return Page();
        }
        var tz = WebSupportHours.Zones.Any(z => z.Id == zone) ? zone! : "Africa/Johannesburg";
        var err = await Tenants.UpdateBrandingAsync(TenantId, t =>
        {
            t.WebSupportEnabled = enabled;
            t.WebSupportHours = WebSupportHours.Format(days);
            t.WebSupportTimeZone = tz;
        });
        if (err.Length > 0) Error = err;
        else
        {
            Message = enabled ? "Saved. The link rings through during these hours." : "Saved. The link is switched off — anybody who opens it is told so.";
            // Website chat's "Browser support link" button comes and goes with this (3.8.3).
            if (_hubs.Get(TenantId) is { } hub) await hub.PushWebChatListAsync();
        }
        await LoadAsync();
        Fill();
        return Page();
    }
}
