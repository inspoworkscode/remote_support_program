using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// A company takes its data with it (roadmap 4.9, Clint 2026-10-22: "the ability for companies to export their
/// clients, tickets and knowledgebase"). Section 11 of the terms already promised it — "Ask us before then for an
/// export of your data", within the 60 days after an agreement ends — and until now that meant somebody on the
/// server by hand. Every rule is here.
///
/// <para><b>Clint's four answers (2026-10-22), all the recommendation:</b></para>
/// <list type="number">
/// <item><b>Who</b>: the owner and supervisors — <see cref="Permissions.CanExportCompany"/>, asked here as well as by
/// the page's policy.</item>
/// <item><b>A company that cannot sign in</b> (suspended, or closed and within its 60 days) <b>is exported by us</b>:
/// a platform admin presses Export on Owner → All companies (<see cref="StartForOwnersAsync"/>, through
/// <c>ControlServers.Dispatch</c> so it reaches whichever server holds the company), and when it is made the owners
/// are emailed a link that works without signing in, for <see cref="KeepDays"/> days. Nobody shut out is let back
/// in to fetch it, and the terms' "ask us" stays true.</item>
/// <item><b>The format</b>: one zip of Excel workbooks and the files themselves. <c>clients.xlsx</c>'s first sheet is
/// EXACTLY the columns Clients → Import clients reads (<see cref="ClientImport.TemplateHeaders"/>), so the file goes
/// straight back in — here or into another program. Excel, not CSV, because a CSV opened in Excel loses a phone
/// number's leading zero; every cell is text.</item>
/// <item><b>Session recordings</b> are a tick box, off by default: they are listed either way, copied only when
/// ticked — one can be gigabytes.</item>
/// </list>
///
/// <para><b>It is a background job, never one request</b>: a big company's photos are gigabytes and minutes of work.
/// <see cref="CompanyExportSweep"/> makes one at a time, writing to a <c>.part</c> file renamed when complete, so a
/// half-made export is never offered; one cut short by a restart is begun again. The zip lives in
/// <c>data/exports/{company}</c> for <see cref="KeepDays"/> days, and a newer one replaces it. <b>The backup leaves
/// <c>exports</c> out</b> (deploy/rsp-backup.sh): everything in it is already in the backup once.</para>
///
/// <para>Left out on purpose: <b>device passwords</b> (stored encrypted for the technician at the bench; a zip that
/// travels by email link is no place for them — the tickets say only whether there was one), deleted articles,
/// an article's older versions, staged photos never attached to a ticket, and files picked for a comment never posted.</para>
/// </summary>
public sealed class CompanyExportService
{
    /// <summary>How long a made export is kept for download, and how long the emailed link works.</summary>
    public const int KeepDays = 7;

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ILogger<CompanyExportService> _log;
    private readonly PlatformMailService _mail;
    private readonly PlatformAdmins _admins;
    private readonly AdminCompanies _companies;
    private readonly TicketPhotoService _photos;
    private readonly TicketAttachmentService _files;
    private readonly KnowledgeBaseService _kb;
    private readonly RecordingService _recordings;
    private readonly string _root;
    private readonly string _publicUrl;
    private readonly SemaphoreSlim _wake = new(0);

    public CompanyExportService(IDbContextFactory<PlatformDb> dbf, ILogger<CompanyExportService> log, PlatformMailService mail,
        PlatformAdmins admins, AdminCompanies companies, TicketPhotoService photos, TicketAttachmentService files,
        KnowledgeBaseService kb, RecordingService recordings, IConfiguration cfg, string dataDir)
    {
        _dbf = dbf; _log = log; _mail = mail; _admins = admins; _companies = companies;
        _photos = photos; _files = files; _kb = kb; _recordings = recordings;
        _root = Path.Combine(dataDir, "exports");
        _publicUrl = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
    }

    public const string Refusal = "Only the owner or a supervisor can export the company's data.";

    // ================================================================== asking for one

