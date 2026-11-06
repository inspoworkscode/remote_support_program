using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Hub;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Offers the release to machines, a few at a time (2026-10-16, auto-update stage C). Every <c>Updates:SweepSeconds</c>
/// (30) it walks the connected agents that update themselves, in the rings the rollout has opened, and offers the
/// release to at most <c>Updates:PerMinute</c> (20) machines a minute — five hundred 130 MB downloads at once would
/// flatten the server, whose link is the cell's limit (HANDOVER §126). Whom to offer is
/// <see cref="UpdateService.Wants"/>; the agent adds its own random wait before it downloads.
///
/// <para>One loop rather than an offer at each hello: a ring opened, a pause lifted, a machine coming back — the next
/// sweep catches them all, the same way.</para>
/// </summary>
public sealed class UpdateSweep : BackgroundService
{
    private readonly UpdateService _updates;
    private readonly HubRegistry _hubs;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ILogger<UpdateSweep> _log;
    private readonly int _seconds, _perMinute;

    public UpdateSweep(UpdateService updates, HubRegistry hubs, IDbContextFactory<PlatformDb> dbf, IConfiguration cfg, ILogger<UpdateSweep> log)
    {
        _updates = updates; _hubs = hubs; _dbf = dbf; _log = log;
        _seconds = Math.Clamp(cfg.GetValue("Updates:SweepSeconds", 30), 1, 3600);
        _perMinute = Math.Clamp(cfg.GetValue("Updates:PerMinute", 20), 1, 10000);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await _updates.WarmAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "update: could not read the release"); }
        if (_updates.Version.Length == 0) { _log.LogInformation("update: not a released build — nothing will be offered"); return; }
        _log.LogInformation("update: release {Version}; offering to at most {N} machines a minute", _updates.Version, _perMinute);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(_seconds), ct); } catch (OperationCanceledException) { break; }
            try { await SweepOnceAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "update: sweep failed"); }
        }
    }

    public async Task<int> SweepOnceAsync()
    {
        if (_updates.Client is not { } release) return 0;
        Dictionary<int, int> rings;
        await using (var db = await _dbf.CreateDbContextAsync())
            rings = (await db.Tenants.Select(t => new { t.Id, t.NotBilled, t.Pilot }).ToListAsync())
                .ToDictionary(t => t.Id, t => t.NotBilled ? 0 : t.Pilot ? 1 : 2);
        int budget = Math.Max(1, _perMinute * _seconds / 60), offered = 0;
        var now = DateTime.UtcNow;
        foreach (var hub in _hubs.All.ToList())
        {
            var ring = rings.GetValueOrDefault(hub.TenantId, 2);
            hub.Ring = ring;
            if (!await _updates.OpenForAsync(ring)) continue;
            foreach (var (id, machine, version) in hub.UpdateCandidates())
            {
                if (offered >= budget) return offered;
                if (!_updates.Wants(id, version, now)) continue;
                if (!hub.OfferUpdate(id, release)) continue;
                await _updates.OfferedAsync(hub.TenantId, id, machine);
                offered++;
            }
        }
        return offered;
    }
}
