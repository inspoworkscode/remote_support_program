using RemoteSupport.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

/// <summary>
/// Signing a company up, in three steps (2026-10-05, Clint: "when signing up, the system must first ask
/// what country they are from … this will determine pricing and also add that tenant to the relevant
/// server", and "a country relevant disclaimer … which the client must acknowledge"):
///
/// <list type="number">
/// <item><b>Where.</b> One of the five regions, each with its price. A region this server does not hold
/// goes to its own server if there is one, or to a waiting list (<see cref="RegionServers"/>).</item>
/// <item><b>The terms</b> for that region, read and ticked (<see cref="Terms"/>).</item>
/// <item><b>The company</b> and the owner's login — the password is typed on this last step only, so it
/// is never carried between steps.</item>
/// </list>
/// The region and the fingerprint of the terms accepted travel in hidden fields; the service checks
/// both again (<see cref="TenantService.SignupAsync"/>), so a doctored form gains nothing.
/// </summary>
public sealed class SignupModel : PageModel
{
    private readonly TenantService _tenants;
    private readonly IConfiguration _cfg;
    private readonly SitePreview _preview;
    private readonly CompanyBilling _billing;
    private readonly PayfastService _payfast;
    private readonly OpenPlaces _places;
    public SignupModel(TenantService tenants, IConfiguration cfg, RegionServers regions, SitePreview preview, CompanyBilling billing, PayfastService payfast, OpenPlaces places)
    { _tenants = tenants; _cfg = cfg; Regions = regions; _preview = preview; _billing = billing; _payfast = payfast; _places = places; }

    /// <summary>
    /// A place offered from the waiting list (2026-10-22, <see cref="OpenPlaces"/>): carried through every step in a hidden
    /// field, checked here for the page and spent by <see cref="TenantService.SignupAsync"/>.
    /// </summary>
    [BindProperty(SupportsGet = true)] public string Place { get; set; } = "";
    /// <summary>Why the place link cannot be used, when one was given and it cannot.</summary>
    public string PlaceProblem { get; private set; } = "";
    /// <summary>The places now — read once per request, before <see cref="Route"/>.</summary>
    public OpenPlaces.State Places { get; private set; } = new(0, 0, 0);
    /// <summary>A full-version signup must wait: every place is taken and no offered place was brought.</summary>
    private bool MustWait => Places.Full && (Place.Length == 0 || PlaceProblem.Length > 0);

    /// <summary>The places, and whether a place link brought along still works. Every handler asks it first.</summary>
    private async Task ReadPlacesAsync()
    {
        Places = await _places.StateAsync();
        Place = (Place ?? "").Trim();
        if (Place.Length > 0) PlaceProblem = (await _places.FindAsync(Place)).why;
    }

    public RegionServers Regions { get; }

    /// <summary>Public self-service signup is switched off while the platform is in private testing.</summary>
    public bool SignupsEnabled => _preview.SignupsOn(HttpContext);

    /// <summary>A full-version signup in South Africa, made and on its way to PayFast for the card (stage B).</summary>
    public PayfastService.Checkout? Checkout { get; private set; }

    [BindProperty(SupportsGet = true)] public string Region { get; set; } = "";
    /// <summary>"free", "basic" or "full" (2026-10-06; "basic" since 2026-10-25, §163): chosen after the region, before the terms,
    /// because the terms differ.</summary>
    [BindProperty(SupportsGet = true)] public string Plan { get; set; } = "";
    /// <summary>A one-person plan was chosen — the Free version or the Basic version.</summary>
    public bool Free => FreePlan.Is(Plan);
    /// <summary>The Free version itself: nothing to pay, so no card; approved by hand.</summary>
    public bool FreeVersion => FreePlan.IsFree(Plan);
    [BindProperty] public string Country { get; set; } = "";
    /// <summary>Chosen on the company step where the region has several (the US, Canada, Europe) — CompanyClock.</summary>
    [BindProperty] public string TimeZone { get; set; } = "";
    [BindProperty] public string CompanyName { get; set; } = "";
    [BindProperty] public string ContactName { get; set; } = "";
    [BindProperty] public string ContactNumber { get; set; } = "";
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Primary { get; set; } = "";
    [BindProperty] public string Accent { get; set; } = "";
    /// <summary>Where customers email for support (2026-10-09), optional — the start of Tickets by email, finished in the portal.</summary>
    [BindProperty] public string TicketEmail { get; set; } = "";
    [BindProperty] public bool Accept { get; set; }
    [BindProperty] public string TermsSha { get; set; } = "";

    /// <summary>"region", "elsewhere", "waiting", "plan", "terms", "details", "pay" or "done" — the step being shown.</summary>
    public string Step { get; private set; } = "region";
    public string Error { get; private set; } = "";
    public string Message { get; private set; } = "";
    /// <summary>The chosen region, once one is chosen.</summary>
    public Pricing.Region? Chosen { get; private set; }
    /// <summary>The terms being shown or accepted.</summary>
    public Terms.Text? TermsText { get; private set; }
    public string ElsewhereUrl { get; private set; } = "";

