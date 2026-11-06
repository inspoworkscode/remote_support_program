using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Hub;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

/// <summary>
/// The one downloadable .exe (2026-09-30: "It would be much better if every download was an exe").
/// <c>/download/{slug}</c> serves the SAME signed file to every company, named for the company —
/// <c>anywhere-it-support.exe</c> — and <c>/download/{slug}/brand.json</c> is what that file fetches to
/// learn the company's name, colours, server and key: exactly what the zip's branding\brand.json held,
/// so it is no more public than the zip was. The browser records this address beside the download,
/// which is the first thing the app reads (RemoteSupport.Client.DownloadIdentity).
///
/// <para><c>/download/{slug}/may-add?hw=…</c> (2026-10-06, the free version): whether a computer with
/// that fingerprint would be let on, asked by the installer BEFORE it installs anything, so a third
/// computer is told why and nothing is left behind. It says no more than the hello would.</para>
/// </summary>
public sealed class DownloadModel : PageModel
{
    private readonly TenantService _tenants;
    private readonly PayloadService _payloads;
    private readonly HubRegistry _hubs;
    public DownloadModel(TenantService tenants, PayloadService payloads, HubRegistry hubs) { _tenants = tenants; _payloads = payloads; _hubs = hubs; }

    public async Task<IActionResult> OnGetAsync(string slug, string? what, string? hw)
    {
        var t = await _tenants.BySlugAsync((slug ?? "").ToLowerInvariant());
        if (t == null || !t.IsServiceEnabled) return NotFound();
        if (string.Equals(what, "brand.json", StringComparison.OrdinalIgnoreCase))
            return Content(_payloads.BrandJson(t), "application/json");
        if (string.Equals(what, "may-add", StringComparison.OrdinalIgnoreCase))
        {
            var why = RemoteSupport.Shared.FreePlan.Is(t.Plan) ? _hubs.Get(t.Id)?.FreeWouldRefuse(hw ?? "") : null;
            return new JsonResult(new { ok = why == null, message = why ?? "" });
        }
        if (!string.IsNullOrEmpty(what) || !_payloads.ClientExeAvailable) return NotFound();
        return PhysicalFile(_payloads.ClientExePath, "application/vnd.microsoft.portable-executable", $"{t.Slug}-support.exe");
    }
}
