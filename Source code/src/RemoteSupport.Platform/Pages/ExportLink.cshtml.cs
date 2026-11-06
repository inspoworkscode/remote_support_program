using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

/// <summary>
/// The link we email a company's owners when one of us has made its export (2026-10-22, <see cref="CompanyExportService"/>):
/// for a company that can no longer sign in — suspended, or closed and within the 60 days its terms give. No sign-in; the
/// token in the address is the only key, 256 random bits, kept on the server as its SHA-256 only, good for
/// <see cref="CompanyExportService.KeepDays"/> days.
///
/// <para><b>Opening the link downloads nothing</b>: the page says what it is and the download is a button (a POST). Mail
/// filters open every link in a message, and one opening this would otherwise pull a company's whole export.</para>
/// </summary>
public sealed class ExportLinkModel : PageModel
{
    private readonly CompanyExportService _exports;
    public ExportLinkModel(CompanyExportService exports) => _exports = exports;

    [BindProperty(SupportsGet = true)] public string Token { get; set; } = "";
    public CompanyExportService.Download? Found { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Found = await _exports.ByLinkAsync(Token, counting: false);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        var d = await _exports.ByLinkAsync(Token, counting: true);
        if (d == null) { Found = null; return Page(); }
        return PhysicalFile(d.Path, "application/zip", d.Name);
    }
}
