using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Where Microsoft sends the browser back after "Sign in with Microsoft" (2026-10-19, §151) — the redirect URI
/// registered on our Entra app, so its address never changes: <see cref="MicrosoftSignIn.CallbackPath"/>. Owner and
/// supervisors only (the page policy in Program.cs, as for Tickets by email itself).
///
/// It draws nothing. It checks the answer is for a sign-in THIS person started for THIS company in THIS browser in the
/// last 15 minutes, exchanges the code, keeps the sign-in, and goes back to Tickets by email with a short code saying
/// how it went — which that page tests and puts in words. A refresh of this address can do nothing: the code is spent
/// and the cookie gone.
/// </summary>
public sealed class MailIntakeMicrosoftModel : PortalPage
{
    private readonly TicketIntake _intake;
    private readonly MicrosoftSignIn _ms;
    public MailIntakeMicrosoftModel(TenantService t, TicketIntake intake, MicrosoftSignIn ms) : base(t) { _intake = intake; _ms = ms; }

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, string? error_description, string? admin_consent)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanManageCompany(MyRole)) return Forbid();
        const string back = "/portal/mailintake?ms=";
        var cookie = Request.Cookies[MicrosoftSignIn.CookieName];
        Response.Cookies.Delete(MicrosoftSignIn.CookieName, new CookieOptions { Path = "/portal/mailintake" });

        // An administrator approving the app for their organisation (the link under the button) gives no code.
        if (string.Equals(admin_consent, "True", StringComparison.OrdinalIgnoreCase)) return Redirect(back + "approved");
        if (!string.IsNullOrEmpty(error)) return Redirect(back + MicrosoftSignIn.CallbackError(error, error_description));

        if (_ms.CheckReturn(state, cookie, TenantId, TechnicianId) is not { } started || string.IsNullOrEmpty(code))
            return Redirect(back + "expired");
        var a = await _ms.RedeemAsync(code, started.verifier, started.nonce, HttpContext.RequestAborted);
        if (!a.Ok) return Redirect(back + (a.Failure == MsFailure.Ours ? "ours" : "failed"));
        if (!TicketIntake.LooksLikeEmail(a.User)) return Redirect(back + "noaccount");
        await _intake.ConnectMicrosoftAsync(TenantId, a, Me);
        return Redirect(back + "connected");
    }
}
