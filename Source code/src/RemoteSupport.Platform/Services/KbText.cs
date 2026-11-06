using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The knowledge base's words, drawn as a page (3.8.5).
///
/// <para>An article is plain text with a handful of line-start marks, because what technicians write
/// is steps: <c>1.</c> starts a numbered step, <c>-</c> a bullet, <c>#</c> a heading, a line of three
/// backticks opens and closes a block of commands, <c>**bold**</c> and <c>`code`</c> work inside a line,
/// and <c>[image:12]</c> on its own line is a picture. Web addresses become links.</para>
///
/// <para><b>Everything typed is HTML-encoded before anything else happens</b>, and the only tags in the
/// output are the ones written here. That is the whole reason this is not a rich-text editor storing
/// HTML: anybody at the company may edit an article and everybody reads it, so stored HTML would be a
/// way for one account to run script in every colleague's browser. Nothing is bought in, and a
/// sanitiser of our own would be a much bigger thing to get right than this.</para>
///
/// <para>One renderer, on the server: the editor's Preview asks for it rather than repeating it in
/// script, so what is previewed is what is saved.</para>
/// </summary>
public static class KbText
{
    /// <summary>Where a picture is served from. The endpoint checks the company; the id alone grants nothing.</summary>
    public static string ImageUrl(long id) => $"/portal/kb/image/{id}";

    /// <summary>How a picture is written in an article: on a line of its own.</summary>
    public static string ImageTag(long id) => $"[image:{id}]";

