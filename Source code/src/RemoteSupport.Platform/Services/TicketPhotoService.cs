using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Workshop photographs, and the two gates that make them mean something: three of the machine as
/// it arrived before a job can be booked in, three of it as it leaves before that job can be
/// closed.
///
/// The gates are HERE rather than on a form, because the point of them is the argument that does
/// not happen six weeks later about a scratch on a lid. A rule that can be walked around by
/// posting the form from a stale tab is not evidence of anything.
///
/// Only <see cref="TicketCategory.Workshop"/> is gated. A remote support ticket has no machine on
/// a bench to photograph.
/// </summary>
public sealed class TicketPhotoService
{
    /// <summary>
    /// Three. Enough to show a lid, a base and a screen, and few enough that nobody starts
    /// skipping it. Clint, 2026-09-20.
    /// </summary>
    public const int Required = 3;

    /// <summary>
    /// 12 MB. A phone photo is 2–5 MB; this leaves room for a good camera without letting somebody
    /// put a video on a ticket.
    /// </summary>
    public const long MaxBytes = 12 * 1024 * 1024;

    /// <summary>
    /// What ONE upload may carry, several photos together: 400 MB. There is no limit on how many
    /// photos a job has — three is the least, never the most (Clint, 2026-09-29, fourteen of a
    /// dropped laptop). Every photo picked used to go up as a single request, and the server's
    /// 30 MB default refused fourteen phone photos outright, losing all of them. The portal and the
    /// console now send them one at a time; this is so a console that has not been updated yet
    /// stops losing them the day the server is restarted.
    /// </summary>
    public const long MaxRequestBytes = 400L * 1024 * 1024;

    /// <summary>
    /// What a browser and a phone actually produce. Checked against the BYTES rather than the file
    /// name — an extension is a claim, and this decides what gets written to disk and served back.
    /// </summary>
    private static readonly string[] Allowed = { "image/jpeg", "image/png", "image/webp", "image/heic" };

    /// <summary>Staged photos older than this were an abandoned booking form.</summary>
    private static readonly TimeSpan StageLife = TimeSpan.FromDays(1);

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ILogger<TicketPhotoService> _log;
    private readonly string _root;

    public TicketPhotoService(IDbContextFactory<PlatformDb> dbf, ILogger<TicketPhotoService> log, string dataDir)
    {
        _dbf = dbf; _log = log;
        _root = Path.Combine(dataDir, "ticketphotos");
        Directory.CreateDirectory(_root);
    }

    public string TenantDir(int tenantId)
    {
        var d = Path.Combine(_root, tenantId.ToString());
        Directory.CreateDirectory(d);
        return d;
    }

    public string FilePath(int tenantId, string fileName) => Path.Combine(TenantDir(tenantId), fileName);

    // ---------------------------------------------------------------- what is actually an image

