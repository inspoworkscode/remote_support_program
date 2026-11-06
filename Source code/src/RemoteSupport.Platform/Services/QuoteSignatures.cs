using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The rules of a quote request signature (Clint, 2026-10-06), in one place: what a line may look
/// like, what an image may be, and how both are written into an email. Text, an image, or both; the
/// text as lines, each with its own font, size, colour and weight.
///
/// <para>Nothing typed ever reaches the email as markup. The words are encoded; the font is a name
/// from <see cref="Fonts"/>, the size a number from <see cref="Sizes"/>, the colour six hex digits —
/// anything else is put back to the default, so a signature cannot carry a style of its own making
/// into a supplier's inbox.</para>
/// </summary>
public static class QuoteSignatures
{
    /// <summary>One line of a signature's text.</summary>
    public sealed record Line(string Text, string Font, int Size, string Color, string Style);

    /// <summary>Fonts every mail program draws, by the name shown, with what it falls back to.</summary>
    public static readonly IReadOnlyList<(string name, string stack)> Fonts = new[]
    {
        ("Arial", "Arial, Helvetica, sans-serif"),
        ("Calibri", "Calibri, Carlito, Arial, sans-serif"),
        ("Segoe UI", "'Segoe UI', Arial, sans-serif"),
        ("Verdana", "Verdana, Geneva, sans-serif"),
        ("Tahoma", "Tahoma, Verdana, sans-serif"),
        ("Trebuchet MS", "'Trebuchet MS', Arial, sans-serif"),
        ("Georgia", "Georgia, 'Times New Roman', serif"),
        ("Times New Roman", "'Times New Roman', Times, serif"),
        ("Garamond", "Garamond, Georgia, serif"),
        ("Courier New", "'Courier New', Courier, monospace"),
        ("Brush Script MT", "'Brush Script MT', 'Segoe Script', cursive"),
    };

    public static readonly int[] Sizes = { 10, 11, 12, 13, 14, 16, 18, 20, 22, 24, 28, 32 };

    /// <summary>Weight and slant, by value, with what the editor calls them.</summary>
    public static readonly IReadOnlyList<(string value, string label)> Styles = new[]
    {
        ("normal", "Normal"), ("bold", "Bold"), ("italic", "Italic"), ("bold-italic", "Bold italic")
    };

    public const string DefaultFont = "Arial";
    public const int DefaultSize = 14;
    public const string DefaultColor = "#222222";
    public const int MaxLines = 12, MaxLineLength = 200, MaxNameLength = 80;
    public const int MaxImageBytes = 1024 * 1024;
    public const int MinImageWidth = 40, MaxImageWidth = 800, DefaultImageWidth = 400;
    /// <summary>How the email refers to the image it carries.</summary>
    public const string ContentId = "signature@rsp";

    private static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>A line made safe: anything not on the lists put back to the default.</summary>
    public static Line Clean(string? text, string? font, int size, string? color, string? style)
    {
        var t = new string((text ?? "").Where(c => c is not ('\r' or '\n')).ToArray()).TrimEnd();
        if (t.Length > MaxLineLength) t = t[..MaxLineLength];
        var f = Fonts.Any(x => x.name == font) ? font! : DefaultFont;
        var s = Sizes.Contains(size) ? size : Sizes.OrderBy(x => Math.Abs(x - size)).First();
        if (size <= 0) s = DefaultSize;
        var c2 = color != null && Hex.IsMatch(color.Trim()) ? color.Trim().ToLowerInvariant() : DefaultColor;
        var st = Styles.Any(x => x.value == style) ? style! : "normal";
        return new Line(t, f, s, c2, st);
    }

    /// <summary>The lines as typed, cleaned, with blank lines at the end dropped (those in the middle are spacing).</summary>
    public static List<Line> CleanAll(IEnumerable<Line> lines)
    {
        var list = lines.Select(l => Clean(l.Text, l.Font, l.Size, l.Color, l.Style)).Take(MaxLines).ToList();
        while (list.Count > 0 && list[^1].Text.Trim().Length == 0) list.RemoveAt(list.Count - 1);
        return list;
    }