    private static readonly Regex Image = new(@"^\[image:(\d{1,18})\]$", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^(\d{1,4})[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^(#{1,3})\s+(.*)$", RegexOptions.Compiled);
    // Run on ENCODED text, so there is no raw quote or angle bracket left to end the attribute early.
    private static readonly Regex Url = new(@"https?://[^\s<]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);

    // ---- link buttons (Clint, 2026-10-06: "some urls can be extremely long and would look messy
    // when typed straight into the knowledgebase") ----
    // [button: Download the driver | https://…] is a button with those words. An address alone on a
    // line is a button too, named after its site ("Open hp.com"). An address inside a sentence stays a
    // link, its long tail shortened to "/…" on the page. Run on ENCODED text, like Url.
    private static readonly Regex ButtonMark = new(@"\[button:\s*(?<w>[^\]|]*?)\s*\|\s*(?<u>https?://[^\s\]<]+)\s*\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AloneUrl = new(@"^https?://\S+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>Longer than this, an address in a sentence shows as its site and "/…".</summary>
    private const int LongLink = 50;

    /// <summary>How a link button is written in an article. The editor's Link button writes this.</summary>
    public static string ButtonTag(string words, string url) => $"[button: {words.Replace("|", "/").Replace("]", ")")} | {url}]";

    /// <summary>"hp.com" from an address (encoded or not), or empty when it is not one.</summary>
    private static string SiteOf(string url)
    {
        if (!Uri.TryCreate(WebUtility.HtmlDecode(url), UriKind.Absolute, out var u) || u.Host.Length == 0) return "";
        var host = u.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    /// <summary>A button to an address. <paramref name="encUrl"/> and <paramref name="encWords"/> are already encoded.</summary>
    private static string ButtonHtml(string encUrl, string encWords)
    {
        if (encWords.Trim().Length == 0) encWords = WebUtility.HtmlEncode(SiteOf(encUrl) is { Length: > 0 } s ? $"Open {s}" : "Open the link");
        return $"<a class=\"kb-btn\" href=\"{encUrl}\" title=\"{encUrl}\" target=\"_blank\" rel=\"noopener noreferrer\">{encWords} <span aria-hidden=\"true\">↗</span></a>";
    }

    /// <param name="imageUrl">Each picture's address, from its id — <c>KnowledgeBaseService.ImageAddressesAsync</c>, which
    /// gives this company's own (HANDOVER §159); null for a picture that is not theirs. Left out, the old id address.</param>
    public static string Render(string? body, Func<long, string?>? imageUrl = null)
    {
        var lines = (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var html = new StringBuilder();
        // The block being built: "p", "ol" or "ul", and its lines or items.
        string kind = "";
        var items = new List<string>();
        int olStart = 1;

        void Flush()
        {
            if (kind == "p" && items.Count > 0)
                html.Append("<p>").Append(string.Join("<br>", items)).Append("</p>\n");
            else if (kind is "ol" or "ul" && items.Count > 0)
            {
                html.Append(kind == "ol" ? (olStart != 1 ? $"<ol start=\"{olStart}\">" : "<ol>") : "<ul>");
                foreach (var i in items) html.Append("<li>").Append(i).Append("</li>");
                html.Append(kind == "ol" ? "</ol>\n" : "</ul>\n");
            }
            kind = ""; items.Clear();
        }

        for (int n = 0; n < lines.Length; n++)
        {
            var raw = lines[n];
            var line = raw.Trim();

            // A block of commands: everything until the closing fence, exactly as typed.
            if (line.StartsWith("```"))
            {
                Flush();
                var code = new List<string>();
                for (n++; n < lines.Length && !lines[n].Trim().StartsWith("```"); n++) code.Add(lines[n].TrimEnd());
                html.Append("<pre><code>").Append(WebUtility.HtmlEncode(string.Join("\n", code))).Append("</code></pre>\n");
                continue;
            }
            if (line.Length == 0) { Flush(); continue; }

            Match m;
            if ((m = Image.Match(line)).Success)
            {
                Flush();
                // A portal page names each picture by its address there (HANDOVER §159), never by its id.
                var id = long.Parse(m.Groups[1].Value);
                var src = imageUrl == null ? ImageUrl(id) : imageUrl(id);
                if (src == null) { html.Append("<p class=\"muted\">(A picture that is not on file.)</p>\n"); continue; }
                html.Append($"<figure class=\"kb-fig\"><a href=\"{src}\" target=\"_blank\" rel=\"noopener\">")
                    .Append($"<img src=\"{src}\" alt=\"Picture\" loading=\"lazy\"></a></figure>\n");
                continue;
            }
            if (AloneUrl.IsMatch(line) && TrimTail(WebUtility.HtmlEncode(line)) is var (alone, rest) && rest.Length == 0 && alone.Length >= 9)
            {
                // An address on a line of its own: a button named after its site, not the address.
                Flush();
                html.Append("<p class=\"kb-btn-line\">").Append(ButtonHtml(alone, "")).Append("</p>\n");
                continue;
            }
            if ((m = Heading.Match(line)).Success)
            {
                Flush();
                // An article's own title is the page's heading, so the article's go one level down.
                var level = m.Groups[1].Value.Length + 1;
                html.Append($"<h{level}>").Append(Inline(m.Groups[2].Value)).Append($"</h{level}>\n");
                continue;
            }
            if ((m = Numbered.Match(line)).Success)
            {
                if (kind != "ol") { Flush(); kind = "ol"; olStart = int.Parse(m.Groups[1].Value); }
                if (m.Groups[2].Value.Trim().Length > 0) items.Add(Inline(m.Groups[2].Value));
                continue;
            }
            if ((m = Bullet.Match(line)).Success)
            {
                if (kind != "ul") { Flush(); kind = "ul"; }
                if (m.Groups[1].Value.Trim().Length > 0) items.Add(Inline(m.Groups[1].Value));
                continue;
            }
            // A plain line straight after a step belongs to that step (a note under it), when it is
            // indented — the way people continue a list item by hand. Otherwise it is a paragraph.
            if (kind is "ol" or "ul" && items.Count > 0 && raw.Length > 0 && char.IsWhiteSpace(raw[0]))
            {
                items[^1] += "<br>" + Inline(line);
                continue;
            }
            if (kind != "p") { Flush(); kind = "p"; }
            items.Add(Inline(line));
        }
        Flush();
        return html.ToString();
    }

    /// <summary>One line's own marks: `code` first (nothing inside it is touched), then bold and links.</summary>
    private static string Inline(string text)
    {
        var parts = text.Split('`');
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            // An odd part is between backticks — unless it is the last part, which means the closing
            // backtick is missing and the backtick was just a character.
            bool code = i % 2 == 1 && i < parts.Length - 1;
            var enc = WebUtility.HtmlEncode(parts[i]);
            if (code) { sb.Append("<code>").Append(enc).Append("</code>"); continue; }
            if (i % 2 == 1) sb.Append('`');
            // Buttons first, set aside, so the address inside one is not made a link a second time.
            var buttons = new List<string>();
            enc = enc.Replace("\u0001", "").Replace("\u0002", "");   // the set-aside marks are never typed ones
            enc = ButtonMark.Replace(enc, b =>
            {
                buttons.Add(ButtonHtml(b.Groups["u"].Value, b.Groups["w"].Value));
                return $"\u0001{buttons.Count - 1}\u0002";
            });
            enc = Bold.Replace(enc, "<strong>$1</strong>");
            enc = Url.Replace(enc, u =>
            {
                var (url, tail) = TrimTail(u.Value);
                if (url.Length < 9) return u.Value;
                // A long address in a sentence shows as its site and "/…"; the whole of it is on hover.
                if (url.Length <= LongLink || SiteOf(url) is not { Length: > 0 } site)
                    return $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener noreferrer\">{url}</a>{tail}";
                return $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener noreferrer\" title=\"{url}\">{site}/…</a>{tail}";
            });
            enc = Regex.Replace(enc, "\u0001(\\d+)\u0002", b => buttons[int.Parse(b.Groups[1].Value)]);
            sb.Append(enc);
        }
        return sb.ToString();
    }

    /// <summary>An address with a sentence's full stop, bracket or quote taken off its end, and what was taken.</summary>
    private static (string url, string tail) TrimTail(string encoded)
    {
        var url = encoded;
        var tail = "";
        for (bool more = true; more && url.Length > 0;)
        {
            more = false;
            // A quote typed round the address arrives encoded; it is not part of it either.
            foreach (var q in new[] { "&quot;", "&#39;" })
                if (url.EndsWith(q)) { tail = q + tail; url = url[..^q.Length]; more = true; }
            if (url.Length > 0 && ".,;:!?)".Contains(url[^1])) { tail = url[^1] + tail; url = url[..^1]; more = true; }
        }
        return (url, tail);
    }

    /// <summary>
    /// The first words of an article as plain text, for a list. Marks taken off, pictures left out,
    /// runs of space made one.
    /// </summary>
    public static string Snippet(string? body, int max = 180)
    {
        var sb = new StringBuilder();
        bool inCode = false;
        foreach (var raw in (body ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```")) { inCode = !inCode; continue; }
            if (line.Length == 0 || Image.IsMatch(line)) continue;
            if (!inCode)
            {
                Match m;
                if ((m = Heading.Match(line)).Success) line = m.Groups[2].Value;
                else if ((m = Numbered.Match(line)).Success) line = m.Groups[1].Value + ". " + m.Groups[2].Value;
                else if ((m = Bullet.Match(line)).Success) line = m.Groups[1].Value;
                line = line.Replace("**", "").Replace("`", "");
                // A button reads as its words in a list, never its address.
                line = ButtonMark.Replace(line, b => b.Groups["w"].Value.Trim() is { Length: > 0 } w ? w : SiteOf(b.Groups["u"].Value));
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(line);
            if (sb.Length > max) break;
        }
        var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }

    /// <summary>The picture ids an article uses, so a page can be told which ones it may show.</summary>
    public static IEnumerable<long> ImageIds(string? body) =>
        (body ?? "").Replace("\r", "").Split('\n').Select(l => Image.Match(l.Trim()))
            .Where(m => m.Success).Select(m => long.Parse(m.Groups[1].Value));

    // ---- passwords (Clint's answer 6: "never a password") ----

    // "Password: Summer2026", "pwd = x", "admin pw is hunter2", "PIN 1234". A word that says what
    // follows is a secret, then something that looks like the secret. Deliberately simple: it is a
    // nudge on a page anybody at the company can read, never a refusal.
    private static readonly Regex Secret = new(
        @"\b(pass\s?word|passwd|pwd|pw|passcode|pin|wi-?fi key|network key)\b\s*(is|=|:|-)?\s*(?<v>[^\s,;]{3,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Words that mean the line is ABOUT a password rather than giving one: "set a password for them",
    // "reset password via the portal". Without these every article about resetting one is flagged.
    private static readonly HashSet<string> NotASecret = new(StringComparer.OrdinalIgnoreCase)
    {
        "for", "to", "the", "a", "an", "of", "and", "or", "in", "on", "via", "with", "from", "field", "box",
        "reset", "change", "changed", "manager", "policy", "expiry", "expires", "prompt", "page", "screen",
        "they", "their", "them", "you", "your", "it", "is", "will", "must", "should", "needs", "required",
        "here", "there", "that", "this", "when", "if", "set", "new", "old", "same", "who", "as", "be",
        "never", "not", "no", "hidden", "stored", "vault", "ticket", "client's", "user's", "users", "user",
    };

    /// <summary>
    /// The first line that looks as if it gives a password, with the secret itself hidden, or empty.
    /// The page shows it so the person can find the line — never the secret.
    /// </summary>
    public static string LooksLikePassword(string? body)
    {
        bool inCode = false;
        foreach (var raw in (body ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```")) { inCode = !inCode; continue; }
            foreach (Match m in Secret.Matches(line))
            {
                var v = m.Groups["v"].Value.Trim('"', '\'', '.', '*', '`');
                if (v.Length < 3 || NotASecret.Contains(v)) continue;
                // "Password: ********" or "PIN: xxxx" is somebody already hiding it.
                if (v.All(c => c is '*' or '•' or 'x' or 'X' or '#')) continue;
                var shown = line[..m.Groups["v"].Index] + new string('•', 6) + line[(m.Groups["v"].Index + m.Groups["v"].Length)..];
                return shown.Length > 120 ? shown[..120] + "…" : shown;
            }
        }
        return "";
    }
}
