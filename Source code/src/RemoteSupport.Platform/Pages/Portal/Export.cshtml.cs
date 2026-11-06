using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Export your data (roadmap 4.9, 2026-10-22): the owner or a supervisor asks for one zip of the company's clients,
/// tickets and knowledge base, it is made in the background, and it is downloaded here. The owner and supervisors'
/// page (Program.cs), and every rule asked again of <see cref="CompanyExportService"/>.
/// </summary>
public sealed class ExportModel : PortalPage
{
    private readonly CompanyExportService _exports;
    public ExportModel(TenantService t, CompanyExportService exports) : base(t) => _exports = exports;

    public List<CompanyExport> Exports { get; private set; } = new();
    public (int count, long bytes) Recordings { get; private set; }
    /// <summary>One is waiting or being made: the page looks again by itself.</summary>
    public bool Busy => Exports.Any(e => e.State is ExportState.Waiting or ExportState.Making);

    private async Task FillAsync()
    {
        Exports = await _exports.ListAsync(TenantId);
        Recordings = await _exports.RecordingsAsync(TenantId);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        await FillAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostStartAsync(bool recordings)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var (job, error) = await _exports.StartAsync(TenantId, MyRole, TechnicianId > 0 ? TechnicianId : null, Me, recordings);
        if (job == null) { Error = error; await FillAsync(); return Page(); }
        // Post, then redirect: a refresh of the page must never ask for a second one.
        return Redirect("/portal/export?asked=1");
    }

    public async Task<IActionResult> OnGetDownloadAsync(long id)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var d = await _exports.FileForAsync(TenantId, MyRole, id);
        if (d == null) { Error = "That export is no longer there to download. Make a new one."; await FillAsync(); return Page(); }
        return PhysicalFile(d.Path, "application/zip", d.Name);
    }
}