    /// <summary>
    /// Reads the first bytes and says what they really are, or empty for anything else.
    ///
    /// The file name is not evidence. Somebody renaming a file to .jpg would otherwise put whatever
    /// they liked on disk in a folder this server hands back to browsers, which is how an image
    /// upload becomes a way to serve a script from your own domain.
    /// </summary>
    public static string SniffType(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 12 && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        // HEIC and friends: an ISO base media file whose brand says heic/heif/mif1.
        if (b.Length >= 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p')
        {
            var brand = System.Text.Encoding.ASCII.GetString(b, 8, 4);
            if (brand is "heic" or "heix" or "heif" or "mif1" or "msf1") return "image/heic";
        }
        return "";
    }

    /// <summary>
    /// What the bytes are, as <see cref="SniffType"/> says — but only if they hold together as that kind of picture,
    /// else empty (2026-10-23, the pentest's F7: a PNG's eight-byte signature followed by junk was stored as a photo).
    /// The headers are walked, nothing is decoded: JPEG's segments to a frame header with a width, PNG's chunks from
    /// IHDR through IDAT to IEND, WebP's RIFF and first chunk, HEIC's top-level boxes from ftyp to a meta. Decoding
    /// would need a library (ImageSharp is licensed) and is kept off this server on purpose (§62). Asked where a
    /// picture is STORED; SniffType alone is still what labels a stored file when it is served.
    /// </summary>
    public static string WholeImageType(byte[] b) => SniffType(b) switch
    {
        "image/jpeg" when JpegHolds(b) => "image/jpeg",
        "image/png" when PngHolds(b) => "image/png",
        "image/webp" when WebpHolds(b) => "image/webp",
        "image/heic" when HeicHolds(b) => "image/heic",
        _ => ""
    };

    private static uint Be32(byte[] b, long i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
    private static uint Le32(byte[] b, long i) => (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24);
    private static string Tag(byte[] b, long i) => System.Text.Encoding.ASCII.GetString(b, (int)i, 4);

    private static bool JpegHolds(byte[] b)
    {
        long i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF) return false;
            var m = b[i + 1];
            if (m == 0xFF) { i++; continue; }                                    // fill byte
            if (m is 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; } // markers with no length
            if (m is 0xD9 or 0xDA) return false;                                // the end, or a scan, before any frame header
            var len = b[i + 2] << 8 | b[i + 3];
            if (len < 2 || i + 2 + len > b.Length) return false;
            if (m is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))       // a frame header: precision, height, WIDTH
                return len >= 8 && (b[i + 7] << 8 | b[i + 8]) > 0;
            i += 2 + len;
        }
        return false;
    }

    private static bool PngHolds(byte[] b)
    {
        if (b.Length < 8 + 25 + 12 + 12 || b[4] != 0x0D || b[5] != 0x0A || b[6] != 0x1A || b[7] != 0x0A) return false;
        long i = 8;
        var data = false;
        while (i + 12 <= b.Length)
        {
            long len = Be32(b, i);
            if (i + 12 + len > b.Length) return false;
            var type = Tag(b, i + 4);
            if (i == 8) { if (type != "IHDR" || len != 13 || Be32(b, i + 8) == 0 || Be32(b, i + 12) == 0) return false; }
            else if (type == "IDAT") data = true;
            else if (type == "IEND") return data;
            i += 12 + len;
        }
        return false;
    }

    private static bool WebpHolds(byte[] b)
    {
        if (b.Length < 20 || Tag(b, 0) != "RIFF") return false;
        long riff = Le32(b, 4);
        if (riff < 12 || riff + 8 > b.Length) return false;
        return Tag(b, 12) is "VP8 " or "VP8L" or "VP8X" && Le32(b, 16) + 20L <= riff + 8;
    }

    private static bool HeicHolds(byte[] b)
    {
        long i = 0;
        var meta = false;
        while (i + 8 <= b.Length)
        {
            long size = Be32(b, i);
            var head = 8;
            if (size == 1) { if (i + 16 > b.Length) return false; size = (long)Be32(b, i + 8) << 32 | Be32(b, i + 12); head = 16; }
            else if (size == 0) size = b.Length - i;   // runs to the end of the file
            if (size < head || i + size > b.Length) return false;
            var type = Tag(b, i + 4);
            if (i == 0 && type != "ftyp") return false;
            if (type == "meta") meta = true;
            i += size;
        }
        return meta;
    }

    public static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => ".png", "image/webp" => ".webp", "image/heic" => ".heic", _ => ".jpg"
    };

    // ---------------------------------------------------------------- uploading

    /// <summary>
    /// Stores one photo, either against a ticket that exists or staged against a booking form that
    /// does not have one yet.
    /// </summary>
    public async Task<(TicketPhoto? photo, string error)> AddAsync(int tenantId, byte[] bytes, string originalName,
        PhotoKind kind, string uploadedBy, int? ticketId, string stageId)
    {
        if (bytes.Length == 0) return (null, "That file was empty.");
        if (bytes.Length > MaxBytes) return (null, $"That photo is larger than {MaxBytes / (1024 * 1024)} MB.");

        var type = WholeImageType(bytes);
        if (!Allowed.Contains(type))
            return (null, "That is not a photo. JPEG, PNG, WEBP and HEIC are accepted.");

        if (ticketId == null && string.IsNullOrWhiteSpace(stageId))
            return (null, "Nothing to attach that photo to.");

        var name = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..40] + ExtensionFor(type);
        await File.WriteAllBytesAsync(FilePath(tenantId, name), bytes);

        await using var db = await _dbf.CreateDbContextAsync();
        var photo = new TicketPhoto
        {
            TenantId = tenantId, TicketId = ticketId, Kind = kind,
            FileName = name, Bytes = bytes.Length,
            // Kept only so the page can say what they picked; never used as a path.
            OriginalName = Path.GetFileName(originalName ?? "").Trim(),
            UploadedBy = uploadedBy,
            StageId = ticketId == null ? stageId.Trim() : "",
            StagedBy = ticketId == null ? uploadedBy : ""
        };
        db.TicketPhotos.Add(photo);
        await db.SaveChangesAsync();
        return (photo, "");
    }

    // ---------------------------------------------------------------- reading

    public async Task<List<TicketPhoto>> ForTicketAsync(int tenantId, int ticketId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TicketPhotos.Where(p => p.TenantId == tenantId && p.TicketId == ticketId)
            .OrderBy(p => p.Kind).ThenBy(p => p.UploadedUtc).ToListAsync();
    }

    /// <summary>Photos uploaded for a booking form that has not been submitted yet.</summary>
    public async Task<List<TicketPhoto>> StagedAsync(int tenantId, string stageId, string stagedBy)
    {
        if (string.IsNullOrWhiteSpace(stageId)) return new();
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TicketPhotos
            .Where(p => p.TenantId == tenantId && p.TicketId == null && p.StageId == stageId && p.StagedBy == stagedBy)
            .OrderBy(p => p.UploadedUtc).ToListAsync();
    }

    public async Task<TicketPhoto?> GetAsync(int tenantId, long id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TicketPhotos.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId);
    }

    public async Task<int> CountAsync(int tenantId, int ticketId, PhotoKind kind)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TicketPhotos.CountAsync(p => p.TenantId == tenantId && p.TicketId == ticketId && p.Kind == kind);
    }

    // ---------------------------------------------------------------- the two gates

    /// <summary>
    /// Whether a workshop job may be booked in with the photos staged so far. Empty means yes.
    /// </summary>
    /// <param name="routeOff">The Job types the booking follows: another machine on a job follows that job's. Null — a new
    /// booking — asks the company's as they are now, which is what the new ticket will copy.</param>
    public async Task<string> CheckBookInAsync(int tenantId, TicketCategory category, string stageId, string stagedBy, string? routeOff = null)
    {
        if (category != TicketCategory.Workshop) return "";
        if (routeOff == null)
        {
            await using var db = await _dbf.CreateDbContextAsync();
            routeOff = await TicketService.CompanyOffAsync(db, tenantId);
        }
        if (!JobTypes.PhotosInRequired(routeOff)) return "";
        var n = (await StagedAsync(tenantId, stageId, stagedBy)).Count;
        return n >= Required ? ""
            : $"A workshop booking needs {Required} photos of the machine as it arrived. {Describe(n)}";
    }

    /// <summary>
    /// Whether a workshop job may be resolved or closed. Empty means yes.
    ///
    /// Only blocks the move to Resolved or Closed. Every other status change is fine with no
    /// photos at all — the machine is still on the bench, and a technician should not have to
    /// photograph it to say they have started.
    /// </summary>
    public async Task<string> CheckCloseAsync(int tenantId, Ticket ticket, TicketStatus wanted)
    {
        if (ticket.Category != TicketCategory.Workshop) return "";
        if (!JobTypes.PhotosOutRequired(ticket.RouteOff)) return "";
        if (wanted is not (TicketStatus.Resolved or TicketStatus.Closed)) return "";
        if (ticket.Status == wanted) return "";
        var n = await CountAsync(tenantId, ticket.Id, PhotoKind.After);
        return n >= Required ? ""
            : $"A workshop job needs {Required} photos of the finished machine before it can be closed. {Describe(n)}";
    }

    private static string Describe(int n) => n switch
    {
        0 => "None have been added yet.",
        1 => "One has been added.",
        _ => $"{n} have been added."
    };

    // ---------------------------------------------------------------- staging

    /// <summary>
    /// Hands a booking form's staged photos to the ticket it just created.
    ///
    /// Only the ones staged by the same person under the same form id, so two people booking
    /// machines in at the same counter cannot take each other's photographs.
    /// </summary>
    /// <param name="machineId">Which of the ticket's machines they are of, when it is not the ticket's own
    /// (a further machine on the same job, 2026-10-02).</param>
    public async Task<int> AttachStagedAsync(int tenantId, int ticketId, string stageId, string stagedBy, int? machineId = null)
    {
        if (string.IsNullOrWhiteSpace(stageId)) return 0;
        await using var db = await _dbf.CreateDbContextAsync();
        var staged = await db.TicketPhotos
            .Where(p => p.TenantId == tenantId && p.TicketId == null && p.StageId == stageId && p.StagedBy == stagedBy)
            .ToListAsync();
        foreach (var p in staged) { p.TicketId = ticketId; p.StageId = ""; p.StagedBy = ""; p.MachineId = machineId; }
        await db.SaveChangesAsync();
        return staged.Count;
    }

    /// <summary>Removes one photo, file and row together. Staged only, or a ticket's own.</summary>
    public async Task<string> DeleteAsync(int tenantId, long id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var p = await db.TicketPhotos.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (p == null) return "That photo is not on file.";
        var path = FilePath(tenantId, p.FileName);
        db.TicketPhotos.Remove(p);
        await db.SaveChangesAsync();
        // The row goes first. A file left behind is wasted disk; a row pointing at a file that is
        // gone is a broken image on a ticket, which looks like lost evidence.
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _log.LogWarning(ex, "deleting photo file {File}", p.FileName); }
        return "";
    }

    /// <summary>
    /// Clears staged photos from booking forms nobody ever submitted. Without it, every abandoned
    /// booking leaves three photographs on disk for ever.
    /// </summary>
    public async Task<int> SweepStagedAsync()
    {
        var cutoff = DateTime.UtcNow - StageLife;
        await using var db = await _dbf.CreateDbContextAsync();
        var old = await db.TicketPhotos.Where(p => p.TicketId == null && p.UploadedUtc < cutoff).ToListAsync();
        if (old.Count == 0) return 0;
        foreach (var p in old)
        {
            try
            {
                var path = FilePath(p.TenantId, p.FileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) { _log.LogWarning(ex, "sweeping staged photo {File}", p.FileName); }
        }
        db.TicketPhotos.RemoveRange(old);
        await db.SaveChangesAsync();
        _log.LogInformation("swept {Count} abandoned workshop photo(s)", old.Count);
        return old.Count;
    }
}
