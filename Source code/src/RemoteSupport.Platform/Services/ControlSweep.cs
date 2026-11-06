using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Tells the Full admins and Owners when a server is nearing capacity (2026-10-13, stage B) — Clint: <i>"It should
/// show us when we are nearing the recommended capacity, so that we know whether to upgrade the server, or setup a new
/// one"</i>, and on his answer, an email at amber and at red.
///
/// <para>Every <c>Platform:ControlSweepSeconds</c> (300) it reads every server, as the dashboard does. A level is
/// emailed once it has HELD for two sweeps in a row (a restart storm's minute of 97% processor is not a full server),
/// and once only: again only if it rises further, or falls and later rises again. "Not answering" is a level too.
/// Nothing is sent while there is no admin to send it to; the dashboard shows the same either way.</para>
/// </summary>
public sealed class ControlSweep : BackgroundService
{
    private readonly ControlServers _servers;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly PlatformMailService _mail;
    private readonly PlatformAdmins _admins;
    private readonly ILogger<ControlSweep> _log;
    private readonly TimeSpan _every;
    private readonly string _publicUrl;

    public ControlSweep(ControlServers servers, IDbContextFactory<PlatformDb> dbf, PlatformMailService mail, PlatformAdmins admins,
        IConfiguration cfg, ILogger<ControlSweep> log)
    {
        _servers = servers; _dbf = dbf; _mail = mail; _admins = admins; _log = log;
        _every = TimeSpan.FromSeconds(Math.Clamp(cfg.GetValue("Platform:ControlSweepSeconds", 300.0), 1, 86400));
        _publicUrl = (cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // The first reading waits a sweep: the processor average needs its samples, and a server just started is busy.
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(_every, stop); } catch (OperationCanceledException) { return; }
            try { await SweepAsync(); } catch (Exception ex) { _log.LogWarning(ex, "control sweep failed"); }
        }
    }

    public async Task SweepAsync()
    {
        if (!await _admins.AnyAsync()) return;
        var views = await _servers.FleetAsync();
        await using var db = await _dbf.CreateDbContextAsync();
        foreach (var v in views)
        {
            var row = await db.ControlServers.FindAsync(v.Row.Id);
            if (row == null) continue;
            var level = (int)v.Level;
            var held = level == row.LastLevel;
            row.LastLevel = level;
            if (held && level > row.AlertedLevel && level >= (int)ServerCapacity.Level.Amber)
            {
                row.AlertedLevel = level;
                await TellAsync(v);
            }
            else if (held && level < row.AlertedLevel)
            {
                row.AlertedLevel = level;   // it came down: a later rise is news again
                await _admins.LogAsync("the dashboard", $"Server {v.Row.Name} is back to {ServerCapacity.Label(v.Level)}");
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task TellAsync(ControlServers.View v)
    {
        var name = v.Row.Name;
        var (subject, body) = v.Assessment == null
            ? ($"{name} is not answering the dashboard", $"{name} ({v.Row.Address}) has not answered the dashboard for two readings in a row.\n\nWhy: {v.Error}\n")
            : Compose(name, v.Assessment, v.Report!);
        body += $"\nThe dashboard: {_publicUrl}/owner\n";
        var to = (await _admins.AllAsync()).Where(a => a.Level >= AdminLevel.Full).Select(a => a.Email).ToList();
        var r = to.Count == 0 ? "no Full admin or Owner to tell" : await _mail.SendAsync(to, subject, body);
        await _admins.LogAsync("the dashboard", $"Server {name}: {ServerCapacity.Label(v.Level)} — {(r == "sent" ? "emailed " + string.Join(", ", to) : "NOT emailed: " + r)}");
    }

    private static (string, string) Compose(string name, ServerCapacity.Assessment a, ServerReport r)
    {
        var worst = a.Measures.First(m => m.Name == a.Worst);
        var b = new StringBuilder();
        b.Append($"{name} is {ServerCapacity.Label(a.Level).ToLowerInvariant()}: {worst.Name.ToLowerInvariant()} at {worst.Percent:0}%.\n\n");
        b.Append(a.Advice + "\n\n");
        foreach (var m in a.Measures) b.Append($"  {m.Name,-12} {m.Percent,5:0}%   {m.Detail}\n");
        b.Append($"\n{r.Companies.Count(c => c.ServiceOn)} companies using it, {r.Companies.Sum(c => c.Machines):N0} machines, {r.Companies.Sum(c => c.ConsoleUsers)} console users.\n");
        return ($"{name} is {ServerCapacity.Label(a.Level).ToLowerInvariant()} ({worst.Name.ToLowerInvariant()} {worst.Percent:0}%)", b.ToString());
    }
}
