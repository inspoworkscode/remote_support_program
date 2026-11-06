using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

public sealed class BillingModel : PortalPage
{
    private readonly PayfastService _payfast;
    private readonly CompanyBilling _billing;
    private readonly AdminCompanies _companies;
    private readonly RemoteSupport.Platform.Hub.HubRegistry _hubs;
    public BillingModel(TenantService t, PayfastService payfast, CompanyBilling billing, AdminCompanies companies, RemoteSupport.Platform.Hub.HubRegistry hubs) : base(t)
    { _payfast = payfast; _billing = billing; _companies = companies; _hubs = hubs; }

    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)] public int Upgraded { get; set; }
    /// <summary>Arrived from moving down to the Free version (§163).</summary>
    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)] public int Moved { get; set; }

    /// <summary>The month's price with the people in seats now (2026-10-05): the package, and any seats past it we allowed (2026-10-25).</summary>
    public Pricing.Bill Bill { get; private set; } = null!;
    /// <summary>The most seats this company may fill — five, or what we allowed.</summary>
    public int Most => Pricing.MostSeatsFor(Tenant);
    /// <summary>The people in seats, and the owner when they are not one of them — so the count can be checked by eye.</summary>
    public List<Technician> People { get; private set; } = new();
    /// <summary>Every higher price the owner agreed to, newest first.</summary>
    public List<SeatAgreement> Agreements { get; private set; } = new();
    /// <summary>The terms the company accepted at signup, word for word — none for a company made on the command line.</summary>
    public TermsAcceptance? TermsAccepted { get; private set; }

    /// <summary>Paying by card (2026-10-13, stage B): the subscription the company pays by, if any.</summary>
    public PayfastSubscription? Card { get; private set; }
    /// <summary>Why this company cannot pay by card here, or null when it can (CompanyBilling.Refusal).</summary>
    public string? CardRefusal { get; private set; }
    /// <summary>Only the owner puts a card on the company — the same people who agree its price.</summary>
    public bool MayPay => Permissions.CanAcceptPrice(MyRole);
    public string CardUpdateUrl { get; private set; } = "";
    /// <summary>What the company has paid by card, newest first (stage C).</summary>
    public List<PayfastPayment> Payments { get; private set; } = new();
    /// <summary>A checkout to hand to PayFast: the page posts it as soon as it loads.</summary>
    public PayfastService.Checkout? Checkout { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        await FillAsync();
        // Arrived from Upgrade (2026-10-06): said once, here, where the new price and terms are. Since §163 it may be to the Basic version.
        if (Upgraded == 1 && !RemoteSupport.Shared.FreePlan.IsFree(Tenant.Plan))
            Message = (RemoteSupport.Shared.FreePlan.IsBasic(Tenant.Plan) ? $"Upgraded to {RemoteSupport.Shared.FreePlan.TheName}." : "Upgraded to the full version.")
                + (Tenant.Status == TenantStatus.Trial ? $" Your {Services.TenantService.UpgradeTrialDays}-day free trial has started" : " The new price is charged from your next payment")
                + " — everything you had is still here.";
        // Moved down from the Basic version (§163).
        if (Moved == 1 && RemoteSupport.Shared.FreePlan.IsFree(Tenant.Plan))
            Message = $"You are on {RemoteSupport.Shared.FreePlan.FreeTheName} now. Nothing more is charged, and your computer is reached as before, with its password.";
        return Page();
    }

    /// <summary>The owner adds the company's card: on to PayFast, R0.00 now, the month's price from the end of the trial.</summary>
    public async Task<IActionResult> OnPostCardAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!MayPay) { Error = "Only the owner can add the company's card."; await FillAsync(); return Page(); }
        var back = _payfast.PublicUrl + "/signup/paid?ref={ref}";
        var (co, error) = await _billing.StartAsync(TenantId, "card", Me, back, back + "&cancelled=1");
        if (co == null) Error = error;
        Checkout = co;
        await FillAsync();
        return Page();
    }

    /// <summary>The owner cancels the card subscription (stage D): PayFast first, then ours; the service runs to the end of the month paid for.</summary>
    public async Task<IActionResult> OnPostCancelAsync(bool sure)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!MayPay) Error = "Only the owner can cancel the subscription.";
        else if (!sure) Error = "Tick the box to say you mean it, then press Cancel the subscription.";
        else
        {
            var (error, ends) = await _billing.CancelAsync(TenantId, Me);
            if (error.Length > 0) Error = error;
            else Message = string.CompareOrdinal(ends, CompanyClock.Now.ToString("yyyy-MM-dd")) >= 0
                ? $"Cancelled. Nothing more will be charged, and your service carries on until the end of {ends}. We have emailed you."
                : "Cancelled. Nothing more will be charged. The time you had paid for had already ended, so the service stops now.";
        }
        await FillAsync();
        return Page();
    }

    /// <summary>A rise in the Basic version's computers the owner is being asked to accept (2026-10-25); null when none is asked.</summary>
    public Pricing.PriceRise? MachinesQuestion { get; private set; }
    /// <summary>The computers installed now, as the hub counts them.</summary>
    public int Installed { get; private set; }

    /// <summary>
    /// The Basic version's computers (2026-10-25, Clint: extra machines at R10 each, no approval from us, up to 20). A rise is
    /// shown first and posted again with its figure (<see cref="Pricing.PriceRise.Token"/>); fewer goes straight through. The hub
    /// is told at once, so the next install is let on (or a console's count is right) without anybody signing in again.
    /// </summary>
    public async Task<IActionResult> OnPostMachinesAsync(int machines, string? agreed)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (agreed == null && TenantService.BasicMachinesRise(Tenant, machines) is { } rise
            && machines <= RemoteSupport.Shared.FreePlan.MostMachines && Permissions.CanAcceptPrice(MyRole))
        {
            MachinesQuestion = rise;
            await FillAsync();
            return Page();
        }
        var why = await Tenants.SetBasicMachinesAsync(TenantId, machines, MyRole, agreed,
            $"{Me} ({User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value})");
        if (why.Length > 0) Error = why;
        else
        {
            await LoadAsync();
            if (_hubs.Get(TenantId) is { } hub) await hub.PlanChangedAsync(Tenant);
            Message = $"You now pay for {RemoteSupport.Shared.FreePlan.MachinesFor(Tenant.Plan, Tenant.BasicMachines)} computers.";
        }
        await FillAsync();
        return Page();
    }

    /// <summary>
    /// The owner asks us for more seats (2026-10-25): kept on the company for Owner → Approvals, and we are emailed. Nothing
    /// changes for them until we allow it; then they add people on People and agree each seat's price there.
    /// </summary>
    public async Task<IActionResult> OnPostSeatsAsync(int seats, string? note)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var by = $"{Me} ({User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value})";
        var (asked, error) = await Tenants.AskForSeatsAsync(TenantId, seats, note, MyRole, by);
        if (asked == null) Error = error;
        else
        {
            await _companies.TellUsOfSeatRequestAsync(asked);
            Message = $"Thank you — we have your request for {asked.SeatsAskedFor} seats, and will email you when we have looked at it.";
            await LoadAsync();   // the page shows the request from the company as it now is
        }
        await FillAsync();
        return Page();
    }

    private async Task FillAsync()
    {
        People = await Tenants.TechniciansAsync(TenantId);
        Bill = Pricing.BillFor(Tenant, Pricing.PeopleInSeats(People));
        if (Bill.Basic) Installed = await Tenants.InstalledMachinesAsync(TenantId);
        Agreements = await Tenants.SeatAgreementsAsync(TenantId);
        TermsAccepted = await Tenants.TermsAcceptedAsync(TenantId);
        var acct = await _payfast.AccountAsync();
        CardRefusal = CompanyBilling.Refusal(Tenant, acct);
        Card = await _payfast.CurrentForAsync(TenantId);
        Payments = await _billing.PaymentsAsync(TenantId);
        if (Card is { Status: PayfastStatus.Active, Token: { } token } && acct != null)
            CardUpdateUrl = _payfast.CardUpdateUrl(Card.Sandbox, token, _payfast.PublicUrl + "/portal/billing");
    }
}
