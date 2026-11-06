using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

/// <summary>
/// A pilot company signs itself up from the link we sent it (2026-10-15, <see cref="PilotService"/>): the terms — South
/// Africa's, with the pilot agreement ahead of section 1 — then the company and the owner's login, then straight into the
/// portal: Active at once, no trial, no approval, no card.
///
/// <para>Its own address, <c>/pilot/{token}</c>, on purpose: <c>/signup</c> is behind the holding page on the live server
/// and is closed while public signup is, and the link must work through both. The token travels in the address only; the
/// service checks it again when the company is made and spends it in the same save, so a second use, or two at once, fails.</para>
/// </summary>
public sealed class PilotModel : PageModel
{
    private readonly TenantService _tenants;
    private readonly PilotService _pilots;
    public PilotModel(TenantService tenants, PilotService pilots) { _tenants = tenants; _pilots = pilots; }

    /// <summary>Pilots are South African companies (Clint's two); the region's terms and price are South Africa's.</summary>
    public const string Region = "ZA";

    [BindProperty(SupportsGet = true)] public string Token { get; set; } = "";
    [BindProperty] public string CompanyName { get; set; } = "";
    [BindProperty] public string ContactName { get; set; } = "";
    [BindProperty] public string ContactNumber { get; set; } = "";
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Primary { get; set; } = "";
    [BindProperty] public string Accent { get; set; } = "";
    [BindProperty] public string TicketEmail { get; set; } = "";
    [BindProperty] public bool Accept { get; set; }
    [BindProperty] public string TermsSha { get; set; } = "";

    /// <summary>"refused", "terms", "details" or "done".</summary>
    public string Step { get; private set; } = "terms";
    public string Error { get; private set; } = "";
    public Pricing.Region Chosen => Pricing.For(Region);
    public Terms.Text TermsText { get; } = Terms.For(Region, plan: null, pilot: true);

    /// <summary>The link is good: fill in what it was made for, and show the terms.</summary>
    public async Task OnGetAsync()
    {
        var (invite, why) = await _pilots.FindAsync(Token);
        if (why.Length > 0) { Step = "refused"; Error = why; return; }
        CompanyName = invite!.CompanyName; Email = invite.Email;
        Step = "terms";
    }

    /// <summary>
    /// Every box as text, never null. Binding turns an EMPTY box into null (§130), and a link made without an email posts its
    /// empty Email box on every tick of the terms — that threw, and answered 500 on the live server (2026-10-16, pilotcheck).
    /// </summary>
    private void Tidy()
    {
        Token ??= ""; CompanyName ??= ""; ContactName ??= ""; ContactNumber ??= ""; Email ??= ""; Password ??= "";
        Primary ??= ""; Accent ??= ""; TicketEmail ??= ""; TermsSha ??= "";
    }

    /// <summary>The terms ticked: the company form. Its own handler, or a post would land back on the terms (§84).</summary>
    public async Task OnPostTermsAsync()
    {
        Tidy();
        var (invite, why) = await _pilots.FindAsync(Token);
        if (why.Length > 0) { Step = "refused"; Error = why; return; }
        if (CompanyName.Trim().Length == 0) CompanyName = invite!.CompanyName;
        if (Email.Trim().Length == 0) Email = invite!.Email;
        if (!Accept) { Error = "Please read the terms and tick the box to accept them."; Step = "terms"; return; }
        if (TermsSha != TermsText.Sha256) { Error = TenantService.TermsChanged; Step = "terms"; return; }
        Step = "details";
    }

    /// <summary>The company and its owner, made — and the link spent with them.</summary>
    public async Task OnPostCreateAsync()
    {
        Tidy();
        var (t, err) = await _tenants.SignupAsync(new TenantService.SignupRequest(Region, null, CompanyName, ContactName, ContactNumber,
            Email, Password, Primary, Accent, Accept, TermsSha, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", "", null, TicketEmail,
            PilotToken: Token));
        if (t != null) { Step = "done"; return; }
        Error = err;
        Step = PilotService.IsLinkProblem(err) ? "refused" : err == TenantService.TermsChanged || !Accept ? "terms" : "details";
    }
}
