using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

/// <summary>
/// "Forgot password?" (2026-10-01). Mails a link to choose a new one — and says exactly the same
/// thing whether or not the address has an account, so the page cannot be used to find out who
/// does. The rules, and the limits, are AccountLinks'.
/// </summary>
public sealed class ForgotModel : PageModel
{
    private readonly AccountLinks _links;
    public ForgotModel(AccountLinks links) => _links = links;

    [BindProperty(SupportsGet = true)] public string Email { get; set; } = "";
    public bool Sent { get; private set; }

    public void OnGet() { }

    public async Task OnPostAsync()
    {
        await Task.Delay(Random.Shared.Next(200, 500));
        await _links.ForgotAsync(Email, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?");
        Sent = true;
    }
}
