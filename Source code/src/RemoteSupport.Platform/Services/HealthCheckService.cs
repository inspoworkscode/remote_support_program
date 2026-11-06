using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Receives a monthly health check from a machine, keeps the files, and emails the report using the
/// tenant's own mailbox.
///
/// The machine does the work and builds the PDF - it has Edge and this server does not - then
/// uploads. The mail password lives here and only here, so no customer machine holds one and
/// changing it is one portal edit rather than a visit to every PC. See docs/PLAN-HEALTHCHECK.md.
/// </summary>
public sealed class HealthCheckService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly TenantService _tenants;
    private readonly ILogger<HealthCheckService> _log;
    private readonly string _root;

    public HealthCheckService(IDbContextFactory<PlatformDb> dbf, TenantService tenants, ILogger<HealthCheckService> log, string dataDir)
    {
        _dbf = dbf; _tenants = tenants; _log = log;
        _root = Path.Combine(dataDir, "healthchecks");
        Directory.CreateDirectory(_root);
    }

    public string TenantDir(int tenantId)
    {
        var d = Path.Combine(_root, tenantId.ToString());
        Directory.CreateDirectory(d);
        return d;
    }

    public string FilePath(int tenantId, string fileName) => Path.Combine(TenantDir(tenantId), fileName);

    /// <summary>
    /// Authenticate a machine by the identity it already holds for the websocket. Returns null when
    /// the device is unknown or the key does not match, without saying which - an upload endpoint is
    /// reachable by anything on the network.
    /// </summary>
    public async Task<(Tenant tenant, Device device)?> AuthenticateAsync(string deviceId, string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(deviceKey)) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null) return null;
        // Fixed-length comparison, so a wrong key cannot be narrowed down by timing.
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(device.DeviceKey),
                System.Text.Encoding.UTF8.GetBytes(deviceKey))) return null;
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == device.TenantId);
        if (tenant == null) return null;
        return (tenant, device);
    }

    /// <summary>
    /// What a machine needs to know before it builds its report: which customer it belongs to.
    /// The installer cannot know this - a machine is linked to a client in the portal afterwards -
    /// so the machine asks each month and falls back to its own name if it cannot.
    /// </summary>
    public async Task<string> ClientNameAsync(Device device)
    {
        if (device.ClientId is not int id) return "";
        await using var db = await _dbf.CreateDbContextAsync();
        var c = await db.Clients.FirstOrDefaultAsync(x => x.Id == id);
        return c?.Name ?? "";
    }

    /// <summary>
    /// Whether this machine should have the monthly task at all — asked by the monthly run itself,
    /// which removes its own task when the answer is no. The same rule the hub applies, by the same
    /// function, so the machine's agent and the machine's task can never be told different things.
    /// </summary>
    public async Task<bool> WantedAsync(Tenant tenant, Device device)
    {
        bool ticked = false;
        if (device.ClientId is int id)
        {
            await using var db = await _dbf.CreateDbContextAsync();
            ticked = await db.Clients.AnyAsync(c => c.Id == id && c.TenantId == tenant.Id && c.HealthCheck);
        }
        return RemoteSupport.Shared.HealthCheckModes.Wanted(tenant.HealthCheckReady, device.HealthCheckMode, ticked);
    }

    /// <summary>
    /// Count findings by severity from the collector's own JSON rather than trusting numbers sent
    /// alongside it, so the portal and the report can never disagree. Unknown shapes count nothing
    /// instead of throwing: a run that arrives is worth keeping even if it cannot be summarised.
    /// </summary>
    public static (int crit, int warn, int info, int ok, DateTime runUtc, bool maint, bool restore) Summarise(string json)
    {
        int crit = 0, warn = 0, info = 0, ok = 0;
        var runUtc = DateTime.UtcNow;
        bool maint = false, restore = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("Findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in findings.EnumerateArray())
                {
                    if (!f.TryGetProperty("Severity", out var s) || s.ValueKind != JsonValueKind.String) continue;
                    switch ((s.GetString() ?? "").Trim().ToLowerInvariant())
                    {
                        case "critical": crit++; break;
                        case "warning": warn++; break;
                        case "info": info++; break;
                        case "ok": ok++; break;
                    }
                }
            }

            foreach (var name in new[] { "CollectedUtc", "Collected", "RunDate", "Timestamp" })
                if (root.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(t.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
                { runUtc = parsed; break; }

        }
        catch { }
        return (crit, warn, info, ok, runUtc, maint, restore);
    }

    /// <summary>
    /// Reads the maintenance record, which the endpoint writes as a SEPARATE file from the health
    /// check. Looking for a "Maintenance" section inside the health check JSON found nothing, so
    /// every maintenance run was recorded as "inspection only" - wrong, and wrong in the direction
    /// that makes the product look like it did less than it did.
    ///
    /// The restore point is not a field of its own: it is the task named "Safety restore point",
    /// and MaintenanceRan is already false when it could not be made, because maintenance refuses
    /// to change anything without one.
    /// </summary>
    public static (bool ran, bool restorePoint) SummariseMaintenance(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (false, false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ran = root.TryGetProperty("MaintenanceRan", out var mr)
                      && mr.ValueKind is JsonValueKind.True or JsonValueKind.False && mr.GetBoolean();

            var restore = false;
            if (root.TryGetProperty("Tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tasks.EnumerateArray())
                {
                    if (!t.TryGetProperty("Name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                    if (!string.Equals(n.GetString(), "Safety restore point", StringComparison.OrdinalIgnoreCase)) continue;
                    var note = t.TryGetProperty("Note", out var no) && no.ValueKind == JsonValueKind.String ? no.GetString() : "";
                    restore = string.Equals(note, "Created", StringComparison.OrdinalIgnoreCase);
                    break;
                }
            }
            return (ran, restore);
        }
        catch { return (false, false); }
    }

    /// <summary>Stores an uploaded run and returns it. Does not send: sending is a separate step that may fail on its own.</summary>
    public async Task<HealthCheckRun> RecordAsync(Tenant tenant, Device device, string json, byte[]? pdf, string pdfName, string? maintenanceJson = null)
    {
        var (crit, warn, info, ok, runUtc, _, _) = Summarise(json);
        var (maint, restore) = SummariseMaintenance(maintenanceJson);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var safeMachine = new string((device.MachineName.Length > 0 ? device.MachineName : "machine")
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
        var baseName = $"{safeMachine}-{stamp}";

        var dir = TenantDir(tenant.Id);
        var jsonFile = baseName + ".json";
        await File.WriteAllTextAsync(Path.Combine(dir, jsonFile), json);

        // Kept beside the health check. The IT company reads these, and "what did maintenance
        // actually free on that machine" is not answerable from the client-facing report.
        if (!string.IsNullOrWhiteSpace(maintenanceJson))
            await File.WriteAllTextAsync(Path.Combine(dir, baseName + "-maintenance.json"), maintenanceJson);

        var pdfFile = "";
        if (pdf is { Length: > 0 })
        {
            // Trust the extension, not the sender's file name: the name arrives from a client machine.
            pdfFile = baseName + (Path.GetExtension(pdfName).ToLowerInvariant() == ".html" ? ".html" : ".pdf");
            await File.WriteAllBytesAsync(Path.Combine(dir, pdfFile), pdf);
        }

        var run = new HealthCheckRun
        {
            TenantId = tenant.Id, DeviceId = device.DeviceId, MachineName = device.MachineName,
            ClientId = device.ClientId, RunUtc = runUtc, ReceivedUtc = DateTime.UtcNow,
            Critical = crit, Warning = warn, Info = info, Ok = ok,
            MaintenanceRan = maint, RestorePointMade = restore,
            JsonFile = jsonFile, PdfFile = pdfFile
        };

        await using var db = await _dbf.CreateDbContextAsync();
        db.HealthCheckRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>
    /// Emails a stored run to the address the IT company chose. Records the outcome on the run either
    /// way: a send that failed silently is how a monthly report stops arriving without anyone noticing.
    /// </summary>
    public async Task<string> SendAsync(Tenant tenant, HealthCheckRun run, string clientName)
    {
        var status = await TrySendAsync(tenant, run, clientName);
        await using var db = await _dbf.CreateDbContextAsync();
        var row = await db.HealthCheckRuns.FirstOrDefaultAsync(r => r.Id == run.Id);
        if (row != null)
        {
            row.MailStatus = status;
            row.MailedUtc = status == "sent" ? DateTime.UtcNow : null;
            await db.SaveChangesAsync();
        }
        run.MailStatus = status;
        return status;
    }

    private async Task<string> TrySendAsync(Tenant tenant, HealthCheckRun run, string clientName)
    {
        if (string.IsNullOrWhiteSpace(tenant.HealthCheckEmailTo)) return "no address configured";
        if (string.IsNullOrWhiteSpace(tenant.SmtpHost)) return "no mail server configured";

        string? password = null;
        if (!string.IsNullOrWhiteSpace(tenant.SmtpUser))
        {
            password = _tenants.UnprotectSmtpPassword(tenant.SmtpPasswordEnc);
            // Sending with no credentials after a failed decrypt makes the server answer
            // "authentication required", which reads like a wrong password rather than a password
            // that never arrived. The standalone toolkit learned this one the hard way.
            if (password == null) return "the stored mail password could not be read - save it again";
        }

        try
        {
            var who = string.IsNullOrWhiteSpace(clientName) ? run.MachineName : $"{clientName} ({run.MachineName})";
            var subject = $"[{tenant.CompanyName}] {who} - {run.Critical} urgent, {run.Warning} to plan";

            var body = new System.Text.StringBuilder();
            body.AppendLine($"Machine      : {run.MachineName}");
            if (!string.IsNullOrWhiteSpace(clientName)) body.AppendLine($"Customer     : {clientName}");
            body.AppendLine($"Run finished : {run.RunUtc:yyyy-MM-dd HH:mm} UTC");
            body.AppendLine($"Maintenance  : {(run.MaintenanceRan ? (run.RestorePointMade ? "done, restore point made" : "done") : "not run")}");
            body.AppendLine();
            body.AppendLine($"Needs attention : {run.Critical}");
            body.AppendLine($"To plan         : {run.Warning}");
            body.AppendLine();
            body.AppendLine("The attached report explains each item in plain English and is written for");
            body.AppendLine("the customer to read, so it can be forwarded as it is.");

            using var msg = new MailMessage
            {
                From = new MailAddress(string.IsNullOrWhiteSpace(tenant.SmtpFrom) ? tenant.SmtpUser : tenant.SmtpFrom),
                Subject = subject,
                Body = body.ToString()
            };
            foreach (var to in tenant.HealthCheckEmailTo.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                msg.To.Add(to);
            if (msg.To.Count == 0) return "no address configured";

            FileStream? attach = null;
            if (!string.IsNullOrEmpty(run.PdfFile))
            {
                var p = FilePath(tenant.Id, run.PdfFile);
                if (File.Exists(p))
                {
                    attach = File.OpenRead(p);
                    msg.Attachments.Add(new Attachment(attach, run.PdfFile,
                        run.PdfFile.EndsWith(".pdf") ? "application/pdf" : "text/html"));
                }
            }

            try
            {
                using var smtp = new SmtpClient(tenant.SmtpHost, tenant.SmtpPort > 0 ? tenant.SmtpPort : 587)
                {
                    EnableSsl = tenant.SmtpUseSsl,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Timeout = 60_000
                };
                if (!string.IsNullOrWhiteSpace(tenant.SmtpUser))
                {
                    smtp.UseDefaultCredentials = false;
                    smtp.Credentials = new NetworkCredential(tenant.SmtpUser, password);
                }
                await smtp.SendMailAsync(msg);
            }
            finally { attach?.Dispose(); }

            return "sent";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Health check mail failed for tenant {Tenant}, run {Run}", tenant.Slug, run.Id);
            // Kept short: this is shown in the portal next to the run, not in a log file.
            return ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
        }
    }

    /// <summary>One line of the report, as the collector wrote it. Already in client language.</summary>
    public sealed record Finding(string Category, string Severity, string Title, string Detail, string Recommendation);

    /// <summary>
    /// Reads the findings back out of a stored run. The text is the collector's own - severity is
    /// decided in PowerShell and stays that way, so the same machine reports the same severity every
    /// month and the portal cannot disagree with the PDF the customer was sent.
    /// </summary>
    public List<Finding> FindingsOf(HealthCheckRun run)
    {
        var list = new List<Finding>();
        if (string.IsNullOrEmpty(run.JsonFile)) return list;
        var path = FilePath(run.TenantId, run.JsonFile);
        if (!File.Exists(path)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Findings", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var f in arr.EnumerateArray())
            {
                string S(string n) => f.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                list.Add(new Finding(S("Category"), S("Severity"), S("Title"), S("Detail"), S("Recommendation")));
            }
        }
        catch { }
        // Worst first, so what needs doing is at the top rather than buried under the OKs.
        var order = new Dictionary<string, int> { ["critical"] = 0, ["warning"] = 1, ["info"] = 2, ["ok"] = 3 };
        return list.OrderBy(f => order.TryGetValue(f.Severity.ToLowerInvariant(), out var o) ? o : 4).ToList();
    }

    /// <summary>
    /// Upgrades a run whose report arrived as HTML into a PDF, when the server can. Called before
    /// the mail goes out so the attachment is the better artefact. Leaves the run untouched when
    /// conversion is not possible - the HTML is still a complete, self-contained report.
    /// </summary>
    public async Task<string> TryUpgradeToPdfAsync(HealthCheckRun run, ReportPdfService pdfs)
    {
        if (string.IsNullOrEmpty(run.PdfFile) || run.PdfFile.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return "";

        var htmlPath = FilePath(run.TenantId, run.PdfFile);
        var (pdfPath, reason) = await pdfs.ConvertAsync(htmlPath);
        if (pdfPath == null)
        {
            _log.LogInformation("Kept the HTML report for run {Run}: {Reason}", run.Id, reason);
            return reason;
        }

        var newName = Path.GetFileName(pdfPath);
        await using var db = await _dbf.CreateDbContextAsync();
        var row = await db.HealthCheckRuns.FirstOrDefaultAsync(r => r.Id == run.Id);
        if (row != null) { row.PdfFile = newName; await db.SaveChangesAsync(); }
        run.PdfFile = newName;

        // The HTML is no longer the thing anyone opens, and keeping both invites sending the wrong
        // one. The JSON beside it is the record that matters for a rebuild.
        try { File.Delete(htmlPath); } catch { }
        return "";
    }

    public async Task<List<HealthCheckRun>> RecentAsync(int tenantId, int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.HealthCheckRuns.Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.ReceivedUtc).Take(take).ToListAsync();
    }

    public async Task<HealthCheckRun?> ByIdAsync(int tenantId, int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.HealthCheckRuns.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id);
    }
}
