using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Files and photos on a ticket's comments (Clint, 2026-10-13: "an option to upload files or photos on all comments.
/// Sometimes the technician wants to upload photos during stages to show progress they are making. This should also be
/// able to be emailed") — and the damage report on Not repairable — Damage report needed, which is the same thing on a
/// step instead of a comment.
///
/// <para>How it goes, the same in the portal and the console: each file goes up on its own the moment it is picked
/// (a photo made smaller first, as workshop photos are, §62 — fourteen phone photos in one request were lost once),
/// and is held LOOSE on the ticket by whoever picked it. Posting the comment names the ids it carries and
/// <see cref="ClaimAsync"/> hands them to it — only loose ones, on that ticket, picked by that person, so nobody can
/// take a colleague's half-written comment's files, or another ticket's. Loose files nobody posted are cleared after a
/// day, the next time that person picks one.</para>
///
/// <para>What may go up is an ALLOW-list of the kinds of file a workshop actually has — pictures, PDFs, office
/// documents, text, a zip — never a program or a script. Pictures and PDFs are told by their BYTES, and only those
/// two are ever shown in the browser; everything else downloads, as a file, never as a page on our domain.</para>
/// </summary>
public sealed class TicketAttachmentService
{
    /// <summary>20 MB a file: a damage report, a phone video of the fault, a page of photos — not a disk image.</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>
    /// What one email may carry, all its files together. Most mail servers refuse a message past 25 MB, and attaching
    /// grows a file by a third — so a comment whose files are larger than this is refused BEFORE it is posted, with the
    /// words kept, rather than posted and then bounced.
    /// </summary>
    public const long EmailMaxBytes = 15L * 1024 * 1024;

