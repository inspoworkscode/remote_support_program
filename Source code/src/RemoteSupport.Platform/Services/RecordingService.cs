using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Session recordings on the server, attached to the session's ticket (roadmap 1.6). Clint,
/// 2026-09-29: a Record button in the session window, kept on the server with the ticket, and the
/// customer told in their tray app as well as on the ticket.
///
/// <para>The console makes the .mp4 (it records what the technician saw, costing the customer's
/// machine nothing) and uploads it when recording stops. The ticket is found through
/// <see cref="SessionLog.TicketId"/>, not through the hub, because the upload very often arrives
/// after the session has ended and the hub has forgotten it.</para>
/// </summary>
public sealed class RecordingService
{
    /// <summary>
    /// 4 GB: nine hours at the 7.5 MB a minute tools/recordcheck measured for a full-HD screen.
    /// Far past any session, and a stop on a runaway rather than a size anybody plans for.
    /// </summary>
    public const long MaxBytes = 4L * 1024 * 1024 * 1024;

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ILogger<RecordingService> _log;
    private readonly string _root;

    public RecordingService(IDbContextFactory<PlatformDb> dbf, ILogger<RecordingService> log, string dataDir)
    { _dbf = dbf; _log = log; _root = Path.Combine(dataDir, "recordings"); }

    public string FilePath(int tenantId, string fileName) => Path.Combine(_root, tenantId.ToString(), fileName);

    /// <summary>
    /// Stores an upload for <paramref name="sessionId"/>. Only somebody who was IN the session, or
    /// who may see every ticket anyway, may attach a recording to its ticket. The body is streamed
    /// to disk and checked for an MP4's own header before it is kept.
    /// </summary>
    public async Task<(SessionRecording? rec, string error)> AddAsync(int tenantId, string sessionId, string who, TenantRole role,
        Stream body, int seconds, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var log = await db.Sessions.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SessionId == sessionId, ct);
        if (log == null) return (null, "There is no such session.");
        if (log.TicketId is not int ticketId) return (null, "That session has no ticket to attach the recording to.");
        bool wasThere = log.StartedBy == who || Assignees.Has(log.Technicians, who);
        if (!wasThere && !Permissions.CanSeeAllTickets(role)) return (null, "Only somebody who was in that session can add its recording.");

        var dir = Path.Combine(_root, tenantId.ToString());
        Directory.CreateDirectory(dir);
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.mp4";
        var part = Path.Combine(dir, name + ".part");
        long bytes = 0;
        try
        {
            await using (var f = File.Create(part))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await body.ReadAsync(buf, ct)) > 0)
                {
                    bytes += n;
                    if (bytes > MaxBytes) { f.Close(); File.Delete(part); return (null, "That recording is larger than the server accepts."); }
                    await f.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            if (!LooksLikeMp4(part)) { File.Delete(part); return (null, "That is not an MP4 recording."); }
            File.Move(part, Path.Combine(dir, name));
        }
        catch (Exception ex)
        {
            try { File.Delete(part); } catch { }
            _log.LogWarning(ex, "storing a recording for session {Session}", sessionId);
            return (null, "The recording could not be stored: " + ex.Message);
        }

        var rec = new SessionRecording
        {
            TenantId = tenantId, TicketId = ticketId, SessionId = sessionId, MachineName = log.MachineName,
            FileName = name, Bytes = bytes, Seconds = Math.Max(0, seconds), RecordedBy = who
        };
        db.SessionRecordings.Add(rec);
        await db.SaveChangesAsync(ct);
        return (rec, "");
    }

    /// <summary>An MP4 says so in its first box: bytes 4-7 are "ftyp". An extension is only a claim.</summary>
    public static bool LooksLikeMp4(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[8];
            return f.Read(head, 0, 8) == 8 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p';
        }
        catch { return false; }
    }

    public async Task<List<SessionRecording>> ForTicketAsync(int tenantId, int ticketId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.SessionRecordings.Where(r => r.TenantId == tenantId && r.TicketId == ticketId).OrderBy(r => r.RecordedUtc).ToListAsync();
    }

    /// <summary>
    /// A recording, but only for somebody who may see its ticket — the same question the ticket
    /// page itself asks (<see cref="TicketService.VisibleTo"/>), so watching one is never wider
    /// than reading about it.
    /// </summary>
    public async Task<SessionRecording?> GetForViewerAsync(int tenantId, long id, TenantRole role, string me)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var r = await db.SessionRecordings.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (r == null) return null;
        var visible = await TicketService.VisibleTo(db.Tickets.Where(t => t.TenantId == tenantId && t.Id == r.TicketId), role, me).AnyAsync();
        return visible ? r : null;
    }

    public async Task<string> DeleteAsync(int tenantId, long id, TenantRole role)
    {
        if (!Permissions.CanDeleteRecordings(role)) return "Only the owner or a supervisor can delete a recording.";
        await using var db = await _dbf.CreateDbContextAsync();
        var r = await db.SessionRecordings.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (r == null) return "That recording is not on file.";
        db.SessionRecordings.Remove(r);
        await db.SaveChangesAsync();
        // The row first, as with photos: a stray file is wasted disk, a row pointing at nothing is
        // a recording that looks lost.
        try { var p = FilePath(tenantId, r.FileName); if (File.Exists(p)) File.Delete(p); }
        catch (Exception ex) { _log.LogWarning(ex, "deleting recording file {File}", r.FileName); }
        return "";
    }

    public static string Length(int seconds) =>
        seconds >= 3600 ? $"{seconds / 3600} h {seconds % 3600 / 60} min" : seconds >= 60 ? $"{seconds / 60} min {seconds % 60} s" : $"{seconds} s";
}
