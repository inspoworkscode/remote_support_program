using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Hub;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Website chat (3.8.3, 2026-09-30): the switch, the hours and the line to paste into the company's
/// website — the owner and supervisors' — and the recent chats, which everybody who answers them sees.
/// Answering happens in the Chat corner of every portal page and in the console, not here.
/// </summary>
public sealed class WebChatModel : PortalPage
{
    private readonly WebChatService _chat;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly HubRegistry _hubs;
    public WebChatModel(TenantService t, WebChatService chat, IDbContextFactory<PlatformDb> dbf, HubRegistry hubs) : base(t)
    { _chat = chat; _dbf = dbf; _hubs = hubs; }

    public WebSupportHours.Day?[] Days { get; private set; } = new WebSupportHours.Day?[7];
    public WebChatService.Mode Mode { get; private set; }
    public bool CanManage => Permissions.CanManageCompany(MyRole);
    public List<WebChat> Recent { get; private set; } = new();
    public Dictionary<int, List<WebChatLine>> Lines { get; private set; } = new();
    public Dictionary<int, int> TicketNumbers { get; private set; } = new();

    /// <summary>The one line the company pastes into its website, on this server.</summary>
    public string Snippet => $"<script src=\"{Origin}/webchat/{Tenant.Slug}.js\" async></script>";
    public string Origin => $"{Request.Scheme}://{Request.Host}";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        await FillAsync();
        return Page();
    }

    private async Task FillAsync()
    {
        Days = WebSupportHours.Parse(Tenant.WebChatHours);
        Mode = _chat.ModeFor(Tenant, DateTime.UtcNow);
        Recent = await _chat.RecentAsync(TenantId, 50);
        var ids = Recent.Select(c => c.Id).ToList();
        await using var db = await _dbf.CreateDbContextAsync();
        Lines = (await db.WebChatLines.Where(l => l.TenantId == TenantId && ids.Contains(l.WebChatId)).OrderBy(l => l.Id).ToListAsync())
            .GroupBy(l => l.WebChatId).ToDictionary(g => g.Key, g => g.ToList());
        // A message left becomes a ticket; the link names it by its number (HANDOVER §159).
        var tids = Recent.Where(c => c.TicketId != null).Select(c => c.TicketId!.Value).ToList();
        TicketNumbers = await db.Tickets.Where(t => t.TenantId == TenantId && tids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number);
    }

    public async Task<IActionResult> OnPostAsync(bool enabled, string? zone, bool[]? open, string[]? from, string[]? to)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!CanManage) return Forbid();
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
            await FillAsync();
            return Page();
        }
        var tz = WebSupportHours.Zones.Any(z => z.Id == zone) ? zone! : "Africa/Johannesburg";
        var err = await Tenants.UpdateBrandingAsync(TenantId, t =>
        {
            t.WebChatEnabled = enabled;
            t.WebChatHours = WebSupportHours.Format(days);
            t.WebChatTimeZone = tz;
        });
        if (err.Length > 0) Error = err;
        else
        {
            Message = enabled ? "Saved. The chat bubble takes chats during these hours while somebody is signed in, and messages otherwise."
                              : "Saved. The chat bubble is switched off — it no longer appears on your website.";
            // The consoles show or hide their Website chat button from this.
            if (_hubs.Get(TenantId) is { } hub) await hub.PushWebChatListAsync();
        }
        await LoadAsync();
        await FillAsync();
        return Page();
    }
}
