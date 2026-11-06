using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// Where a platform admin's set-up link lands (2026-10-13): a password, then the authenticator's QR code and its
/// first code — saved together only then (<see cref="PlatformAdmins.FinishSetupAsync"/>). Opening it spends nothing.
/// </summary>
public sealed class SetupModel : PageModel
{
    private readonly PlatformAdmins _admins;
    public SetupModel(PlatformAdmins admins) => _admins = admins;

    [BindProperty(SupportsGet = true)] public string T { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Again { get; set; } = "";
    [BindProperty] public string Pending { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";

    public PlatformAdmin? Who { get; private set; }
    public string LinkError { get; private set; } = "";
    public string Error { get; private set; } = "";
    public string Secret { get; private set; } = "";
    public string Qr { get; private set; } = "";

    public async Task OnGetAsync() { (Who, LinkError) = await _admins.LookAsync(T); }

    public async Task<IActionResult> OnPostAsync()
    {
        await Task.Delay(Random.Shared.Next(100, 300));
        if (Pending.Length > 0)
        {
            var from = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
            var done = await _admins.FinishSetupAsync(Pending, Code, from);
            if (done.Done == null)
            {
                Error = done.Error; Pending = done.Pending; Secret = done.Secret; Qr = done.Qr;
                (Who, LinkError) = await _admins.LookAsync(T);
                if (Pending.Length == 0 && Who == null) LinkError = done.Error;
                return Page();
            }
            if (done.Trust.Length > 0)
                Response.Cookies.Append(PlatformAdmins.TrustCookie, done.Trust, new CookieOptions
                {
                    HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/owner",
                    Expires = DateTimeOffset.UtcNow.Add(_admins.TrustFor), IsEssential = true
                });
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AdminIdentity.Build(done.Done));
            return Redirect("/owner");
        }

        (Who, LinkError) = await _admins.LookAsync(T);
        if (Who == null) return Page();
        var step = await _admins.ChoosePasswordAsync(T, Password, Again);
        Error = step.Error; Pending = step.Pending; Secret = step.Secret; Qr = step.Qr;
        return Page();
    }
}
