using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// What the monthly checks actually found, newest first. This is where absorbing the toolkit pays:
/// standalone it had nowhere to show anything, and a month's results lived in a mailbox.
/// </summary>
public sealed class HealthChecksModel : PortalPage
{
    private readonly HealthCheckService _hc;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public HealthChecksModel(TenantService t, HealthCheckService hc, IDbContextFactory<PlatformDb> dbf) : base(t)
    { _hc = hc; _dbf = dbf; }

    public sealed class Row
    {
        public HealthCheckRun Run = null!;
        public string ClientName = "";
        public List<HealthCheckService.Finding> Findings = new();
    }

    public List<Row> Rows { get; private set; } = new();
    public Dictionary<string, DateTime> LastRunByMachine { get; private set; } = new();

    /// <summary>Capped: each row reads its findings from disk, so this is a page of results, not an archive.</summary>
    private const int PageSize = 25;

    private async Task<bool> LoadPageAsync()
    {
        if (!await LoadAsync()) return false;

        var runs = await _hc.RecentAsync(TenantId, PageSize);

        await using var db = await _dbf.CreateDbContextAsync();
        var clientNames = await db.Clients.Where(c => c.TenantId == TenantId)
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        Rows = runs.Select(r => new Row
        {
            Run = r,
            // The name as it was when the run was recorded, from the id copied onto the run - not a
            // live join, so a machine that changed hands does not rewrite its own history.
            ClientName = r.ClientId.HasValue && clientNames.TryGetValue(r.ClientId.Value, out var n) ? n : "",
            Findings = _hc.FindingsOf(r)
        }).ToList();

        LastRunByMachine = Rows.GroupBy(r => r.Run.MachineName)
            .ToDictionary(g => g.Key, g => g.Max(r => r.Run.RunUtc));
        return true;
    }

    public async Task<IActionResult> OnGetAsync() => await LoadPageAsync() ? Page() : Redirect("/login");

    /// <summary>
    /// Serves the stored report. Scoped to the signed-in company by looking the run up within the
    /// tenant, so an id from another company's report is a 404 rather than a download.
    /// </summary>
    public async Task<IActionResult> OnGetReportAsync(int id)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var run = await _hc.ByIdAsync(TenantId, id);
        if (run == null || string.IsNullOrEmpty(run.PdfFile)) return NotFound();
        var path = _hc.FilePath(TenantId, run.PdfFile);
        if (!System.IO.File.Exists(path)) return NotFound();

        var download = $"{run.MachineName}-{run.RunUtc:yyyy-MM}" + Path.GetExtension(run.PdfFile);
        return PhysicalFile(path, run.PdfFile.EndsWith(".pdf") ? "application/pdf" : "text/html", download);
    }

    public static string Pill(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => "pill-bad",
        "warning" => "pill-warn",
        "ok" => "pill-ok",
        _ => "pill-muted"
    };
}
