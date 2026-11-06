using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Hub;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// The dashboard: what the work in front of you looks like when you sit down. The onboarding
/// cards this page used to be are still here, but only while the tenant is still setting up.
/// </summary>
public sealed class IndexModel : PortalPage
{
    private readonly IConfiguration _cfg;
    private readonly DashboardService _dash;
    private readonly HubRegistry _hubs;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ContactService _contacts;

    public IndexModel(TenantService t, IConfiguration cfg, DashboardService dash, HubRegistry hubs,
        IDbContextFactory<PlatformDb> dbf, ContactService contacts, TicketActivityService activity)
        : base(t) { _cfg = cfg; _dash = dash; _hubs = hubs; _dbf = dbf; _contacts = contacts; _activity = activity; }
    private readonly TicketActivityService _activity;

    public int Techs, Devices, Sessions;
    /// <summary>Articles in the knowledge base (3.8.5), for its line on the dashboard.</summary>
    public int KbCount;
    /// <summary>New on Latest activity since this person last looked (HANDOVER §164), for its card.</summary>
    public int ActivityNew;
    public DashboardService.Data D { get; private set; } = new();
    /// <summary>Clients for the "link this machine" picker on unassigned devices.</summary>
    public List<Client> Clients { get; private set; } = new();
    public string PublicUrl => (_cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');

    /// <summary>Show the "brand it, add technicians, share your link" cards only until they are done.</summary>
    public bool SetupIncomplete => Techs <= 1 || string.IsNullOrEmpty(Tenant.LogoFile) || Devices == 0;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        // Job types (2026-10-27, §167): the owner is shown them ONCE, the first time they come here after they exist.
        // The trip itself is what is remembered (JobTypesShownUtc), not an answer on that page: waiting for Save or Keep
        // everything sent an owner who simply clicked away back there on every Dashboard click.
        if (MyRole.Has(TenantRole.Owner) && Tenant.JobTypesChosenUtc == null && Tenant.JobTypesShownUtc == null
            && !RemoteSupport.Shared.FreePlan.Is(Tenant.Plan) && await Tenants.MarkJobTypesShownAsync(TenantId))
            return Redirect("/portal/jobtypes?first=1");
        (Techs, Devices, Sessions) = await Tenants.CountsAsync(TenantId);
        D = await _dash.LoadAsync(TenantId, Me, _hubs.Get(TenantId)?.OnlineDeviceIds() ?? new HashSet<string>());
        await using var db = await _dbf.CreateDbContextAsync();
        Clients = await db.Clients.Where(c => c.TenantId == TenantId && !c.Archived).OrderBy(c => c.Name).ToListAsync();
        KbCount = await db.KbArticles.CountAsync(a => a.TenantId == TenantId && a.DeletedUtc == null);
        ActivityNew = (await _activity.NewCountAsync(TenantId, TechnicianId)).Count;
        return Page();
    }

    /// <summary>Link an unassigned machine to a client straight from the dashboard.</summary>
    public async Task<IActionResult> OnPostLinkAsync(string deviceId, int clientId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (clientId > 0)
        {
            // Through ContactService like every other owner change, so a machine that already had
            // a person on it does not end up owned by one company and one person at another.
            var (owner, error) = await _contacts.LinkDeviceAsync(TenantId, deviceId, clientId, null);
            if (error.Length == 0)
                _hubs.Get(TenantId)?.RefreshDeviceOwner(deviceId, owner.ClientId, owner.ClientName,
                    owner.ContactId, owner.ContactName);
        }
        return Redirect("/portal");
    }

    public static string StatusName(TicketStatus s) => s switch
    { TicketStatus.InProgress => "In progress", TicketStatus.OnHold => "On hold", _ => s.ToString() };

    public static string PriorityPill(TicketPriority p) => p switch
    { TicketPriority.Urgent => "pill-bad", TicketPriority.High => "pill-warn", _ => "pill-muted" };

    /// <summary>Ageing reads as a warning at a week and a problem at a fortnight.</summary>
    public static string AgePill(int days) => days >= 14 ? "pill-bad" : days >= 7 ? "pill-warn" : "pill-muted";

    public static string Local(DateTime utc) => utc.Local().ToString("ddd d MMM HH:mm");

    /// <summary>"today", "yesterday", "3 days ago" — reads properly in a sentence.</summary>
    public static string Ago(int days) => days switch { <= 0 => "today", 1 => "yesterday", _ => $"{days} days ago" };
}