    /// <summary>
    /// Kept for the platform owner's country picker (Owner → Tenants) and the waiting list: the countries
    /// a company may have on file. Signup itself asks the region, and for Europe one of
    /// <see cref="Pricing.EuropeanCountries"/>.
    /// </summary>
    public static readonly string[] Countries =
    {
        "South Africa", "United States", "United Kingdom", "Australia", "Canada", "New Zealand", "Ireland", "Germany", "Netherlands", "France", "Spain", "Italy",
        "Portugal", "Belgium", "Switzerland", "Austria", "Sweden", "Norway", "Denmark", "Finland", "Poland", "Namibia", "Botswana", "Zimbabwe", "Zambia", "Mozambique",
        "Kenya", "Nigeria", "Ghana", "Mauritius", "United Arab Emirates", "Saudi Arabia", "India", "Singapore", "Malaysia", "Philippines", "Brazil", "Mexico", "Argentina", "Other"
    };

    /// <summary>A region code as sent, if it is one of the five.</summary>
    private Pricing.Region? Pick(string? code) => Pricing.Regions.FirstOrDefault(r => r.Code == (code ?? "").Trim().ToUpperInvariant());

    public async Task OnGetAsync()
    {
        if (!SignupsEnabled) return;
        await ReadPlacesAsync();
        Chosen = Pick(Region);
        if (Chosen == null) { Step = "region"; return; }
        Route();
    }

    /// <summary>Where a chosen region goes: its terms here, another server, the waiting list — or, for the full version with every place taken, the waiting list for a place.</summary>
    private void Route()
    {
        var (place, url) = Regions.Where(Chosen!.Code);
        if (place == RegionServers.Place.Elsewhere) { Step = "elsewhere"; ElsewhereUrl = $"{url}/signup?region={Chosen.Code}"; return; }
        if (place == RegionServers.Place.NotYet) { Step = "waiting"; return; }
        // Free, Basic or full: asked here, on this server, because each has its own terms.
        Plan = FreePlan.IsFree(Plan) ? FreePlan.Name : FreePlan.IsBasic(Plan) ? FreePlan.BasicName : Plan == "full" ? "full" : "";
        if (Plan.Length == 0) { Step = "plan"; return; }
        if (!Free && MustWait) { Step = "full"; return; }
        Step = "terms";
        TermsText = Terms.For(Chosen.Code, Free ? Plan : null);
    }

    /// <summary>Step two: the terms ticked. Only then is the form for the company shown.</summary>
    public async Task OnPostTermsAsync()
    {
        if (!SignupsEnabled) return;
        await ReadPlacesAsync();
        Chosen = Pick(Region);
        if (Chosen == null) { Step = "region"; return; }
        Route();
        if (Step != "terms") return;
        if (!Accept) { Error = "Please read the terms and tick the box to accept them."; return; }
        if (TermsSha != TermsText!.Sha256) { Error = TenantService.TermsChanged; return; }
        Step = "details";
    }

    /// <summary>Step three: the company, and the account is made.</summary>
    public async Task OnPostCreateAsync()
    {
        if (!SignupsEnabled) { Error = "We are not accepting new signups at the moment."; return; }
        await ReadPlacesAsync();
        Chosen = Pick(Region);
        if (Chosen == null) { Step = "region"; return; }
        Route();
        if (Step != "terms") return;
        var (t, err) = await _tenants.SignupAsync(new TenantService.SignupRequest(Chosen.Code, Country, CompanyName, ContactName, ContactNumber,
            Email, Password, Primary, Accent, Accept, TermsSha, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", TimeZone, Free ? Plan : null, TicketEmail,
            PlaceToken: Free || Place.Length == 0 ? null : Place));
        if (t != null)
        {
            Step = "done";
            // The full version in South Africa (2026-10-13, stage B), and the Basic version since 2026-10-25: the card now, nothing charged for 14 days, and the
            // company switched on by PayFast's notice — nobody approves it. Anywhere card payment does not apply, the
            // signup waits for approval by hand as before — and so does the Free version, which has nothing to pay (§163).
            if (CompanyBilling.Refusal(t, await _payfast.AccountAsync()) == null)
            {
                var back = _payfast.PublicUrl + "/signup/paid?ref={ref}";
                var (co, _) = await _billing.StartAsync(t.Id, "signup", Email.Trim(), back, back + "&cancelled=1");
                if (co != null) { Checkout = co; Step = "pay"; }
            }
            return;
        }
        Error = err;
        // Every place was taken while they filled the form in (OpenPlaces): the waiting list, with their details kept.
        if (OpenPlaces.IsFullRefusal(err)) { Places = await _places.StateAsync(); Step = "full"; Error = ""; return; }
        // Terms not accepted, or changed under them: back to the terms. Anything else: the form again.
        Step = !Accept || err == TenantService.TermsChanged ? "terms" : "details";
    }

    /// <summary>A region with no server yet — or every place taken: tell us who you are, and we will tell you when it opens.</summary>
    public async Task OnPostWaitAsync()
    {
        if (!SignupsEnabled) return;
        await ReadPlacesAsync();
        Chosen = Pick(Region);
        if (Chosen == null) { Step = "region"; return; }
        Route();
        if (Step is not ("waiting" or "full")) return;
        bool full = Step == "full";
        Error = await _tenants.JoinWaitingListAsync(Chosen.Code, Country, CompanyName, ContactName, Email, ContactNumber, full);
        if (Error.Length == 0) Message = full
            ? $"Thank you — you are on the waiting list. We will email {Email.Trim()} as soon as a place opens."
            : $"Thank you — we will email {Email.Trim()} as soon as we open in {Chosen.In}.";
    }
}
