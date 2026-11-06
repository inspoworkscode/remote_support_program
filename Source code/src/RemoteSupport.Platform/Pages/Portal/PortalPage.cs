using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>Base for tenant portal pages: resolves the signed-in company from the cookie.</summary>
public abstract class PortalPage : PageModel
{
    protected readonly TenantService Tenants;
    protected PortalPage(TenantService tenants) => Tenants = tenants;

    public Tenant Tenant { get; private set; } = null!;
    public string Message { get; protected set; } = "";
    public string Error { get; protected set; } = "";

    public int TenantId => int.TryParse(User.FindFirst(PortalIdentity.TenantIdClaim)?.Value, out var id) ? id : 0;

    // ---- who is signed in ----
    //
    // Added in 3.6.3. This class used to resolve a COMPANY and nothing else, so every portal page
    // was company-wide by construction and "a technician sees only their own tickets" could not be
    // written down, let alone enforced. Everything role-shaped in stage 3.6.4 hangs off these four.

    /// <summary>The signed-in person's row id. 0 only for a cookie issued before 3.6.3.</summary>
    public int TechnicianId => PortalIdentity.TechnicianIdOf(User);

    /// <summary>Their display name — the string tickets are assigned by.</summary>
    public string Me => User.Identity?.Name ?? "";

    public TenantRole MyRole => PortalIdentity.RoleOf(User);

    /// <summary>The owner or supervisor: sees everything and may change anything.</summary>
    public bool IsOwner => Permissions.CanManageCompany(MyRole);

    /// <summary>
    /// The office administrator. Sees every ticket, opens them, assigns and reassigns them, and
    /// comments — and may not move a status. Assignment is theirs; where a job has got to is the
    /// technician's. Enforced in the service layer, never by hiding a control.
    /// </summary>
    public bool IsOfficeAdmin => MyRole.Has(TenantRole.Billing);

    /// <summary>Sees their own tickets and the unassigned pool, and nothing of a colleague's.</summary>
    public bool IsTechnician => MyRole.Has(TenantRole.Technician);

    /// <summary>Does ticket work — false for an owner-only login, which oversees and comments (2026-10-15).</summary>
    public bool WorksTickets => Permissions.CanWorkTickets(MyRole);

    /// <summary>
    /// The second lock on ticket work (<see cref="TicketWorkAttribute"/>): a handler a page does not allow an
    /// owner-only login is refused before it runs. A refused page goes to the ticket list; a refused post is told why.
    /// </summary>
    public override async Task OnPageHandlerExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext ctx,
        Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
        if (TicketWorkAttribute.Refuses(GetType(), Request.Method, ctx.HandlerMethod?.Name, MyRole))
        {
            ctx.Result = HttpMethods.IsGet(Request.Method) && string.IsNullOrEmpty(ctx.HandlerMethod?.Name)
                ? Redirect("/portal/tickets")
                : new ContentResult { StatusCode = 403, Content = Permissions.OwnerOnlyRefusal, ContentType = "text/plain; charset=utf-8" };
            return;
        }
        await next();
    }

    // ---- a page that names one thing (2026-10-23, HANDOVER §159) ----

    /// <summary>The number in this page's address — <c>/portal/t/{number}</c>, <c>/portal/c/{number}</c> … — or 0.</summary>
    public int AddressNumber => RouteData.Values.TryGetValue("number", out var v) && int.TryParse(v?.ToString(), out var n) ? n : 0;

    /// <summary>
    /// The id this page's address means, within the signed-in company, for a page reached at its new address
    /// (a number) or its old one (a database id). 0 when it names nothing of this company's — the page then answers
    /// exactly as it answers a number that does not exist, so another company's id says nothing. An old address that IS
    /// this company's is sent on to the new one, query kept — a 302 for a GET, a 307 for a POST (a page left open since
    /// before the change still posts) — and null comes back with <paramref name="ctx"/>'s result set.
    /// </summary>
    protected async Task<int?> AddressedIdAsync(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext ctx,
        Func<PlatformDb, int, int, Task<int>> idOfNumber)
    {
        var dbf = HttpContext.RequestServices.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<PlatformDb>>();
        await using var db = await dbf.CreateDbContextAsync();
        if (RouteData.Values.ContainsKey("number")) return AddressNumber > 0 ? await idOfNumber(db, TenantId, AddressNumber) : 0;
        if (!RouteData.Values.ContainsKey("id")) return 0;
        var to = await PortalAddresses.FromOldAsync(db, TenantId, Request.Path + Request.QueryString);
        if (to == null) return 0;
        ctx.Result = HttpMethods.IsGet(Request.Method) ? Redirect(to) : RedirectPreserveMethod(to);
        return null;
    }

    /// <summary>
    /// A query that still names a client or ticket by id the old way (<c>?client=</c>, <c>?ticket=</c>): sent on to the
    /// same page asking by number. One that names nothing of this company's goes to the page with no query at all —
    /// the same answer whether it is another company's or nobody's.
    /// </summary>
    protected async Task<IActionResult> FromOldQueryAsync()
    {
        var dbf = HttpContext.RequestServices.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<PlatformDb>>();
        await using var db = await dbf.CreateDbContextAsync();
        var to = await PortalAddresses.FromOldAsync(db, TenantId, Request.Path + Request.QueryString) ?? Request.Path.ToString();
        return HttpMethods.IsGet(Request.Method) ? Redirect(to) : RedirectPreserveMethod(to);
    }

    protected async Task<bool> LoadAsync()
    {
        var t = await Tenants.ByIdAsync(TenantId);
        if (t == null) return false;
        Tenant = t;
        ViewData["Section"] = "portal";
        // The portal wears the tenant's colours, not the product's. Resellers get their own brand
        // without a stylesheet each; see BrandPalette for why the text shade is not the raw colour.
        ViewData["BrandCss"] = BrandPalette.CssVariables(t.PrimaryColor, t.AccentColor);
        return true;
    }

    public string StatusPill => Tenant.Status switch
    {
        TenantStatus.Active => "pill-ok", TenantStatus.Trial => Tenant.TrialExpired ? "pill-bad" : "pill-warn",
        TenantStatus.Pending => "pill-muted", _ => "pill-bad"
    };
    public string StatusText => Tenant.Status switch
    {
        // Through TrialDaysLeft, so this pill and the warning emails can never disagree about how
        // many days are left — they used to, by one, which is exactly the kind of small
        // inconsistency that makes somebody distrust the whole notice.
        TenantStatus.Trial => Tenant.TrialExpired ? "Trial ended" : $"Trial — {Math.Max(0, Tenant.TrialDaysLeft ?? 0)} days left",
        _ => Tenant.Status.ToString()
    };
}
