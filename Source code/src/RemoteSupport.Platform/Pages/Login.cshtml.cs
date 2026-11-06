using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

public sealed class LoginModel : PageModel
{
    private readonly AuthService _auth;
    private readonly TenantService _tenants;
    public LoginModel(AuthService auth, TenantService tenants) { _auth = auth; _tenants = tenants; }

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    // The second step (2026-10-01) — see WebSignIn.
    [BindProperty] public string Mode { get; set; } = "";
    [BindProperty] public string Pending { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    public WebSignIn Step { get; private set; } = new();
    public string Error => Step.Error;

    /// <summary>
    /// Set when a cookie was refused because the person signed in on another browser (one sign-in
    /// at a time, 2026-10-05) — Program.cs, OnValidatePrincipal. Read once, here, and deleted.
    /// </summary>
    public const string SignedOutCookie = "rsp_out";
    public string SignedOutWhy { get; private set; } = "";

    /// <summary>Somebody already signed in goes to their own home, which differs by role.</summary>
    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true && !User.IsInRole("owner"))
            return Redirect(PortalIdentity.HomePage(PortalIdentity.RoleOf(User)));
        if (Request.Cookies[SignedOutCookie] == "elsewhere")
        {
            SignedOutWhy = AuthService.ElsewhereReason;
            Response.Cookies.Delete(SignedOutCookie, new CookieOptions { Path = "/" });
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await Task.Delay(Random.Shared.Next(100, 300));
        Step = await WebSignIn.StepAsync(_auth, Mode, Pending, Email, Password, Code,
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?", HttpContext);
        var session = Step.Session;
        if (session == null) return Page();

        // Technicians sign in here too, since 3.6.3. They used to be turned away with "technicians
        // use the desktop console", which was true when the portal had no idea who you were and
        // every page showed the whole company. Their ticket queue lives in the browser now, so the
        // portal has to be able to tell one person from another — see PortalIdentity.
        var t = await _tenants.ByIdAsync(session.TenantId);
        if (t == null) { Step = new WebSignIn { Error = "That account is not attached to a company." }; return Page(); }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            PortalIdentity.Build(session, t.Slug, session.Appearance));
        // Just set up: signed in, but shown the recovery codes once before going on.
        if (Step.Mode == "saved") { Step.ContinueTo = PortalIdentity.HomePage(session.Role); return Page(); }
        return Redirect(PortalIdentity.HomePage(session.Role));
    }
}