    /// <summary>
    /// The company's own request, from Portal → Export. One at a time: while one is waiting or being made, asking again
    /// is answered with that one rather than a second.
    /// </summary>
    public async Task<(CompanyExport? job, string error)> StartAsync(int tenantId, TenantRole role, int? technicianId, string who, bool recordings)
    {
        if (!Permissions.CanExportCompany(role)) return (null, Refusal);
        await using var db = await _dbf.CreateDbContextAsync();
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId)) return (null, "That company no longer exists.");
        var going = await InFlight(db, tenantId);
        if (going != null) return (going, "");
        var job = new CompanyExport { TenantId = tenantId, RequestedBy = who, RequestedById = technicianId, IncludeRecordings = recordings, Progress = "Waiting to start" };
        db.CompanyExports.Add(job);
        await db.SaveChangesAsync();
        _wake.Release();
        return (job, "");
    }

    /// <summary>
    /// One of us makes it for a company that cannot sign in (Clint's answer: "we make it for them"), and the owners are
    /// emailed a link when it is ready. Any status — that is the point; a Manager or above, as for every change to a company.
    /// </summary>
    public async Task<AdminCompanies.Done> StartForOwnersAsync(PlatformAdmins.Actor by, int tenantId, bool recordings)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return AdminCompanies.Done.No(AdminCompanies.ViewerRefusal);
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.FindAsync(tenantId);
        if (t == null) return AdminCompanies.Done.No("That company no longer exists.");
        var owners = await _companies.OwnersAsync(tenantId);
        if (owners.Count == 0) return AdminCompanies.Done.No($"{t.CompanyName} has no owner or support address to send the link to.");
        var going = await InFlight(db, tenantId);
        if (going != null) return AdminCompanies.Done.No($"An export of {t.CompanyName} is already being made ({going.Progress}).");
        db.CompanyExports.Add(new CompanyExport
        {
            TenantId = tenantId, RequestedBy = by.Name, ByUs = true, IncludeRecordings = recordings, Progress = "Waiting to start"
        });
        await db.SaveChangesAsync();
        _wake.Release();
        await _admins.LogAsync(by.Name, $"Export started{(recordings ? ", with recordings" : "")}", t.CompanyName);
        return new("", $"Export of {t.CompanyName} started. When it is made, {string.Join(", ", owners.Select(o => o.email))} "
                       + $"{(owners.Count == 1 ? "is" : "are")} emailed a link to download it, good for {KeepDays} days.");
    }

    private static Task<CompanyExport?> InFlight(PlatformDb db, int tenantId) =>
        db.CompanyExports.Where(e => e.TenantId == tenantId && (e.State == ExportState.Waiting || e.State == ExportState.Making))
            .OrderBy(e => e.Id).FirstOrDefaultAsync();

    /// <summary>The company's exports, newest first — what Portal → Export lists.</summary>
    public async Task<List<CompanyExport>> ListAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CompanyExports.Where(e => e.TenantId == tenantId).OrderByDescending(e => e.Id).Take(10).ToListAsync();
    }

    /// <summary>How many recordings the company has, and their size — said beside the tick box.</summary>
    public async Task<(int count, long bytes)> RecordingsAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var sizes = await db.SessionRecordings.Where(r => r.TenantId == tenantId).Select(r => r.Bytes).ToListAsync();
        return (sizes.Count, sizes.Sum());
    }

    // ================================================================== downloading

    public sealed record Download(string Path, string Name, CompanyExport Job, string CompanyName);

    /// <summary>A signed-in owner or supervisor downloading their own company's — never another company's id.</summary>
    public async Task<Download?> FileForAsync(int tenantId, TenantRole role, long id)
    {
        if (!Permissions.CanExportCompany(role)) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        var job = await db.CompanyExports.FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId);
        return await Served(db, job);
    }

    /// <summary>The emailed link's export (one we made), while it is good. Nothing about the company without the token.</summary>
    public async Task<Download?> ByLinkAsync(string? token, bool counting)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        var hash = Hash(token);
        var job = await db.CompanyExports.FirstOrDefaultAsync(e => e.LinkHash == hash && e.ByUs);
        return counting ? await Served(db, job) : await Peek(db, job);
    }

    private async Task<Download?> Peek(PlatformDb db, CompanyExport? job)
    {
        if (job is not { State: ExportState.Ready } || job.ExpiresUtc < DateTime.UtcNow) return null;
        var path = Path.Combine(TenantDir(job.TenantId), job.FileName);
        if (!File.Exists(path)) return null;
        var t = await db.Tenants.FindAsync(job.TenantId);
        var company = t?.CompanyName ?? "Company";
        var day = job.FinishedUtc ?? DateTime.UtcNow;
        return new Download(path, $"{SafeName(company)} - export {day:yyyy-MM-dd}.zip", job, company);
    }

    private async Task<Download?> Served(PlatformDb db, CompanyExport? job)
    {
        var d = await Peek(db, job);
        if (d == null) return null;
        d.Job.Downloads++; d.Job.DownloadedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return d;
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()))).ToLowerInvariant();

    private string TenantDir(int tenantId) => Path.Combine(_root, tenantId.ToString(CultureInfo.InvariantCulture));

    // ================================================================== the background half

    /// <summary>Waits until one is asked for, or <paramref name="most"/> passes — whichever is first.</summary>
    public Task WaitAsync(TimeSpan most, CancellationToken stop) => _wake.WaitAsync(most, stop);

    /// <summary>
    /// Once at start: anything left "being made" was cut short by a restart, so it waits again — and its half-made
    /// file goes.
    /// </summary>
    public async Task RecoverAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        foreach (var e in await db.CompanyExports.Where(e => e.State == ExportState.Making).ToListAsync())
        {
            e.State = ExportState.Waiting; e.Progress = "Waiting to start again (the server restarted)";
            TryDelete(Path.Combine(TenantDir(e.TenantId), e.FileName + ".part"));
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One pass: expired files deleted, then every waiting export made, oldest first, one at a time. Returns how many
    /// were made or failed.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken stop = default)
    {
        await ExpireAsync();
        int n = 0;
        while (!stop.IsCancellationRequested)
        {
            long id;
            await using (var db = await _dbf.CreateDbContextAsync())
            {
                var next = await db.CompanyExports.Where(e => e.State == ExportState.Waiting).OrderBy(e => e.Id).FirstOrDefaultAsync(stop);
                if (next == null) break;
                next.State = ExportState.Making; next.StartedUtc = DateTime.UtcNow; next.Progress = "Starting";
                next.FileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..40] + ".zip";
                await db.SaveChangesAsync(stop);
                id = next.Id;
            }
            await MakeAsync(id, stop);
            n++;
        }
        return n;
    }

    private async Task ExpireAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        foreach (var e in await db.CompanyExports.Where(e => e.State == ExportState.Ready && e.ExpiresUtc < now).ToListAsync())
        {
            TryDelete(Path.Combine(TenantDir(e.TenantId), e.FileName));
            e.State = ExportState.Expired;
        }
        await db.SaveChangesAsync();
    }

    private async Task MakeAsync(long id, CancellationToken stop)
    {
        CompanyExport job;
        Tenant tenant;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            job = await db.CompanyExports.SingleAsync(e => e.Id == id, stop);
            tenant = await db.Tenants.SingleAsync(t => t.Id == job.TenantId, stop);
        }
        var dir = TenantDir(job.TenantId);
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, job.FileName + ".part");
        try
        {
            var made = await new Maker(this, job, tenant, part, stop).RunAsync();
            File.Move(part, Path.Combine(dir, job.FileName), overwrite: true);
            List<CompanyExport> older;
            await using (var db = await _dbf.CreateDbContextAsync())
            {
                var e = await db.CompanyExports.SingleAsync(x => x.Id == id, CancellationToken.None);
                e.State = ExportState.Ready; e.FinishedUtc = DateTime.UtcNow; e.ExpiresUtc = DateTime.UtcNow.AddDays(KeepDays);
                e.Bytes = new FileInfo(Path.Combine(dir, job.FileName)).Length; e.Summary = made; e.Progress = "";
                // A newer export replaces the one before: only the latest is kept.
                older = await db.CompanyExports.Where(x => x.TenantId == job.TenantId && x.Id != id && x.State == ExportState.Ready).ToListAsync(CancellationToken.None);
                foreach (var o in older) { TryDelete(Path.Combine(dir, o.FileName)); o.State = ExportState.Expired; }
                await db.SaveChangesAsync(CancellationToken.None);
                job = e;
            }
            _log.LogInformation("Export {Id} of {Company} made: {Summary}, {Bytes} bytes", id, tenant.CompanyName, made, job.Bytes);
            await TellAsync(job, tenant);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopping: RecoverAsync begins it again next time.
            TryDelete(part);
        }
        catch (Exception ex)
        {
            TryDelete(part);
            _log.LogError(ex, "Export {Id} of {Company} failed", id, tenant.CompanyName);
            await using var db = await _dbf.CreateDbContextAsync();
            var e = await db.CompanyExports.SingleAsync(x => x.Id == id);
            e.State = ExportState.Failed; e.FinishedUtc = DateTime.UtcNow; e.Progress = "";
            e.Error = ex is ExportProblem p ? p.Message : "Something went wrong while making it. Try again; if it fails again, tell us.";
            await db.SaveChangesAsync();
            if (e.ByUs) await _admins.LogAsync(e.RequestedBy, $"Export FAILED: {e.Error}", tenant.CompanyName);
        }
    }

    /// <summary>A failure worth saying in words on the page, rather than "something went wrong".</summary>
    private sealed class ExportProblem(string message) : Exception(message);

    /// <summary>Who is told it is ready: the person who asked, or — one we made — the owners, with the link.</summary>
    private async Task TellAsync(CompanyExport job, Tenant t)
    {
        if (job.ByUs)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            await using (var db = await _dbf.CreateDbContextAsync())
            {
                var e = await db.CompanyExports.SingleAsync(x => x.Id == job.Id);
                e.LinkHash = Hash(token);
                await db.SaveChangesAsync();
            }
            var owners = await _companies.OwnersAsync(t.Id);
            var body = new StringBuilder();
            body.Append("Hello,\n\n");
            body.Append($"As asked, here is an export of {t.CompanyName}'s data on The Remote Support Program: your clients and their\n");
            body.Append("contacts, your tickets with their story, quote lines, photos and files, and your knowledge base.\n\n");
            body.Append($"Download it ({Size(job.Bytes)}): {_publicUrl}/export/{token}\n\n");
            body.Append($"The link works for {KeepDays} days, until {job.ExpiresUtc:d MMMM yyyy}. It needs no sign-in, so keep this email to\n");
            body.Append("yourselves. Inside, README.txt says what each file is; clients.xlsx can be imported straight into another program.\n\n");
            body.Append("The Remote Support Program\n");
            var r = owners.Count == 0 ? "no owner or support address" :
                await _mail.SendAsync(owners.Select(o => o.email), $"{t.CompanyName}: your data export is ready", body.ToString(),
                    "The Remote Support Program", null, copyOwner: false);
            await _admins.LogAsync(job.RequestedBy, r == "sent"
                ? $"Export ready ({Size(job.Bytes)}), link emailed to {string.Join(", ", owners.Select(o => o.email))}"
                : $"Export ready ({Size(job.Bytes)}) but the link was NOT emailed: {r}", t.CompanyName);
            return;
        }
        if (job.RequestedById is not int who || _publicUrl.Length == 0) return;
        await using var db2 = await _dbf.CreateDbContextAsync();
        var person = await db2.Technicians.FindAsync(who);
        if (person == null || !person.Email.Contains('@')) return;
        var mail = $"Hello {person.DisplayName},\n\nThe export of {t.CompanyName}'s data you asked for is ready ({Size(job.Bytes)}).\n\n"
                   + $"Download it from {_publicUrl}/portal/export — it is kept for {KeepDays} days.\n\nThe Remote Support Program\n";
        var sent = await _mail.SendAsync(new[] { person.Email }, $"{t.CompanyName}: your data export is ready", mail, "The Remote Support Program", null, copyOwner: false);
        if (sent != "sent") _log.LogInformation("Export {Id}: the ready email did not go: {Why}", job.Id, sent);
    }

    private async Task ProgressAsync(long id, string text)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await db.CompanyExports.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Progress, text));
    }

    // ================================================================== words and names

    public static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>
    /// A name a folder or file can have on Windows, the Mac and Linux alike: none of \/:*?"&lt;&gt;| or control
    /// characters, no trailing dot or space, not a device name, and not so long that a deep path breaks.
    /// </summary>
    public static string SafeName(string? s, int most = 80)
    {
        var sb = new StringBuilder();
        foreach (var c in (s ?? "").Normalize(NormalizationForm.FormC))
            sb.Append(c < 32 || "\\/:*?\"<>|".Contains(c) ? ' ' : c);
        var name = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (name.Length > most) name = name[..most].TrimEnd('.', ' ');
        if (name.Length == 0) name = "Untitled";
        if (Reserved.Contains(name) || Reserved.Contains(Path.GetFileNameWithoutExtension(name))) name = "_" + name;
        return name;
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    // ================================================================== making one

    /// <summary>One export being made: the zip, the workbooks, and the names already used in each folder.</summary>
    private sealed class Maker
    {
        private readonly CompanyExportService _s;
        private readonly CompanyExport _job;
        private readonly Tenant _t;
        private readonly string _part;
        private readonly CancellationToken _stop;
        private readonly TimeZoneInfo _zone;
        private readonly string _scratch;
        private readonly Dictionary<string, HashSet<string>> _used = new(StringComparer.OrdinalIgnoreCase);
        private int _filesIn, _filesMissing;

        public Maker(CompanyExportService s, CompanyExport job, Tenant t, string part, CancellationToken stop)
        {
            _s = s; _job = job; _t = t; _part = part; _stop = stop;
            _zone = CompanyClock.Find(t.TimeZone);
            _scratch = part + ".tmp";
        }

        private string When(DateTime? utc) => utc is { } u ? u.Local(_zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        private static string Day(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        private static string Money(decimal? m) => m?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        private static string YesNo(bool b) => b ? "Yes" : "";

        /// <summary>A name not yet used in that folder of the zip: "Report.pdf", then "Report (2).pdf".</summary>
        private string Unique(string folder, string name)
        {
            if (!_used.TryGetValue(folder, out var used)) _used[folder] = used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
            if (ext.Length > 10) { stem = name; ext = ""; }
            var candidate = name;
            for (int i = 2; !used.Add(candidate); i++) candidate = $"{stem} ({i}){ext}";
            return folder.Length == 0 ? candidate : folder + "/" + candidate;
        }

        public async Task<string> RunAsync()
        {
            await CheckRoomAsync();
            Directory.CreateDirectory(_scratch);
            try
            {
                await using var fs = new FileStream(_part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                var clients = await ClientsAsync(zip);
                var (tickets, files) = await TicketsAsync(zip);
                var articles = await KnowledgeAsync(zip);
                var summary = $"{Count(clients, "client")}, {Count(tickets, "ticket")}, {Count(articles, "article")}, {Count(files, "file")}"
                              + (_filesMissing > 0 ? $" ({_filesMissing} missing on the server)" : "");
                Text(zip, "README.txt", ReadMe(summary));
                return summary;
            }
            finally { try { Directory.Delete(_scratch, true); } catch (IOException) { } }
        }

        private static string Count(int n, string what) => $"{n.ToString("#,0", CultureInfo.InvariantCulture)} {what}{(n == 1 ? "" : "s")}";

        /// <summary>Refuses rather than fills the server's disk: the files' sizes, twice over for the zip and its scratch.</summary>
        private async Task CheckRoomAsync()
        {
            await using var db = await _s._dbf.CreateDbContextAsync();
            int id = _t.Id;
            long need = await db.TicketPhotos.Where(p => p.TenantId == id && p.TicketId != null).SumAsync(p => (long?)p.Bytes) ?? 0;
            need += await db.TicketAttachments.Where(a => a.TenantId == id && a.NoteId != null).SumAsync(a => (long?)a.Bytes) ?? 0;
            need += await db.KbImages.Where(i => i.TenantId == id).SumAsync(i => (long?)i.Bytes) ?? 0;
            if (_job.IncludeRecordings) need += await db.SessionRecordings.Where(r => r.TenantId == id).SumAsync(r => (long?)r.Bytes) ?? 0;
            try
            {
                var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_part))!).AvailableFreeSpace;
                if (free < need + 512L * 1024 * 1024)
                    throw new ExportProblem($"There is not enough room on the server to make it ({Size(need)} of files, {Size(free)} free). Tell us, and we will make room.");
            }
            catch (ArgumentException) { /* a path DriveInfo cannot read: go ahead */ }
        }

        private void Text(ZipArchive zip, string path, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(true));
            w.Write(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        }

        /// <summary>A workbook made in the scratch folder, then put in the zip.</summary>
        private async Task Book(ZipArchive zip, string path, Func<Spreadsheet.Workbook, Task> fill, string cutNote = "[cut here — longer than Excel holds in one cell]")
        {
            var tmp = Path.Combine(_scratch, Guid.NewGuid().ToString("N") + ".xlsx");
            await using (var fs = File.Create(tmp))
            using (var wb = new Spreadsheet.Workbook(fs, cutNote))
                await fill(wb);
            zip.CreateEntryFromFile(tmp, path, CompressionLevel.NoCompression);   // an .xlsx is a zip already
            File.Delete(tmp);
        }

        /// <summary>A stored file copied in; false (and counted) when it is gone from the server's disk.</summary>
        private bool Copy(ZipArchive zip, string from, string to)
        {
            _stop.ThrowIfCancellationRequested();
            if (!File.Exists(from)) { _filesMissing++; return false; }
            var ext = Path.GetExtension(to).ToLowerInvariant();
            var level = ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".mp4" or ".zip" or ".7z" or ".rar" or ".gz" or ".docx" or ".xlsx" or ".pptx" or ".pdf"
                ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            zip.CreateEntryFromFile(from, to, level);
            _filesIn++;
            return true;
        }

        // ---------------------------------------------------------------- clients

        private async Task<int> ClientsAsync(ZipArchive zip)
        {
            await _s.ProgressAsync(_job.Id, "Clients");
            await using var db = await _s._dbf.CreateDbContextAsync();
            var clients = await db.Clients.AsNoTracking().Where(c => c.TenantId == _t.Id).OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync(_stop);
            var people = (await db.Contacts.AsNoTracking().Where(c => c.TenantId == _t.Id).OrderBy(c => c.Id).ToListAsync(_stop))
                .GroupBy(c => c.ClientId).ToDictionary(g => g.Key, g => g.ToList());

            await Book(zip, "clients.xlsx", wb =>
            {
                // Sheet 1 is Import clients' own columns, nothing more, so the file goes straight back in. One row per
                // contact person; the client's own details first (see ImportRows).
                wb.Sheet("Clients", ClientImport.TemplateHeaders, new double[] { 40, 26, 18, 32, 40, 40 });
                foreach (var c in clients.Where(c => !c.Archived))
                    foreach (var r in ImportRows(c, people.GetValueOrDefault(c.Id) ?? new()))
                        wb.Row(r);

                // Sheet 2 is everything, archived clients and contacts too, for the record.
                wb.Sheet("Every client and contact", new[]
                {
                    "Client", "Client archived", "Monthly health check", "Client added", "Address", "Client notes",
                    "Client's own contact person", "Client's own phone", "Client's own email",
                    "Contact person", "Contact role", "Contact phone", "Contact email", "Contact notes", "Contact archived", "Contact added"
                }, new double[] { 36, 10, 10, 16, 36, 36, 24, 16, 28, 24, 18, 16, 28, 30, 10, 16 });
                foreach (var c in clients)
                {
                    string?[] Head() => new[] { c.Name, YesNo(c.Archived), YesNo(c.HealthCheck), When(c.CreatedUtc), c.Address, c.Notes, c.ContactName, c.Phone, c.Email };
                    var mine = people.GetValueOrDefault(c.Id) ?? new();
                    if (mine.Count == 0) wb.Row(Head());
                    foreach (var p in mine)
                        wb.Row(Head().Concat(new[] { p.Name, p.Role, p.Phone, p.Email, p.Notes, YesNo(p.Archived), When(p.CreatedUtc) }).ToArray());
                }
                return Task.CompletedTask;
            });
            return clients.Count;
        }

        /// <summary>
        /// A client as Import clients reads it back. Import takes the client's own phone, email, address and notes from
        /// its FIRST row, and a contact person from every row with one. So: a client with no contact people on file is
        /// one row of its own details. Otherwise the client's own contact person comes first — and when the client's own
        /// phone or email is not that person's, a row with NO contact person carries them ahead of everybody, so Import
        /// files them on the client and makes nobody up. Then one row per contact person. The address goes on every row
        /// (another program may read one row per person); the notes only on the first, where Import reads them.
        /// Contact people are what come back exactly; the client's own legacy fields are filled as Import fills blanks.
        /// </summary>
        public static List<string?[]> ImportRows(Client c, List<Contact> all)
        {
            var rows = new List<string?[]>();
            var people = all.Where(p => !p.Archived).ToList();
            if (people.Count == 0)
            {
                rows.Add(new string?[] { c.Name, c.ContactName, c.Phone, c.Email, c.Address, c.Notes });
                return rows;
            }
            string Key(string s) => ClientImport.NameKey(s);
            var own = people.FirstOrDefault(p => c.ContactName.Length > 0 && Key(p.Name) == Key(c.ContactName));
            bool ownMatches = own != null && own.Phone == c.Phone && own.Email == c.Email;
            if (!ownMatches && (c.Phone.Length > 0 || c.Email.Length > 0))
                rows.Add(new string?[] { c.Name, "", c.Phone, c.Email, c.Address, c.Notes });
            var ordered = own == null ? people : people.Where(p => p != own).Prepend(own);
            foreach (var p in ordered)
                rows.Add(new string?[] { c.Name, p.Name, p.Phone, p.Email, c.Address, rows.Count == 0 ? c.Notes : "" });
            return rows;
        }

        // ---------------------------------------------------------------- tickets

        private static string Kind(PhotoKind k) => k switch
        {
            PhotoKind.Before => "Photo as it arrived", PhotoKind.After => "Photo as it went out", PhotoKind.Part => "Photo of a part",
            _ => "Picture of the screen"
        };
        private static string KindFile(PhotoKind k) => k switch
        {
            PhotoKind.Before => "arrived", PhotoKind.After => "went out", PhotoKind.Part => "part", _ => "screen"
        };

        private static string NoteKind(TicketNote n) =>
            n.WentBack ? "Went back" : n.FromCustomer ? "From the customer" : n.IsSystem ? "Automatic" : n.IsWorkDone ? "Work done"
            : n.FormTitle.Length > 0 || n.MovedTo.Length > 0 ? "Step" : "Comment";

        private static string Source(TicketSource s) => s switch
        {
            TicketSource.Manual => "Opened by hand", TicketSource.Call => "Support request", TicketSource.Chat => "Chat",
            TicketSource.Unattended => "Unattended session", TicketSource.Email => "Email", TicketSource.Form => "Contact form",
            TicketSource.Recurring => "Recurring task", _ => s.ToString()
        };

        private async Task<(int tickets, int files)> TicketsAsync(ZipArchive zip)
        {
            await using var db = await _s._dbf.CreateDbContextAsync();
            int id = _t.Id;
            var tickets = await db.Tickets.AsNoTracking().Where(t => t.TenantId == id).OrderBy(t => t.Number).ToListAsync(_stop);
            var numberOf = tickets.ToDictionary(t => t.Id, t => t.Number);
            string Num(int? ticketId) => ticketId is int x && numberOf.TryGetValue(x, out var n) ? "#" + n.ToString(CultureInfo.InvariantCulture) : "";
            var clientName = await db.Clients.Where(c => c.TenantId == id).ToDictionaryAsync(c => c.Id, c => c.Name, _stop);
            var machines = (await db.TicketMachines.AsNoTracking().Where(m => m.TenantId == id).OrderBy(m => m.Id).ToListAsync(_stop))
                .GroupBy(m => m.TicketId).ToDictionary(g => g.Key, g => g.ToList());
            var photos = (await db.TicketPhotos.AsNoTracking().Where(p => p.TenantId == id && p.TicketId != null).OrderBy(p => p.Id).ToListAsync(_stop))
                .GroupBy(p => p.TicketId!.Value).ToDictionary(g => g.Key, g => g.ToList());
            var attached = (await db.TicketAttachments.AsNoTracking().Where(a => a.TenantId == id && a.NoteId != null).OrderBy(a => a.Id).ToListAsync(_stop))
                .GroupBy(a => a.TicketId).ToDictionary(g => g.Key, g => g.ToList());
            var recordings = (await db.SessionRecordings.AsNoTracking().Where(r => r.TenantId == id).OrderBy(r => r.Id).ToListAsync(_stop))
                .GroupBy(r => r.TicketId).ToDictionary(g => g.Key, g => g.ToList());
            var lines = (await db.QuoteLines.AsNoTracking().Where(q => q.TenantId == id).OrderBy(q => q.Id).ToListAsync(_stop))
                .GroupBy(q => q.TicketId).ToDictionary(g => g.Key, g => g.ToList());

            // Four workbooks written side by side as the tickets go by — each its own scratch file, so one ticket's
            // story never waits in memory for the rest.
            var books = new[] { "tickets", "ticket-story", "quote-lines", "ticket-files" }
                .ToDictionary(n => n, n => Path.Combine(_scratch, n + ".xlsx"));
            var streams = books.ToDictionary(b => b.Key, b => (Stream)File.Create(b.Value));
            const string cut = "[cut here — the whole text is in that ticket's story.txt]";
            var wbs = streams.ToDictionary(s => s.Key, s => new Spreadsheet.Workbook(s.Value, cut));
            int files = 0;
            try
            {
                wbs["tickets"].Sheet("Tickets", TicketHeaders, new double[] { 9, 36, 48, 14, 24, 11, 9, 16, 28, 22, 26, 22, 18, 18, 20, 16, 16, 16 });
                wbs["ticket-story"].Sheet("Story", new[] { "Ticket", "When", "Who", "Stage", "Step", "Moved to", "Kind", "Gone back over", "Words", "Files" },
                    new double[] { 9, 16, 20, 22, 24, 22, 14, 16, 80, 40 });
                wbs["quote-lines"].Sheet("Quote lines", new[] { "Ticket", "Item", "Part number", "Serial number", "Quantity", "Added by", "Added", "Photos" },
                    new double[] { 9, 36, 18, 18, 9, 20, 16, 50 });
                wbs["ticket-files"].Sheet("Files", new[] { "Ticket", "What", "Machine", "Quote line", "File in this export", "Original name", "Size (bytes)", "By", "When" },
                    new double[] { 9, 22, 22, 26, 50, 30, 12, 20, 16 });
                var others = new List<string?[]>();

                int done = 0;
                foreach (var chunk in tickets.Chunk(200))
                {
                    _stop.ThrowIfCancellationRequested();
                    await _s.ProgressAsync(_job.Id, $"Tickets: {done.ToString("#,0", CultureInfo.InvariantCulture)} of {tickets.Count.ToString("#,0", CultureInfo.InvariantCulture)}");
                    var ids = chunk.Select(t => t.Id).ToList();
                    var notes = (await db.TicketNotes.AsNoTracking().Where(n => n.TenantId == id && ids.Contains(n.TicketId)).OrderBy(n => n.Id).ToListAsync(_stop))
                        .GroupBy(n => n.TicketId).ToDictionary(g => g.Key, g => g.ToList());
                    foreach (var t in chunk)
                    {
                        var folder = $"tickets/{t.Number.ToString(CultureInfo.InvariantCulture)}";
                        var num = "#" + t.Number.ToString(CultureInfo.InvariantCulture);
                        var mine = machines.GetValueOrDefault(t.Id) ?? new();
                        string MachineName(int? machineId) => machineId is int m && mine.FirstOrDefault(x => x.Id == m) is { } tm
                            ? (tm.Name.Length > 0 ? tm.Name : "Another machine") : (mine.Count > 0 ? $"{t.AssetMake} {t.AssetModel}".Trim() : "");
                        var myLines = lines.GetValueOrDefault(t.Id) ?? new();

                        // The files first, so the story and the quote lines can name where each went.
                        var lineFiles = new Dictionary<long, List<string>>();
                        var noteFiles = new Dictionary<long, List<string>>();
                        var counters = new Dictionary<string, int>();
                        foreach (var p in photos.GetValueOrDefault(t.Id) ?? new())
                        {
                            var key = KindFile(p.Kind);
                            counters[key] = counters.GetValueOrDefault(key) + 1;
                            var ext = Path.GetExtension(p.FileName);
                            var to = Unique(folder + "/photos", $"{key} {counters[key]}{ext}");
                            bool there = Copy(zip, _s._photos.FilePath(id, p.FileName), to);
                            var line = p.QuoteLineId is long ql ? myLines.FirstOrDefault(l => l.Id == ql) : null;
                            if (line != null) (lineFiles.TryGetValue(line.Id, out var lf) ? lf : lineFiles[line.Id] = new()).Add(to);
                            wbs["ticket-files"].Row(num, Kind(p.Kind), MachineName(p.MachineId), line?.ItemName ?? "", there ? to : "missing on the server",
                                p.OriginalName, p.Bytes.ToString(CultureInfo.InvariantCulture), p.UploadedBy, When(p.UploadedUtc));
                            files++;
                        }
                        foreach (var a in attached.GetValueOrDefault(t.Id) ?? new())
                        {
                            var to = Unique(folder + "/files", SafeName(a.OriginalName.Length > 0 ? a.OriginalName : a.FileName, 120));
                            bool there = Copy(zip, _s._files.FilePath(id, a.FileName), to);
                            (noteFiles.TryGetValue(a.NoteId!.Value, out var nf) ? nf : noteFiles[a.NoteId!.Value] = new()).Add(there ? to : a.OriginalName + " (missing)");
                            wbs["ticket-files"].Row(num, "File on a comment", "", "", there ? to : "missing on the server",
                                a.OriginalName, a.Bytes.ToString(CultureInfo.InvariantCulture), a.UploadedBy, When(a.UploadedUtc));
                            files++;
                        }
                        foreach (var r in recordings.GetValueOrDefault(t.Id) ?? new())
                        {
                            string where;
                            if (_job.IncludeRecordings)
                            {
                                var to = Unique(folder + "/recordings", $"{r.RecordedUtc.Local(_zone):yyyy-MM-dd HHmm} {SafeName(r.MachineName, 40)}.mp4");
                                where = Copy(zip, _s._recordings.FilePath(id, r.FileName), to) ? to : "missing on the server";
                                files++;
                            }
                            else where = "not included — tick Include session recordings";
                            wbs["ticket-files"].Row(num, "Session recording", r.MachineName, "", where,
                                $"{r.Seconds / 60}:{r.Seconds % 60:00} long", r.Bytes.ToString(CultureInfo.InvariantCulture), r.RecordedBy, When(r.RecordedUtc));
                        }

                        var story = notes.GetValueOrDefault(t.Id) ?? new();
                        wbs["tickets"].Row(TicketRow(t, Num(t.ParentTicketId), Num(t.BookingId == t.Id ? null : t.BookingId),
                            t.ClientId is int c ? clientName.GetValueOrDefault(c, "") : "", folder + "/story.txt"));
                        foreach (var m in mine)
                            others.Add(new[] { num, m.Purpose, m.Make, m.Model, m.Serial, m.Accessories, YesNo(m.PasswordProtected), m.AddedBy, When(m.AddedUtc) });
                        foreach (var n in story)
                            wbs["ticket-story"].Row(num, When(n.CreatedUtc), n.Author, Workflow.Label(n.Stage) is { Length: > 0 } s ? s : n.Stage, n.FormTitle,
                                Workflow.Label(n.MovedTo) is { Length: > 0 } mt ? mt : n.MovedTo, NoteKind(n),
                                n.UndoneUtc != null ? $"{When(n.UndoneUtc)} by {n.UndoneBy}" : "", n.Body,
                                string.Join("\n", noteFiles.GetValueOrDefault(n.Id) ?? new()));
                        foreach (var l in myLines)
                            wbs["quote-lines"].Row(num, l.ItemName, l.PartNumber, l.SerialNumber, l.Quantity.ToString(CultureInfo.InvariantCulture),
                                l.AddedBy, When(l.CreatedUtc), string.Join("\n", lineFiles.GetValueOrDefault(l.Id) ?? new()));

                        Text(zip, folder + "/story.txt", StoryText(t, t.ClientId is int c2 ? clientName.GetValueOrDefault(c2, "") : "", story, noteFiles, mine));
                        done++;
                    }
                }
                wbs["tickets"].Sheet("Other machines on a job", new[] { "Ticket", "What it is on the job", "Make", "Model", "Serial number", "Accessories", "Password protected", "Added by", "Added" },
                    new double[] { 9, 36, 16, 20, 18, 26, 10, 20, 16 });
                foreach (var r in others) wbs["tickets"].Row(r);
            }
            finally
            {
                foreach (var w in wbs.Values) w.Dispose();
                foreach (var s in streams.Values) await s.DisposeAsync();
            }
            foreach (var (name, path) in books) { zip.CreateEntryFromFile(path, name + ".xlsx", CompressionLevel.NoCompression); File.Delete(path); }
            return (tickets.Count, files);
        }

        public static readonly string[] TicketHeaders =
        {
            "Ticket", "Subject", "Description", "Category", "Stage", "Status", "Priority", "Source", "Client", "Contact person",
            "Requester email", "Assigned to", "Worked by", "Opened by", "Machine", "Opened", "Last changed", "Closed",
            "Part of ticket", "Booked in with ticket", "Site address", "Appointment", "Make", "Model", "Serial number", "Accessories",
            "Password protected", "Repairable", "Outcome", "Time spent (minutes)", "Quote amount", "Quote reference", "Quote asked of",
            "Invoice amount", "Invoice number", "Payment method", "Payment reference", "Not invoiced because", "Abandoned because",
            "Collected or delivered", "Collected or delivered by", "Delivered", "Due on", "Story"
        };

        private string?[] TicketRow(Ticket t, string parent, string booking, string client, string story) => new[]
        {
            "#" + t.Number.ToString(CultureInfo.InvariantCulture), t.Subject, t.Description, TicketService.CategoryName(t.Category),
            Workflow.Label(t.Stage) is { Length: > 0 } s ? s : t.Stage, TicketSummary.StatusNames.ElementAtOrDefault((int)t.Status) ?? t.Status.ToString(),
            TicketSummary.PriorityNames.ElementAtOrDefault((int)t.Priority) ?? t.Priority.ToString(), Source(t.Source), client, t.ContactName,
            t.RequesterEmail, t.AssignedTo, t.WorkedBy, t.OpenedBy, t.MachineName, When(t.CreatedUtc), When(t.UpdatedUtc), When(t.ClosedUtc),
            parent, booking, t.SiteAddress, When(t.AppointmentUtc), t.AssetMake, t.AssetModel, t.AssetSerial, t.Accessories,
            YesNo(t.DevicePasswordProtected), t.Repairable switch { true => "Yes", false => "No", _ => "" }, t.Outcome,
            t.TimeSpentMinutes?.ToString(CultureInfo.InvariantCulture), Money(t.QuoteAmount), t.QuoteReference, t.QuoteSupplierNames,
            Money(t.InvoiceAmount), t.InvoiceNumber, t.PaymentMethod, t.PaymentReference, t.DoNotInvoiceReason, t.AbandonedReason,
            When(t.CollectedUtc), t.CollectedBy, YesNo(t.Delivered), Day(t.DueOn), story
        };

        /// <summary>One ticket as a person reads it: what it is, then every entry in order, words in full.</summary>
        private string StoryText(Ticket t, string client, List<TicketNote> story, Dictionary<long, List<string>> noteFiles, List<TicketMachine> others)
        {
            var b = new StringBuilder();
            b.Append($"Ticket #{t.Number}: {t.Subject}\n");
            b.Append(new string('=', Math.Min(78, 10 + t.Subject.Length))).Append('\n');
            void Line(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) b.Append($"{label}: {value}\n"); }
            Line("Category", TicketService.CategoryName(t.Category));
            Line("Stage", Workflow.Label(t.Stage) is { Length: > 0 } s ? s : t.Stage);
            Line("Status", TicketSummary.StatusNames.ElementAtOrDefault((int)t.Status));
            Line("Client", client);
            Line("Contact person", t.ContactName);
            Line("Assigned to", t.AssignedTo);
            Line("Machine", t.MachineName);
            Line("Booked-in machine", $"{t.AssetMake} {t.AssetModel}".Trim() + (t.AssetSerial.Length > 0 ? $", serial {t.AssetSerial}" : ""));
            foreach (var m in others) Line("Also on this job", $"{m.Name}{(m.Serial.Length > 0 ? $", serial {m.Serial}" : "")}{(m.Purpose.Length > 0 ? $" — {m.Purpose}" : "")}");
            Line("Opened", $"{When(t.CreatedUtc)} by {t.OpenedBy}".Trim());
            Line("Closed", When(t.ClosedUtc));
            if (t.Description.Trim().Length > 0) b.Append("\nDescription\n-----------\n").Append(t.Description.Trim()).Append('\n');
            b.Append("\nThe story\n---------\n");
            if (story.Count == 0) b.Append("(nothing written on it)\n");
            foreach (var n in story)
            {
                b.Append('\n').Append(When(n.CreatedUtc)).Append("  ").Append(n.Author.Length > 0 ? n.Author : "(automatic)");
                var kind = NoteKind(n);
                if (n.FormTitle.Length > 0) b.Append(" — ").Append(n.FormTitle);
                else if (kind != "Comment") b.Append(" — ").Append(kind);
                if (n.MovedTo.Length > 0) b.Append(" → ").Append(Workflow.Label(n.MovedTo) is { Length: > 0 } to ? to : n.MovedTo);
                if (n.UndoneUtc != null) b.Append($"  [gone back over {When(n.UndoneUtc)} by {n.UndoneBy}]");
                b.Append('\n');
                foreach (var l in n.Body.Replace("\r\n", "\n").Split('\n')) b.Append("    ").Append(l).Append('\n');
                foreach (var f in noteFiles.GetValueOrDefault(n.Id) ?? new()) b.Append("    File: ").Append(f).Append('\n');
            }
            return b.ToString();
        }

        // ---------------------------------------------------------------- the knowledge base

        private async Task<int> KnowledgeAsync(ZipArchive zip)
        {
            await _s.ProgressAsync(_job.Id, "Knowledge base");
            await using var db = await _s._dbf.CreateDbContextAsync();
            int id = _t.Id;
            var clientName = await db.Clients.Where(c => c.TenantId == id).ToDictionaryAsync(c => c.Id, c => c.Name, _stop);
            var folders = await db.KbFolders.AsNoTracking().Where(f => f.TenantId == id).ToListAsync(_stop);
            var articles = await db.KbArticles.AsNoTracking().Where(a => a.TenantId == id && a.DeletedUtc == null)
                .OrderBy(a => a.ClientId).ThenBy(a => a.Title).ToListAsync(_stop);
            var images = await db.KbImages.AsNoTracking().Where(i => i.TenantId == id).OrderBy(i => i.Id).ToListAsync(_stop);

            // Every picture the company has, by its number — an article names one as [image:12].
            var pictures = new Dictionary<long, string>();
            var kbDir = _s._kb.TenantDir(id);
            foreach (var img in images)
            {
                var to = $"knowledge-base/pictures/{img.Id.ToString(CultureInfo.InvariantCulture)}{TicketPhotoService.ExtensionFor(img.ContentType)}";
                if (Copy(zip, Path.Combine(kbDir, img.FileName), to)) pictures[img.Id] = to;
            }

            string Section(int? clientId) => clientId is int c ? "Clients/" + SafeName(clientName.GetValueOrDefault(c, $"Client {c}")) : "General";
            string SectionName(int? clientId) => clientId is int c ? clientName.GetValueOrDefault(c, "") : "General";
            // One zip folder per knowledge base folder, decided once — two folders of the same name side by side get two.
            var folderPath = new Dictionary<int, string>();
            string PathOf(int folderId)
            {
                if (folderPath.TryGetValue(folderId, out var known)) return known;
                var f = folders.First(x => x.Id == folderId);
                var parent = f.ParentId is int p && folders.Any(x => x.Id == p) ? PathOf(p) : "knowledge-base/" + Section(f.ClientId);
                return folderPath[folderId] = Unique(parent, SafeName(f.Name, 60));
            }
            foreach (var f in folders.OrderBy(f => f.Id)) PathOf(f.Id);

            await Book(zip, "knowledge-base.xlsx", wb =>
            {
                wb.Sheet("Articles", new[] { "Section", "Folder", "Title", "File in this export", "Written up from ticket", "Version", "Written by", "Written", "Last changed by", "Last changed" },
                    new double[] { 26, 36, 40, 60, 12, 9, 20, 16, 20, 16 });
                foreach (var a in articles)
                {
                    _stop.ThrowIfCancellationRequested();
                    var dir = a.FolderId is int f && folderPath.TryGetValue(f, out var fp) ? fp : "knowledge-base/" + Section(a.ClientId);
                    var file = Unique(dir, SafeName(a.Title, 80) + ".html");
                    Text(zip, file, ArticleHtml(a, SectionName(a.ClientId), dir, pictures));
                    Text(zip, Path.ChangeExtension(file, ".txt"), a.Body);
                    _used[dir].Add(Path.GetFileName(Path.ChangeExtension(file, ".txt")));
                    wb.Row(SectionName(a.ClientId), a.FolderId is int ff ? KnowledgeBaseService.PathText(folders, ff) : "", a.Title, file,
                        a.FromTicketNumber > 0 ? "#" + a.FromTicketNumber.ToString(CultureInfo.InvariantCulture) : "",
                        a.Version.ToString(CultureInfo.InvariantCulture), a.CreatedBy, When(a.CreatedUtc), a.UpdatedBy, When(a.UpdatedUtc));
                }
                wb.Sheet("Folders", new[] { "Section", "Folder", "Made by", "Made" }, new double[] { 26, 50, 20, 16 });
                foreach (var f in folders.OrderBy(f => f.ClientId).ThenBy(f => KnowledgeBaseService.PathText(folders, f.Id)))
                    wb.Row(SectionName(f.ClientId), KnowledgeBaseService.PathText(folders, f.Id), f.CreatedBy, When(f.CreatedUtc));
                return Task.CompletedTask;
            });
            return articles.Count;
        }

        private static readonly Regex KbImageSrc = new(@"/portal/kb/image/(\d+)", RegexOptions.Compiled);

        /// <summary>
        /// An article as a page that opens on its own: drawn by <see cref="KbText.Render"/> — the same renderer as the
        /// portal, everything typed encoded — with each picture pointed at its copy in the zip.
        /// </summary>
        private string ArticleHtml(KbArticle a, string section, string dir, Dictionary<long, string> pictures)
        {
            var up = string.Concat(Enumerable.Repeat("../", dir.Count(c => c == '/') + 1));
            var body = KbImageSrc.Replace(KbText.Render(a.Body), m =>
                long.TryParse(m.Groups[1].Value, out var n) && pictures.TryGetValue(n, out var to) ? up + string.Join('/', to.Split('/').Select(Uri.EscapeDataString)) : "#missing-picture");
            Func<string?, string> e = WebUtility.HtmlEncode;
            var meta = $"{e(section)} · version {a.Version} · last changed {e(When(a.UpdatedUtc))} by {e(a.UpdatedBy)}"
                       + (a.FromTicketNumber > 0 ? $" · written up from ticket #{a.FromTicketNumber}" : "");
            return "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
                   + $"<title>{e(a.Title)}</title><style>body{{font:16px/1.5 system-ui,Segoe UI,Arial,sans-serif;max-width:860px;margin:32px auto;padding:0 16px;color:#1d2433}}"
                   + "pre,code{background:#f1f3f7;border-radius:4px}pre{padding:12px;overflow:auto}img{max-width:100%;border:1px solid #d5dae3;border-radius:6px}"
                   + ".meta{color:#5b6475;font-size:14px}.kb-btn{display:inline-block;padding:6px 12px;border:1px solid #8a94a6;border-radius:6px;text-decoration:none}</style></head><body>\n"
                   + $"<h1>{e(a.Title)}</h1>\n<p class=\"meta\">{meta}</p>\n{body}\n</body></html>\n";
        }

        // ---------------------------------------------------------------- what it all is

        private string ReadMe(string summary)
        {
            var b = new StringBuilder();
            b.Append($"{_t.CompanyName} — data export from The Remote Support Program\n");
            b.Append($"Made {When(DateTime.UtcNow)} ({_t.TimeZone}), asked for by {_job.RequestedBy}{(_job.ByUs ? " of The Remote Support Program" : "")}.\n");
            b.Append($"In it: {summary}.\n\n");
            b.Append("Every time in these files is in the company's own time zone, written year-month-day.\n\n");
            b.Append("clients.xlsx\n");
            b.Append("  Sheet \"Clients\": one row per contact person, in exactly the columns Clients → Import clients reads —\n");
            b.Append("  Client, Contact person, Phone, Email, Address, Notes. It can be imported straight back, here or into another program.\n");
            b.Append("  Archived clients and contacts are left off this sheet.\n");
            b.Append("  Sheet \"Every client and contact\": everything, archived ones too, with each contact's role and notes.\n\n");
            b.Append("tickets.xlsx\n");
            b.Append("  One row per ticket: what it is, where it got to, who worked it, the machine, the money. A second sheet lists any\n");
            b.Append("  further machines booked in on the same job. Device passwords are never exported — only whether there was one.\n\n");
            b.Append("ticket-story.xlsx\n");
            b.Append("  Every entry on every ticket, in order: comments, steps, work done, replies from the customer, and automatic lines.\n");
            b.Append("  A cell Excel cannot hold whole is cut, and says so; the whole text is always in that ticket's story.txt.\n\n");
            b.Append("quote-lines.xlsx\n  What each quote asked suppliers for, and its photos.\n\n");
            b.Append("ticket-files.xlsx\n  Every photo, file and session recording on every ticket, and where it is in this export.\n\n");
            b.Append("tickets\\<number>\\\n");
            b.Append("  story.txt — the ticket as a person reads it, start to finish. photos\\ — as it arrived, as it went out, parts and\n");
            b.Append("  pictures of the screen. files\\ — files put on comments. recordings\\ — session recordings, ");
            b.Append(_job.IncludeRecordings ? "included in this export.\n\n" : "NOT included in this export\n  (Export → Include session recordings); they are still listed in ticket-files.xlsx.\n\n");
            b.Append("knowledge-base.xlsx\n  Every article, which section and folder it is in, and who wrote it; a second sheet lists the folders.\n\n");
            b.Append("knowledge-base\\\n");
            b.Append("  General\\ and Clients\\<client>\\, with the folders as they are in the portal. Each article twice: .html to read (it\n");
            b.Append("  opens in any browser, with its pictures) and .txt as it was written, marks and all. pictures\\ holds every picture.\n");
            if (_filesMissing > 0)
                b.Append($"\n{_filesMissing} file(s) listed on a ticket or article were missing on our server and could not be included; the\nspreadsheets say which.\n");
            return b.ToString();
        }
    }
}

/// <summary>
/// Makes the exports asked for (<see cref="CompanyExportService.SweepAsync"/>), one at a time, as soon as one is asked
/// for and otherwise every <c>Platform:ExportSweepSeconds</c> (60), and deletes the ones past their days.
/// </summary>
public sealed class CompanyExportSweep : BackgroundService
{
    private readonly CompanyExportService _exports;
    private readonly ILogger<CompanyExportSweep> _log;
    private readonly TimeSpan _every;

    public CompanyExportSweep(CompanyExportService exports, IConfiguration cfg, ILogger<CompanyExportSweep> log)
    {
        _exports = exports; _log = log;
        _every = TimeSpan.FromSeconds(int.TryParse(cfg["Platform:ExportSweepSeconds"], out var s) && s > 0 ? s : 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await _exports.RecoverAsync(); }
        catch (Exception ex) { _log.LogError(ex, "Exports cut short by a restart could not be set going again"); }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var n = await _exports.SweepAsync(stop);
                if (n > 0) _log.LogInformation("Export sweep: {N} made", n);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogError(ex, "The export sweep failed"); }
            try { await _exports.WaitAsync(_every, stop); } catch (OperationCanceledException) { return; }
        }
    }
}
