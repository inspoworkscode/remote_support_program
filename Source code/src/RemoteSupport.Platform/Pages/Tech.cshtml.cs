using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

/// <summary>
/// The technicians' download link, /tech/{slug} — the console's answer to /get/{slug}.
///
/// Clint, 2026-09-30: "There should be a technician download link, much like the client download
/// link so that we dont have to run around with usb sticks every time." The console was already
/// downloadable, but only as a button inside the portal and only for a supervisor or the owner, so
/// every new technician PC was a USB stick.
///
/// Unlike the customers' page it asks the technician to sign in first, with the same email and
/// password as the console, on the page itself: the link can then be pasted anywhere, and only
/// somebody with an account gets the console. Signing in here also signs them in to the portal —
/// the same cookie the login form gives — so a technician who already is goes straight to the button.
/// </summary>
public sealed class TechModel : PageModel
{
    private readonly TenantService _tenants;
    private readonly PayloadService _payloads;
    private readonly AuthService _auth;
    public TechModel(TenantService tenants, PayloadService payloads, AuthService auth)
    { _tenants = tenants; _payloads = payloads; _auth = auth; }

    public Tenant? Tenant { get; private set; }
    public bool HasLogo => Tenant != null && !string.IsNullOrEmpty(Tenant.LogoFile);
    public bool Available => _payloads.TechnicianAvailable;
    /// <summary>The console as one .exe (2026-10-01); the zip only while the payload is an older console.</summary>
    public bool ExeAvailable => _payloads.TechnicianExeAvailable;

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    // The second step (2026-10-01): the same as the portal's sign-in — see WebSignIn.
    [BindProperty] public string Mode { get; set; } = "";
    [BindProperty] public string Pending { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    public WebSignIn Step { get; private set; } = new();
    public string Error => Step.Error;

    /// <summary>Signed in to THIS company's portal, as somebody who may have the console.</summary>
    public bool Allowed => Tenant != null && SignedInHere && Permissions.CanDownloadConsole(PortalIdentity.RoleOf(User));

    /// <summary>Signed in to this company, whether or not they may have the console.</summary>
    public bool SignedInHere => Tenant != null && User.Identity?.IsAuthenticated == true
        && User.FindFirst(PortalIdentity.SlugClaim)?.Value == Tenant.Slug;

    public string Name => User.Identity?.Name ?? "";

    private async Task<bool> LoadAsync(string slug)
    {
        var t = await _tenants.BySlugAsync(slug);
        // The same companies the customers' page answers for: an unapproved or closed company has
        // no link at all.
        Tenant = t != null && t.Status is not (TenantStatus.Pending or TenantStatus.Rejected or TenantStatus.Closed) ? t : null;
        return Tenant != null;
    }

    public async Task OnGetAsync(string slug) => await LoadAsync(slug);

    public async Task<IActionResult> OnPostAsync(string slug)
    {
        if (!await LoadAsync(slug)) return Page();
        await Task.Delay(Random.Shared.Next(100, 300));   // as the login form: no timing tells
        Step = await WebSignIn.StepAsync(_auth, Mode, Pending, Email, Password, Code,
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?", HttpContext);
        var session = Step.Session;
        if (session == null) return Page();
        // Somebody else's account works on their own company's link, not on this one.
        if (session.TenantId != Tenant!.Id) { Step = new WebSignIn { Error = "That account belongs to a different company. Use the link your own company gave you." }; return Page(); }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            PortalIdentity.Build(session, Tenant.Slug, session.Appearance));
        // Just set up: signed in, and shown the recovery codes once before the download button.
        if (Step.Mode == "saved") { Step.ContinueTo = $"/tech/{Tenant.Slug}"; return Page(); }
        // Back to the page by GET, so a refresh does not post the password again — and so the page
        // reads the new cookie rather than the request that carried none.
        return Redirect($"/tech/{Tenant.Slug}");
    }
}