    public static List<Line> LinesOf(QuoteSignature s)
    {
        try { return CleanAll(JsonSerializer.Deserialize<List<Line>>(s.LinesJson) ?? new()); }
        catch (JsonException) { return new(); }
    }

    public static string Serialize(IEnumerable<Line> lines) => JsonSerializer.Serialize(CleanAll(lines));

    public static bool HasText(QuoteSignature s) => LinesOf(s).Any(l => l.Text.Trim().Length > 0);
    public static bool HasImage(QuoteSignature s) => s.Image is { Length: > 0 } && s.ImageToken.Length > 0;

    // ---------------------------------------------------------------- the image

    /// <summary>What mail programs show inline: PNG, JPEG and GIF, by the bytes. WebP and HEIC are not on it — Outlook draws neither.</summary>
    public static string SniffImage(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "image/gif";
        return "";
    }

    /// <summary>
    /// <see cref="SniffImage"/>, but only when the image holds together as that kind (the pentest's F7): PNG and JPEG
    /// by <see cref="TicketPhotoService.WholeImageType"/>, a GIF by its version, a width and the trailer at its end.
    /// Asked when a signature is saved — this image is served publicly and drawn by every mail program.
    /// </summary>
    public static string WholeImage(byte[] b) => SniffImage(b) switch
    {
        "image/gif" when b.Length >= 14 && (b[4] == '7' || b[4] == '9') && b[5] == 'a' && (b[6] | b[7] << 8) > 0 && b[^1] == 0x3B => "image/gif",
        "image/png" or "image/jpeg" when TicketPhotoService.WholeImageType(b) is { Length: > 0 } t => t,
        _ => ""
    };

    public static string ExtensionFor(string type) => type switch { "image/png" => ".png", "image/gif" => ".gif", _ => ".jpg" };

