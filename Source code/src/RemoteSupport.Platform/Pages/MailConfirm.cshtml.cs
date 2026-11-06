using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

/// <summary>
/// The link mailed to a support address that is to be forwarded to us (3.8.4). Opening it only SHOWS
/// what it confirms; pressing Confirm (a POST) is what confirms — mail filters open every link in a
/// message, and a GET that did the deed would be done by a robot before the person saw it (§73's rule).
/// </summary>
public sealed class MailConfirmModel : PageModel
{
    private readonly TicketIntake _intake;
    public MailConfirmModel(TicketIntake intake) => _intake = intake;

    [BindProperty(SupportsGet = true)] public string? T { get; set; }
    public string Company { get; private set; } = "";
    public string Address { get; private set; } = "";
    public string Error { get; private set; } = "";
    public bool Done { get; private set; }

    public async Task OnGetAsync()
    {
        if (await _intake.PeekConfirmationAsync(T) is { } found) (Company, Address) = found;
        else Error = "This link has expired or has already been used.";
    }

    public async Task OnPostAsync()
    {
        if (await _intake.PeekConfirmationAsync(T) is { } found) (Company, Address) = found;
        Error = await _intake.ConfirmAsync(T);
        Done = Error.Length == 0;
    }
}