    /// <summary>The kinds of file that may go up, by extension — what the person sees as the kind of file.</summary>
    public static readonly string[] Extensions =
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".gif",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".rtf",
        ".txt", ".csv", ".log", ".eml", ".msg", ".zip", ".mp4", ".mov"
    };

    /// <summary>Loose files older than this were for a comment nobody posted.</summary>
    private static readonly TimeSpan LooseLife = TimeSpan.FromDays(1);

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ILogger<TicketAttachmentService> _log;
    private readonly string _root;

    public TicketAttachmentService(IDbContextFactory<PlatformDb> dbf, ILogger<TicketAttachmentService> log, string dataDir)
    {
        _dbf = dbf; _log = log;
        _root = Path.Combine(dataDir, "ticketfiles");
        Directory.CreateDirectory(_root);
    }

    public string FilePath(int tenantId, string fileName)
    {
        var d = Path.Combine(_root, tenantId.ToString());
        Directory.CreateDirectory(d);
        return Path.Combine(d, fileName);
    }

    /// <summary>"photo.jpg (1.2 MB)" — how a file is named under its comment.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB"
    };

    /// <summary>What the bytes really are: a picture, a PDF, or nothing this server will show in a browser.</summary>
    public static string Sniff(byte[] b)
    {
        var image = TicketPhotoService.SniffType(b);
        if (image.Length > 0) return image;
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "image/gif";
        if (b.Length >= 5 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F' && b[4] == '-') return "application/pdf";
        return "application/octet-stream";
    }

    /// <summary>Why this file may not go up, or empty.</summary>
    public static string Refusal(string name, long bytes)
    {
        if (bytes <= 0) return $"{name} is empty.";
        if (bytes > MaxBytes) return $"{name} is {Size(bytes)} — a file may be {MaxBytes / (1024 * 1024)} MB at most.";
        var ext = Path.GetExtension(name ?? "").ToLowerInvariant();
        if (!Extensions.Contains(ext))
            return $"{name} is not a kind of file that can go on a ticket. Photos, PDFs, Word, Excel and PowerPoint files, text, a zip or a short video can.";
        return "";
    }

    /// <summary>
    /// One file, loose on the ticket until a comment takes it. The caller has checked the person may see the ticket.
    /// </summary>
    public async Task<(TicketAttachment? file, string error)> AddAsync(int tenantId, int ticketId, byte[] bytes, string originalName, string by)
    {
        var name = Path.GetFileName(originalName ?? "").Trim();
        if (name.Length == 0) name = "file";
        if (name.Length > 150) name = name[..120] + Path.GetExtension(name);
        if (Refusal(name, bytes.Length) is { Length: > 0 } no) return (null, no);

        await using var db = await _dbf.CreateDbContextAsync();
        if (!await db.Tickets.AnyAsync(t => t.Id == ticketId && t.TenantId == tenantId)) return (null, "That ticket is not on file.");
        await SweepLooseAsync(db, tenantId, by);

        var type = Sniff(bytes);
        // The extension it is stored under is ours: a picture or PDF by what its bytes are, anything else ".bin".
        var ext = type switch { "application/pdf" => ".pdf", "image/gif" => ".gif", "application/octet-stream" => ".bin", _ => TicketPhotoService.ExtensionFor(type) };
        var stored = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..40] + ext;
        await File.WriteAllBytesAsync(FilePath(tenantId, stored), bytes);
        var row = new TicketAttachment
        {
            TenantId = tenantId, TicketId = ticketId, FileName = stored, OriginalName = name,
            ContentType = type, Bytes = bytes.Length, UploadedBy = by
        };
        db.TicketAttachments.Add(row);
        await db.SaveChangesAsync();
        return (row, "");
    }

    /// <summary>
    /// The loose files a comment carries, given to it. Only this ticket's, still loose, and picked by this person —
    /// anything else named is ignored rather than stolen. Returns how many it took.
    /// </summary>
    public static async Task<int> ClaimAsync(PlatformDb db, int tenantId, int ticketId, long noteId, IEnumerable<long>? ids, string by)
    {
        var wanted = (ids ?? Enumerable.Empty<long>()).Distinct().ToList();
        if (wanted.Count == 0) return 0;
        var rows = await db.TicketAttachments.Where(a => a.TenantId == tenantId && a.TicketId == ticketId && a.NoteId == null
                                                         && a.UploadedBy == by && wanted.Contains(a.Id)).ToListAsync();
        foreach (var a in rows) a.NoteId = noteId;
        return rows.Count;
    }

    /// <summary>The loose files this person has picked on this ticket that a comment may carry — what is named and still theirs.</summary>
    public async Task<List<TicketAttachment>> LooseAsync(int tenantId, int ticketId, IEnumerable<long>? ids, string by)
    {
        var wanted = (ids ?? Enumerable.Empty<long>()).Distinct().ToList();
        if (wanted.Count == 0) return new();
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TicketAttachments.Where(a => a.TenantId == tenantId && a.TicketId == ticketId && a.NoteId == null
                                                     && a.UploadedBy == by && wanted.Contains(a.Id)).OrderBy(a => a.Id).ToListAsync();
    }

    /// <summary>Why files this size together cannot be emailed, or empty.</summary>
    public static string TooLargeToEmail(IEnumerable<TicketAttachment> files)
    {
        long total = files.Sum(f => f.Bytes);
        return total <= EmailMaxBytes ? ""
            : $"The files come to {Size(total)} together, and an email can carry {EmailMaxBytes / (1024 * 1024)} MB at most. Email fewer, or smaller ones.";
    }

    /// <summary>Every file on a ticket that belongs to a comment, by the comment.</summary>
    public async Task<Dictionary<long, List<TicketAttachment>>> ForTicketAsync(int tenantId, int ticketId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var rows = await db.TicketAttachments.Where(a => a.TenantId == tenantId && a.TicketId == ticketId && a.NoteId != null)
            .OrderBy(a => a.Id).ToListAsync();
        return rows.GroupBy(a => a.NoteId!.Value).ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>One comment's files, for its email.</summary>
    public static Task<List<TicketAttachment>> OfNoteAsync(PlatformDb db, int tenantId, long noteId) =>
        db.TicketAttachments.Where(a => a.TenantId == tenantId && a.NoteId == noteId).OrderBy(a => a.Id).ToListAsync();

    /// <summary>A file, for somebody who may see its ticket — the ticket page's own question, so a file is never wider than its story.</summary>
    public async Task<TicketAttachment?> GetForViewerAsync(int tenantId, long id, TenantRole role, string me)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var a = await db.TicketAttachments.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (a == null) return null;
        var visible = await TicketService.VisibleTo(db.Tickets.Where(t => t.TenantId == tenantId && t.Id == a.TicketId), role, me).AnyAsync();
        return visible ? a : null;
    }

    /// <summary>Takes a loose file back off a comment not yet posted — the × beside it. Only the person who picked it.</summary>
    public async Task<string> RemoveLooseAsync(int tenantId, long id, string by)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var a = await db.TicketAttachments.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.NoteId == null && x.UploadedBy == by);
        if (a == null) return "That file is not waiting to be posted.";
        db.TicketAttachments.Remove(a);
        await db.SaveChangesAsync();
        TryDelete(a);
        return "";
    }

    /// <summary>This person's loose files from more than a day ago: a comment they never posted.</summary>
    private async Task SweepLooseAsync(PlatformDb db, int tenantId, string by)
    {
        var cutoff = DateTime.UtcNow - LooseLife;
        var old = await db.TicketAttachments.Where(a => a.TenantId == tenantId && a.NoteId == null && a.UploadedBy == by && a.UploadedUtc < cutoff).ToListAsync();
        if (old.Count == 0) return;
        db.TicketAttachments.RemoveRange(old);
        await db.SaveChangesAsync();
        foreach (var a in old) TryDelete(a);
    }

    private void TryDelete(TicketAttachment a)
    {
        try { var p = FilePath(a.TenantId, a.FileName); if (File.Exists(p)) File.Delete(p); }
        catch (Exception ex) { _log.LogWarning(ex, "deleting ticket file {File}", a.FileName); }
    }
}
