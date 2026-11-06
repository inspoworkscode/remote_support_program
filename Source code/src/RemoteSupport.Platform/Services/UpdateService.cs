using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Every rule of auto-update on the server (2026-10-16, stage C, docs/NEXT-SESSION-AUTOUPDATE.md). Clint's two decisions:
/// <b>silent on customer machines, and the rollout is ours alone</b> — a company cannot hold its machines back.
///
/// <list type="bullet">
/// <item><b>The release is what this server hands out</b>: the packed customer app (the tray app with the host, the agent
///   and the scripts inside it — one signed file) and the console. build.ps1 stamps the platform and those apps with one
///   version, so the release's version is this server's own (<see cref="AppVersion.Current"/>). An UNSTAMPED server — a
///   development build — offers nothing, ever.</item>
/// <item><b>Rings</b>: 0 our own company (<see cref="Tenant.NotBilled"/>), 1 the pilots, 2 everybody (the free version
///   included). A release starts at ring 0; a Full admin or an Owner moves it on (<see cref="SetRingAsync"/>), and pauses
///   it (<see cref="PauseAsync"/>).</item>
/// <item><b>Automatic pause</b>: when machines that were offered it roll back — 2% of them, and at least one — the release
///   stops at once and every Full admin and Owner is emailed. A bad update must never spread.</item>
/// <item><b>A machine is never offered a version again</b> once it rolled back or refused it (<see cref="UpdateSteps.GivesUp"/>).</item>
/// <item><b>Offers are spread out</b> (<see cref="UpdateSweep"/>): a few machines a minute, so five hundred 130 MB downloads
///   never land on the server at once.</item>
/// </list>
///
/// The server's word is never what makes a file safe: every machine checks every file's signature itself (<see cref="UpdateTrust"/>).
/// </summary>
public sealed class UpdateService
{
    public const string ClientPath = "/update/client.exe";
    public const string ConsolePath = "/update/console.exe";
    /// <summary>The share of machines offered a release that may roll back before it pauses itself.</summary>
    public const double PauseAtShare = 0.02;
    /// <summary>A machine still on an old version is offered again after this long (it may have been off, or busy).</summary>
    public static readonly TimeSpan OfferAgainAfter = TimeSpan.FromHours(6);

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly PayloadService _payloads;
    private readonly PlatformAdmins _admins;
    private readonly PlatformMailService _mail;
    private readonly ILogger<UpdateService> _log;
    private readonly string _publicUrl;
    private readonly string _version;

    private readonly SemaphoreSlim _one = new(1, 1);
    private UpdateRollout? _rollout;
    private bool _loaded;
    /// <summary>Per machine: the version last offered and when. In memory: a restart only means an earlier offer again.</summary>
    private readonly ConcurrentDictionary<string, (string version, DateTime at)> _offered = new();
    /// <summary>Machines that rolled back or refused THIS version — never offered it again. Loaded from the history at start.</summary>
    private readonly ConcurrentDictionary<string, bool> _gaveUp = new();
    private readonly ConcurrentDictionary<string, (DateTime mtime, long size, string sha)> _hashes = new();

    public UpdateService(IDbContextFactory<PlatformDb> dbf, PayloadService payloads, PlatformAdmins admins, PlatformMailService mail,
        IConfiguration cfg, ILogger<UpdateService> log)
    {
        _dbf = dbf; _payloads = payloads; _admins = admins; _mail = mail; _log = log;
        _publicUrl = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
        _version = AppVersion.Current;
    }

    /// <summary>The version this server would hand out, or "" when it is not a released (stamped) build.</summary>
    public string Version => AppVersion.IsStamped(_version) ? _version : "";

    /// <summary>The ring a company is in: 0 our own (not billed), 1 a pilot, 2 everybody else.</summary>
    public static int RingOf(Tenant t) => t.NotBilled ? 0 : t.Pilot ? 1 : 2;
    public static string RingName(int ring) => ring switch { 0 => "Our own company", 1 => "The pilots", _ => "Everybody" };

    // ------------------------------------------------------------------ the files

    /// <summary>The customer app's release, or null when there is nothing to hand out.</summary>
    public UpdateRelease? Client => Version.Length > 0 && _payloads.ClientExeAvailable ? Describe(_payloads.ClientExePath, ClientPath) : null;
    /// <summary>The console's release, or null.</summary>
    public UpdateRelease? Console => Version.Length > 0 && _payloads.TechnicianExeAvailable ? Describe(_payloads.TechnicianExePath, ConsolePath) : null;

