using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Hub;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Changing version, in place (Stage G, 2026-10-06; from R50 since 2026-10-25; three ways since §163). Clint's answer: "Yes, in
/// place" — the same account, the same computers and saved passwords. Asked on 2026-10-25 <i>"will the free then have the option
/// to upgrade to basic and full, and does basic have the option to upgrade to full and downgrade to free?"</i>, so:
/// <list type="bullet">
/// <item>the Free version moves up to the Basic version (<c>?to=basic</c>) or the full version (<c>?to=full</c>) — with neither
/// named it is asked which;</item>
/// <item>the Basic version moves up to the full version, or down to the Free version (<c>?to=free</c>) once one computer is left.</item>
/// </list>
/// Each shows the terms of where it goes and the price, and the owner's one tick and one button do it. The rules are
/// <see cref="TenantService.UpgradeAsync"/> and <see cref="CompanyBilling.DowngradeToFreeAsync"/>; the hub is then told, so every
/// open console changes at once.
/// </summary>
public sealed class UpgradeModel : PortalPage
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly HubRegistry _hubs;
    private readonly PayfastService _payfast;
    private readonly CompanyBilling _billing;
    public UpgradeModel(TenantService t, IDbContextFactory<PlatformDb> dbf, HubRegistry hubs, PayfastService payfast, CompanyBilling billing) : base(t)
    { _dbf = dbf; _hubs = hubs; _payfast = payfast; _billing = billing; }

    /// <summary>Upgraded, and on to PayFast for the card.</summary>
    public PayfastService.Checkout? Checkout { get; private set; }

    /// <summary>Where to: <see cref="FreePlan.BasicName"/>, "full", <see cref="FreePlan.Name"/> (down, from Basic) — or "" on the
    /// Free version before it has chosen.</summary>
    [BindProperty(SupportsGet = true)] public string To { get; set; } = "";
    public bool ToBasic => To == FreePlan.BasicName;
    public bool Down => To == FreePlan.Name;
    /// <summary>The Free version, with nothing chosen yet: both ways up are shown.</summary>
    public bool Choosing => To.Length == 0;

    public Terms.Text TermsText { get; private set; } = null!;
    public Pricing.PriceRise Quote { get; private set; } = null!;
    /// <summary>When the new price starts (2026-10-25): the trial's end, or null — from the next card payment (<see cref="TenantService.UpgradeTrialEnd"/>).</summary>
    public DateTime? TrialEnd { get; private set; }
    /// <summary>Moving down: the computers installed now, which must be no more than the Free version's one.</summary>
    public int Installed { get; private set; }
    public bool MayAccept => Permissions.CanAcceptPrice(MyRole);
    [BindProperty] public bool Accept { get; set; }
    [BindProperty] public string TermsSha { get; set; } = "";
    [BindProperty] public string Agreed { get; set; } = "";

    /// <summary>Where this company may go from here, read from what was asked: anything else is the first way it may go.</summary>
    private bool Settle()
    {
        if (!FreePlan.Is(Tenant.Plan)) return false;
        var ways = TenantService.UpgradesFrom(Tenant.Plan).ToList();
        if (FreePlan.IsBasic(Tenant.Plan)) ways.Add(FreePlan.Name);
        var asked = (To ?? "").Trim().ToLowerInvariant();
        To = ways.Contains(asked) ? asked : FreePlan.IsFree(Tenant.Plan) ? "" : ways[0];
        return true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Settle()) return Redirect("/portal/billing");
        await QuoteAsync();
        return Page();
    }

    /// <summary>Up: no handler name, the form posts to this page's own address.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Settle()) return Redirect("/portal/billing");
        if (Choosing || Down) return Redirect("/portal/upgrade");
        var why = await Tenants.UpgradeAsync(TenantId, MyRole, Me, User.FindFirst(ClaimTypes.Email)?.Value ?? "",
            Accept, TermsSha, Agreed, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", ToBasic ? FreePlan.BasicName : null);
        if (why.Length > 0)
        {
            Error = why;
            await QuoteAsync();
            return Page();
        }
        var t = await Tenants.ByIdAsync(TenantId);
        if (t != null && _hubs.Get(TenantId) is { } hub) await hub.PlanChangedAsync(t);
        // Paying by card (2026-10-13, stage B): straight on to PayFast for the card, first charge when the trial ends. Not
        // finished there, the company is on its trial as before, and Billing (and the trial notices) offer the card again.
        if (t != null && CompanyBilling.Refusal(t, await _payfast.AccountAsync()) == null)
        {
            var back = _payfast.PublicUrl + "/signup/paid?ref={ref}";
            var (co, _) = await _billing.StartAsync(TenantId, "upgrade", Me, back, back + "&cancelled=1");
            if (co != null) { Checkout = co; return Page(); }
        }
        return Redirect("/portal/billing?upgraded=1");
    }

    /// <summary>Down, from the Basic version to the Free version (2026-10-25, §163).</summary>
    public async Task<IActionResult> OnPostDownAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        To = FreePlan.Name;
        if (!Settle() || !Down) return Redirect("/portal/billing");
        var why = await _billing.DowngradeToFreeAsync(TenantId, MyRole, Me, User.FindFirst(ClaimTypes.Email)?.Value ?? "",
            Accept, TermsSha, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
        if (why.Length > 0)
        {
            Error = why;
            await QuoteAsync();
            return Page();
        }
        var t = await Tenants.ByIdAsync(TenantId);
        if (t != null && _hubs.Get(TenantId) is { } hub) await hub.PlanChangedAsync(t);
        return Redirect("/portal/billing?moved=1");
    }

    private async Task QuoteAsync()
    {
        TermsText = Down ? Terms.For(Tenant.Region, FreePlan.Name)
            : ToBasic ? Terms.For(Tenant.Region, FreePlan.BasicName)
            : Terms.For(Tenant.Region);
        await using var db = await _dbf.CreateDbContextAsync();
        var people = await db.Technicians.Where(x => x.TenantId == TenantId).ToListAsync();
        Quote = TenantService.UpgradeQuote(Tenant, people, ToBasic ? FreePlan.BasicName : null);
        TrialEnd = TenantService.UpgradeTrialEnd(Tenant, await Tenants.PaysByCardAsync(TenantId), DateTime.UtcNow);
        if (Down) Installed = await Tenants.InstalledMachinesAsync(TenantId);
    }
}
