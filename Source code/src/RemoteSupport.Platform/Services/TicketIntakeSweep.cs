using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Tickets by email (3.8.4): looks at every company's mailbox that is due, and at our forwarding
/// mailbox, once a minute. A company is due a minute after its last look, or — after a failure —
/// when its back-off says; a company whose password was refused is not looked at at all until it
/// saves its form again (<see cref="TicketIntakeSettings.Paused"/>). The TrialNoticeService pattern:
/// nothing here may take the server down, so every look is wrapped and given its own time limit.
///
/// <c>Testing:IntakeSeconds</c> shortens the minute for the harness; a live server never sets it.
/// </summary>
public sealed class TicketIntakeSweep : BackgroundService
{
    private readonly TicketIntake _intake;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ILogger<TicketIntakeSweep> _log;
    private readonly TimeSpan _every;

    public TicketIntakeSweep(TicketIntake intake, IDbContextFactory<PlatformDb> dbf, IConfiguration cfg, ILogger<TicketIntakeSweep> log)
    {
        _intake = intake; _dbf = dbf; _log = log;
        var s = cfg.GetValue<int>("Testing:IntakeSeconds");
        _every = TimeSpan.FromSeconds(s > 0 ? s : 60);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_every);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var now = DateTime.UtcNow;
                var due = now - _every + TimeSpan.FromSeconds(1);
                List<int> tenants;
                await using (var db = await _dbf.CreateDbContextAsync())
                    tenants = await db.TicketIntake
                        .Where(s => s.EmailEnabled && (s.Way == "mailbox" || (s.Way == "microsoft" && s.MsRefreshTokenEnc != "")) && !s.Paused
                                 && (s.NextTryUtc == null || s.NextTryUtc <= now) && (s.LastCheckUtc == null || s.LastCheckUtc <= due))
                        .Select(s => s.TenantId).ToListAsync(ct);
                // A few at a time: one slow mail server must not hold up everybody else's minute.
                await Parallel.ForEachAsync(tenants, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (id, c) =>
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(c);
                    limit.CancelAfter(TimeSpan.FromSeconds(90));
                    try { await _intake.CheckMailboxAsync(id, limit.Token); }
                    catch (Exception ex) { _log.LogWarning(ex, "mailbox check for tenant {Tenant}", id); }
                });

                var p = await _dbf.CreateDbContextAsync();
                PlatformSettings? ps;
                await using (p) ps = await p.PlatformSettings.FirstOrDefaultAsync(ct);
                if (ps is { ForwardingOffered: true, IntakePaused: false } && (ps.IntakeNextTryUtc == null || ps.IntakeNextTryUtc <= now))
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    limit.CancelAfter(TimeSpan.FromSeconds(90));
                    await _intake.CheckForwardingAsync(limit.Token);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "ticket intake sweep"); }
        }
    }
}