    /// <summary>The image's own width in pixels, read from its header; 0 when it cannot be read.</summary>
    public static int NaturalWidth(byte[] b)
    {
        switch (SniffImage(b))
        {
            case "image/png":
                return b.Length >= 24 ? (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19] : 0;
            case "image/gif":
                return b.Length >= 10 ? b[6] | (b[7] << 8) : 0;
            case "image/jpeg":
                int i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    var m = b[i + 1];
                    if (m == 0xFF) { i++; continue; }
                    if (m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC)) return (b[i + 7] << 8) | b[i + 8];
                    if (m is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
                    i += 2 + ((b[i + 2] << 8) | b[i + 3]);
                }
                return 0;
        }
        return 0;
    }

    /// <summary>The width to draw a newly uploaded image at: its own, no wider than <see cref="DefaultImageWidth"/>.</summary>
    public static int StartingWidth(byte[] b) => NaturalWidth(b) is > 0 and var w ? Math.Clamp(w, MinImageWidth, DefaultImageWidth) : DefaultImageWidth;

    public static string ImagePath(string token) => $"/quote-signature/{token}";

    // ---------------------------------------------------------------- writing it into an email

    /// <summary>The signature's words as plain text, one line each. Empty for an image-only signature.</summary>
    public static string Plain(QuoteSignature s)
    {
        var lines = LinesOf(s);
        return lines.Count == 0 ? "" : string.Join("\r\n", lines.Select(l => l.Text)) + "\r\n";
    }

    private static string StyleOf(Line l)
    {
        var stack = Fonts.First(f => f.name == l.Font).stack;
        var sb = new StringBuilder($"margin:0;font-family:{stack};font-size:{l.Size}px;line-height:1.3;color:{l.Color}");
        if (l.Style is "bold" or "bold-italic") sb.Append(";font-weight:bold");
        if (l.Style is "italic" or "bold-italic") sb.Append(";font-style:italic");
        return sb.ToString();
    }

    /// <summary>
    /// The signature as HTML: each line its own block in its own style, the image above or below.
    /// <paramref name="imageSrc"/> is <c>cid:</c> inside a draft email, the public address on a page
    /// or for pasting into web mail.
    /// </summary>
    public static string Html(QuoteSignature s, string imageSrc)
    {
        var sb = new StringBuilder();
        string Img() => HasImage(s)
            ? $"<img src=\"{WebUtility.HtmlEncode(imageSrc)}\" width=\"{Math.Clamp(s.ImageWidth, MinImageWidth, MaxImageWidth)}\" alt=\"{WebUtility.HtmlEncode(s.Name)}\" style=\"display:block;border:0;width:{Math.Clamp(s.ImageWidth, MinImageWidth, MaxImageWidth)}px;max-width:100%;height:auto;margin:6px 0\">"
            : "";
        sb.Append("<div class=\"rsp-signature\">");
        if (s.ImageFirst) sb.Append(Img());
        foreach (var l in LinesOf(s))
            sb.Append($"<div style=\"{WebUtility.HtmlEncode(StyleOf(l))}\">{(l.Text.Trim().Length == 0 ? "&nbsp;" : WebUtility.HtmlEncode(l.Text))}</div>");
        if (!s.ImageFirst) sb.Append(Img());
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>A whole email as HTML: the request's words as written, then the signature.</summary>
    public static string EmailHtml(string words, string signatureHtml) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>"
        + $"<div style=\"font-family:Arial, Helvetica, sans-serif;font-size:14px;line-height:1.4;color:#222222\">"
        + string.Join("<br>\r\n", words.TrimEnd('\r', '\n').Split("\r\n").Select(WebUtility.HtmlEncode))
        + "</div>\r\n<div style=\"margin-top:14px\">" + signatureHtml + "</div>\r\n</body></html>";

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

/// <summary>
/// The company's quote signatures: kept, chosen, remembered. Changed by the people who request quotes
/// (<see cref="Permissions.CanEditSuppliers"/>), read by anybody who may request one.
/// </summary>
public sealed class QuoteSignatureService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public QuoteSignatureService(IDbContextFactory<PlatformDb> dbf) => _dbf = dbf;

    /// <summary>The live signatures, by name.</summary>
    public async Task<List<QuoteSignature>> ListAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.QuoteSignatures.Where(s => s.TenantId == tenantId && s.RemovedUtc == null).OrderBy(s => s.Name).ToListAsync();
    }

    /// <summary>One signature — taken off the list or not, because a ticket signed with it is downloaded again the same.</summary>
    public async Task<QuoteSignature?> GetAsync(int tenantId, int? id)
    {
        if (id is not > 0) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.QuoteSignatures.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId);
    }

    /// <summary>The image behind a public address — for any company, removed or not: emails already sent point at it.</summary>
    public async Task<(byte[] bytes, string type)?> ImageAsync(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 32 || !token.All(Uri.IsHexDigit)) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        var s = await db.QuoteSignatures.Where(x => x.ImageToken == token).Select(x => new { x.Image, x.ImageType }).FirstOrDefaultAsync();
        return s?.Image is { Length: > 0 } b ? (b, s.ImageType) : null;
    }

    /// <summary>
    /// Adds (<paramref name="id"/> 0) or changes a signature. <paramref name="image"/> null keeps the
    /// one it has; <paramref name="removeImage"/> takes it off. Text or an image is needed — or both.
    /// </summary>
    public async Task<(QuoteSignature? sig, string error)> SaveAsync(int tenantId, int id, string? name, IEnumerable<QuoteSignatures.Line> lines,
        byte[]? image, bool removeImage, int imageWidth, bool imageFirst, string who, TenantRole role)
    {
        if (!Permissions.CanEditSuppliers(role)) return (null, "Signatures are set by the office administrator, a supervisor or the owner.");
        name = (name ?? "").Trim();
        if (name.Length == 0) return (null, "Give the signature a name — it is what the list shows when a quote is requested.");
        if (name.Length > QuoteSignatures.MaxNameLength) return (null, $"Keep the name under {QuoteSignatures.MaxNameLength} characters.");
        var clean = QuoteSignatures.CleanAll(lines);
        string type = "";
        if (image is { Length: > 0 })
        {
            if (image.Length > QuoteSignatures.MaxImageBytes) return (null, "That image is larger than 1 MB. Save it smaller and try again.");
            type = QuoteSignatures.WholeImage(image);
            if (type.Length == 0) return (null, "That image is not a PNG, JPEG or GIF — the kinds every mail program shows.");
        }

        await using var db = await _dbf.CreateDbContextAsync();
        var live = await db.QuoteSignatures.Where(s => s.TenantId == tenantId && s.RemovedUtc == null).ToListAsync();
        if (live.Any(s => s.Id != id && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return (null, $"There is already a signature called \"{name}\".");
        QuoteSignature sig;
        if (id == 0) { sig = new QuoteSignature { TenantId = tenantId, CreatedBy = who }; db.QuoteSignatures.Add(sig); }
        else sig = live.FirstOrDefault(s => s.Id == id)!;
        if (sig == null) return (null, "That signature is not on the list any more.");

        if (type.Length > 0)
        {
            sig.Image = image; sig.ImageType = type; sig.ImageToken = QuoteSignatures.NewToken();
            sig.ImageWidth = imageWidth > 0 ? Math.Clamp(imageWidth, QuoteSignatures.MinImageWidth, QuoteSignatures.MaxImageWidth) : QuoteSignatures.StartingWidth(image!);
        }
        else if (removeImage) { sig.Image = null; sig.ImageType = ""; sig.ImageToken = ""; sig.ImageWidth = 0; }
        else if (imageWidth > 0 && sig.Image != null) sig.ImageWidth = Math.Clamp(imageWidth, QuoteSignatures.MinImageWidth, QuoteSignatures.MaxImageWidth);

        if (clean.All(l => l.Text.Trim().Length == 0) && sig.Image == null)
            return (null, "A signature needs some text, an image, or both.");
        sig.Name = name;
        sig.LinesJson = QuoteSignatures.Serialize(clean);
        sig.ImageFirst = imageFirst;
        await db.SaveChangesAsync();
        return (sig, "");
    }

    /// <summary>Takes a signature off the list. Kept on file, for the tickets already signed with it.</summary>
    public async Task<string> RemoveAsync(int tenantId, int id, TenantRole role)
    {
        if (!Permissions.CanEditSuppliers(role)) return "Signatures are removed by the office administrator, a supervisor or the owner.";
        await using var db = await _dbf.CreateDbContextAsync();
        var s = await db.QuoteSignatures.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.RemovedUtc == null);
        if (s == null) return "";
        s.RemovedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>Which one to tick for this person: the one they used last, if it is still on the list, else the first.</summary>
    public static int Preselected(IReadOnlyList<QuoteSignature> live, int? lastUsed) =>
        live.FirstOrDefault(s => s.Id == lastUsed)?.Id ?? live.FirstOrDefault()?.Id ?? 0;

    /// <summary>
    /// The signature a request is signed with (Clint: "In the case of multiple signatures, the system
    /// should first ask which signature to use before requesting the quote"). None on the list: none,
    /// and the request is signed with the person's name as before. One: that one, nothing asked.
    /// Several: the one posted, which must be one of them — never a guess.
    /// </summary>
    public async Task<(QuoteSignature? sig, string error)> ChooseAsync(int tenantId, int? posted)
    {
        var live = await ListAsync(tenantId);
        if (live.Count == 0) return (null, "");
        if (live.Count == 1) return (live[0], "");
        var chosen = live.FirstOrDefault(s => s.Id == posted);
        return chosen == null ? (null, "Choose which signature to sign the request with.") : (chosen, "");
    }

    /// <summary>After the request: the ticket keeps the signature, and the person's next request starts on it.</summary>
    public async Task RememberAsync(int tenantId, int ticketId, int technicianId, int? signatureId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
        if (t != null) t.QuoteSignatureId = signatureId;
        if (signatureId != null)
        {
            var me = await db.Technicians.FirstOrDefaultAsync(x => x.Id == technicianId && x.TenantId == tenantId);
            if (me != null) me.LastQuoteSignatureId = signatureId;
        }
        await db.SaveChangesAsync();
    }
}