    private UpdateRelease? Describe(string file, string path)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists) return null;
            // Hashed once per file as it is on disk: 130 MB takes a moment, and a deploy replaces the file anyway.
            var h = _hashes.AddOrUpdate(file, _ => Hash(fi), (_, old) => old.mtime == fi.LastWriteTimeUtc && old.size == fi.Length ? old : Hash(fi));
            return new UpdateRelease { Version = Version, Path = path, Size = h.size, Sha256 = h.sha };
        }
        catch (Exception ex) { _log.LogWarning(ex, "update: could not describe {File}", file); return null; }
    }

    private static (DateTime, long, string) Hash(FileInfo fi)
    {
        using var s = fi.OpenRead();
        return (fi.LastWriteTimeUtc, fi.Length, Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant());
    }

    // ------------------------------------------------------------------ the rollout

    /// <summary>This release's rollout, made at ring 0 the first time it is asked for. Null for an unstamped server.</summary>
    public async Task<UpdateRollout?> RolloutAsync()
    {
        if (Version.Length == 0) return null;
        if (_loaded) return _rollout;
        await _one.WaitAsync();
        try
        {
            if (_loaded) return _rollout;
            await using var db = await _dbf.CreateDbContextAsync();
            var r = await db.UpdateRollouts.FirstOrDefaultAsync(x => x.Version == Version);
            if (r == null)
            {
                r = new UpdateRollout { Version = Version, Ring = 0, ChangedBy = "this server, on starting a new release" };
                db.UpdateRollouts.Add(r);
                try { await db.SaveChangesAsync(); }
                catch (DbUpdateException) { db.ChangeTracker.Clear(); r = await db.UpdateRollouts.FirstAsync(x => x.Version == Version); }
            }
            foreach (var id in await db.DeviceUpdateEvents.Where(e => e.Version == Version && (e.Step == UpdateSteps.RolledBack || e.Step == UpdateSteps.Refused))
                         .Select(e => e.DeviceId).Distinct().ToListAsync())
                _gaveUp[id] = true;
            _rollout = r; _loaded = true;
            return r;
        }
        finally { _one.Release(); }
    }

    /// <summary>Whether a company in <paramref name="ring"/> may be offered the release now.</summary>
    public async Task<bool> OpenForAsync(int ring) => await RolloutAsync() is { Paused: false } r && ring <= r.Ring;

    /// <summary>The console release a company may take now, or null — for the console's check at start.</summary>
    public async Task<UpdateRelease?> ConsoleOfferAsync(int ring) => await OpenForAsync(ring) ? Console : null;

    /// <summary>
    /// The same, without waiting — for a console's state, which a hub builds under its lock. Null until <see cref="WarmAsync"/>
    /// has read the rollout and hashed the files, so a console signing in never waits on hashing 50 MB.
    /// </summary>
    public UpdateRelease? ConsoleOfferNow(int ring) => _warm && _rollout is { Paused: false } r && ring <= r.Ring ? Console : null;
    private volatile bool _warm;

    /// <summary>Reads the rollout and hashes both files once, at start (<see cref="UpdateSweep"/>).</summary>
    public async Task WarmAsync()
    {
        if (await RolloutAsync() == null) return;
        await Task.Run(() => { _ = Client; _ = Console; });
        _warm = true;
    }

    /// <summary>
    /// Whether this machine should be offered the release now: its agent older than it, not given up on it, not offered it
    /// in the last <see cref="OfferAgainAfter"/>. The caller has already asked <see cref="OpenForAsync"/> for its ring.
    /// </summary>
    public bool Wants(string deviceId, string agentVersion, DateTime now)
    {
        if (Version.Length == 0 || AppVersion.Compare(agentVersion, Version) >= 0 || _gaveUp.ContainsKey(deviceId)) return false;
        return !_offered.TryGetValue(deviceId, out var o) || o.version != Version || now - o.at >= OfferAgainAfter;
    }

    /// <summary>Recorded as the machine's first step: it was offered the release.</summary>
    public async Task OfferedAsync(int tenantId, string deviceId, string machine)
    {
        _offered[deviceId] = (Version, DateTime.UtcNow);
        await AddAsync(tenantId, deviceId, machine, Version, UpdateSteps.Offered, "");
    }

    /// <summary>What a machine's agent reported. A rollback can pause the release (<see cref="AutoPauseAsync"/>).</summary>
    public async Task ReportAsync(int tenantId, string deviceId, string machine, UpdateReportMsg r)
    {
        var step = (r.Step ?? "").Trim();
        if (step.Length == 0 || step.Length > 32) return;
        var version = (r.Version ?? "").Trim();
        if (version.Length > 40) version = version[..40];
        var detail = (r.Detail ?? "").Trim();
        if (detail.Length > 500) detail = detail[..500];
        await AddAsync(tenantId, deviceId, machine, version, step, detail);
        if (UpdateSteps.GivesUp(step) && version == Version) _gaveUp[deviceId] = true;
        if (step == UpdateSteps.RolledBack && version == Version) await AutoPauseAsync();
        _log.LogInformation("update: {Machine} {Step} {Version}{Detail}", machine, step, version, detail.Length > 0 ? " — " + detail : "");
    }

    private async Task AddAsync(int tenantId, string deviceId, string machine, string version, string step, string detail)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        db.DeviceUpdateEvents.Add(new DeviceUpdateEvent { TenantId = tenantId, DeviceId = deviceId, MachineName = machine, Version = version, Step = step, Detail = detail });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Too many machines rolled back: the release stops being offered at once, and every Full admin and Owner is told.
    /// "Too many" is <see cref="PauseAtShare"/> of the machines offered it, and at least one — with few machines, the first.
    /// </summary>
    private async Task AutoPauseAsync()
    {
        int offered, back;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            offered = await db.DeviceUpdateEvents.Where(e => e.Version == Version && e.Step == UpdateSteps.Offered).Select(e => e.DeviceId).Distinct().CountAsync();
            back = await db.DeviceUpdateEvents.Where(e => e.Version == Version && e.Step == UpdateSteps.RolledBack).Select(e => e.DeviceId).Distinct().CountAsync();
        }
        if (back == 0 || back < PauseAtShare * Math.Max(1, offered)) return;
        var why = $"automatically: {back} of {offered} machine{(offered == 1 ? "" : "s")} offered it rolled back";
        if (!await SetAsync(r => { if (r.Paused) return false; r.Paused = true; r.PausedWhy = why; return true; }, "the update watch")) return;
        await _admins.LogAsync("the update watch", $"Release {Version} PAUSED {why}");
        var to = (await _admins.AllAsync()).Where(a => a.Level >= AdminLevel.Full).Select(a => a.Email).ToList();
        var said = to.Count == 0 ? "no Full admin or Owner to tell" : await _mail.SendAsync(to, $"Update {Version} paused: machines rolled back",
            $"Release {Version} has stopped being offered, {why}.\n\nA machine rolls back when its new agent does not connect within five minutes of "
            + "installing; it puts its old files back and keeps working. Look at what they reported before resuming:\n\n"
            + $"{_publicUrl}/owner/updates\n");
        _log.LogWarning("update: release {Version} paused {Why}; {Said}", Version, why, said);
    }

    /// <summary>A Full admin or an Owner moves the release on to <paramref name="ring"/> (never back: pause for that).</summary>
    public async Task<string> SetRingAsync(PlatformAdmins.Actor by, int ring)
    {
        if (!PlatformAdmins.MayRun(by.Level)) return "Only a Full admin or an Owner moves an update on.";
        if (ring is < 0 or > 2) return "There is no such ring.";
        if (await RolloutAsync() is not { } r) return "This server has no released version to hand out (it is not a stamped build).";
        if (ring <= r.Ring) return $"It is already offered to {RingName(r.Ring).ToLowerInvariant()}.";
        await SetAsync(x => { x.Ring = ring; return true; }, by.Name);
        await _admins.LogAsync(by.Name, $"Release {Version} → {RingName(ring).ToLowerInvariant()}");
        return "";
    }

    public async Task<string> PauseAsync(PlatformAdmins.Actor by)
    {
        if (!PlatformAdmins.MayRun(by.Level)) return "Only a Full admin or an Owner pauses an update.";
        if (await RolloutAsync() is null) return "This server has no released version to hand out.";
        if (!await SetAsync(x => { if (x.Paused) return false; x.Paused = true; x.PausedWhy = "by " + by.Name; return true; }, by.Name)) return "It is already paused.";
        await _admins.LogAsync(by.Name, $"Release {Version} paused");
        return "";
    }

    public async Task<string> ResumeAsync(PlatformAdmins.Actor by)
    {
        if (!PlatformAdmins.MayRun(by.Level)) return "Only a Full admin or an Owner resumes an update.";
        if (await RolloutAsync() is null) return "This server has no released version to hand out.";
        if (!await SetAsync(x => { if (!x.Paused) return false; x.Paused = false; x.PausedWhy = ""; return true; }, by.Name)) return "It is not paused.";
        await _admins.LogAsync(by.Name, $"Release {Version} resumed");
        return "";
    }

    /// <summary>Changes the rollout row, in the database and in memory together. False when <paramref name="change"/> said there was nothing to do.</summary>
    private async Task<bool> SetAsync(Func<UpdateRollout, bool> change, string by)
    {
        await RolloutAsync();
        await _one.WaitAsync();
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var r = await db.UpdateRollouts.FirstOrDefaultAsync(x => x.Version == Version);
            if (r == null || !change(r)) return false;
            r.ChangedUtc = DateTime.UtcNow; r.ChangedBy = by;
            await db.SaveChangesAsync();
            _rollout = r;
            return true;
        }
        finally { _one.Release(); }
    }

    // ------------------------------------------------------------------ what the page shows

    public sealed record RingCount(int Ring, int Machines, int OnIt, int Offered, int InProgress, int RolledBack, int Refused, int NeedReinstall);
    public sealed record Summary(string Version, int Ring, bool Paused, string PausedWhy, DateTime ChangedUtc, string ChangedBy,
        List<RingCount> Rings, UpdateRelease? Client, UpdateRelease? Console);

    /// <summary>The release and, per ring, how its machines stand. Null on an unstamped server.</summary>
    public async Task<Summary?> SummaryAsync()
    {
        if (await RolloutAsync() is not { } r) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        var rings = (await db.Tenants.Select(t => new { t.Id, t.NotBilled, t.Pilot }).ToListAsync())
            .ToDictionary(t => t.Id, t => t.NotBilled ? 0 : t.Pilot ? 1 : 2);
        var devices = await db.Devices.Select(d => new { d.TenantId, d.DeviceId, d.AgentVersion, d.SelfUpdates }).ToListAsync();
        var last = (await db.DeviceUpdateEvents.Where(e => e.Version == Version).Select(e => new { e.DeviceId, e.Step, e.Id }).ToListAsync())
            .GroupBy(e => e.DeviceId).ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Id).First().Step);
        var counts = Enumerable.Range(0, 3).Select(ring =>
        {
            var mine = devices.Where(d => rings.GetValueOrDefault(d.TenantId, 2) == ring).ToList();
            var steps = mine.Select(d => last.GetValueOrDefault(d.DeviceId)).ToList();
            return new RingCount(ring, mine.Count,
                mine.Count(d => AppVersion.Compare(d.AgentVersion, Version) >= 0),
                steps.Count(s => s != null),
                steps.Count(s => s is UpdateSteps.Offered or UpdateSteps.Downloading or UpdateSteps.Ready or UpdateSteps.Installing or UpdateSteps.Failed),
                steps.Count(s => s == UpdateSteps.RolledBack),
                steps.Count(s => s == UpdateSteps.Refused),
                mine.Count(d => !d.SelfUpdates));
        }).ToList();
        return new Summary(r.Version, r.Ring, r.Paused, r.PausedWhy, r.ChangedUtc, r.ChangedBy, counts, Client, Console);
    }

    /// <summary>The newest steps machines reported, for the page: company and machine named.</summary>
    public async Task<List<(DeviceUpdateEvent e, string company)>> RecentAsync(int take = 60)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var rows = await db.DeviceUpdateEvents.OrderByDescending(e => e.Id).Take(take).ToListAsync();
        var names = await db.Tenants.Select(t => new { t.Id, t.CompanyName }).ToDictionaryAsync(t => t.Id, t => t.CompanyName);
        return rows.Select(e => (e, names.GetValueOrDefault(e.TenantId, "?"))).ToList();
    }
}
