using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Account;

/// <summary>
/// Where an invitation or a reset link lands (2026-10-01, HANDOVER §73): choose a password, then —
/// on the same page, through the same steps as signing in (WebSignIn) — the authenticator: the QR
/// code for somebody new, the code for somebody resetting. Then signed in, with the recovery codes
/// once, and on to the console download for anybody with a seat.
///
/// Opening it spends nothing (mail filters open every link first); posting the password does.
/// </summary>
public sealed class SetupModel : PageModel
{
    private readonly AccountLinks _links;
    private readonly AuthService _auth;
    private readonly TenantService _tenants;
    private readonly FreeCompanies _free;
    public SetupModel(AccountLinks links, AuthService auth, TenantService tenants, FreeCompanies free) { _links = links; _auth = auth; _tenants = tenants; _free = free; }

    // A free account made by us (2026-10-23, FreeCompanies): its owner accepts the free terms here, before the password,
    // because they never saw a signup page.
    [BindProperty] public bool Accept { get; set; }
    [BindProperty] public string TermsSha { get; set; } = "";
    public Terms.Text? TermsDue { get; private set; }

    [BindProperty(SupportsGet = true)] public string T { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Again { get; set; } = "";
    // The second step, as on the sign-in page.
    [BindProperty] public string Mode { get; set; } = "";
    [BindProperty] public string Pending { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";

    public Technician? Who { get; private set; }
    public bool IsInvite { get; private set; }
    public string LinkError { get; private set; } = "";
    public string Error { get; private set; } = "";
    public WebSignIn Step { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var (who, link, error) = await _links.LookAsync(T);
        Who = who; LinkError = error; IsInvite = link?.Purpose == AccountTokenPurpose.Invite;
        if (who != null && IsInvite) TermsDue = await _free.TermsDueAsync(who);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await Task.Delay(Random.Shared.Next(100, 300));
        var from = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (Mode.Length > 0)
        {
            // The password is chosen and the link spent; this is the code or the QR step.
            Step = await WebSignIn.StepAsync(_auth, Mode, Pending, "", "", Code, from, HttpContext);
            return await Finish();
        }

        var (who, link, lookError) = await _links.LookAsync(T);
        IsInvite = link?.Purpose == AccountTokenPurpose.Invite;
        if (who == null) { LinkError = lookError; return Page(); }
        // The terms first: nothing is spent until they are ticked, and ticked as the very text shown (§84).
        TermsDue = IsInvite ? await _free.TermsDueAsync(who) : null;
        if (TermsDue != null && !Accept) { Who = who; Error = "Please read the terms and tick the box to accept them."; return Page(); }
        if (TermsDue != null && TermsSha != TermsDue.Sha256) { Who = who; Error = TenantService.TermsChanged; return Page(); }
        var (done, error) = await _links.CompleteAsync(T, Password, Again);
        if (done == null) { Who = who; Error = error; return Page(); }
        if (TermsDue != null) await _free.AcceptTermsAsync(done, TermsDue, from);

        // Straight on to the authenticator, as if they had just typed the password at sign-in.
        Who = done;
        Step = await WebSignIn.StepAsync(_auth, "", "", done.Email, Password, "", from, HttpContext);
        return await Finish();
    }

    private async Task<IActionResult> Finish()
    {
        var session = Step.Session;
        if (session == null) return Page();
        var t = await _tenants.ByIdAsync(session.TenantId);
        if (t == null) { Step = new WebSignIn { Error = "That account is not attached to a company." }; return Page(); }
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, PortalIdentity.Build(session, t.Slug, session.Appearance));
        // Anybody with a seat goes on to the console download; everybody else to their portal home.
        var next = session.Role.Has(TenantRoles.Seated) ? $"/tech/{t.Slug}" : PortalIdentity.HomePage(session.Role);
        if (Step.Mode == "saved") { Step.ContinueTo = next; return Page(); }
        return Redirect(next);
    }
}
