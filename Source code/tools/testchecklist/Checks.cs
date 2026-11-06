using System.Text.RegularExpressions;

namespace TestChecklist;

// The checklist is docs/TEST-3.6.md itself, copied to the share as checks.md. It is read, never
// written: the document stays the one place checks are described, and updating the share after a
// day's fixes is a file copy. Its shape is "## A. Title", an intro, then "12. **Short.** More"
// with continuation lines indented.

sealed class Check
{
    public required string Number { get; init; }
    public required Section Section { get; init; }
    public List<string> Lines { get; } = new();
    public string Short { get; set; } = "";

    /// The date when the document itself says "**DONE by hand 2026-09-21.**".
    public string? DoneInDoc { get; set; }

    /// The section that says it REPLACES this check, when one does.
    public string? ReplacedBy { get; set; }

    public int Order => int.TryParse(Number, out var n) ? n : int.MaxValue;
}

sealed class Section
{
    /// "A", "AB" — or the whole heading for a section with no letter.
    public required string Key { get; init; }
    public required string Title { get; init; }
    public List<string> Intro { get; } = new();
    public List<Check> Checks { get; } = new();
}

sealed class CheckBook
{
    public string Title { get; private set; } = "";
    public List<Section> Sections { get; } = new();
    public Dictionary<string, Check> ByNumber { get; } = new();
    public IEnumerable<Check> All => Sections.SelectMany(s => s.Checks);

    static readonly Regex ItemRx = new(@"^(\d+)\.\s+(.*)$");
    static readonly Regex KeyRx = new(@"^([A-Z]{1,3})\.\s");
    static readonly Regex DoneRx = new(@"^\*\*DONE\b([^*]*)\*\*\s*", RegexOptions.IgnoreCase);
    static readonly Regex DateRx = new(@"\d{4}-\d{2}-\d{2}");
    static readonly Regex ReplacesRx = new(@"REPLACES\s+checks?\s+([\d,\s]+(?:and\s+\d+)?)", RegexOptions.IgnoreCase);

    public static CheckBook Load(string path)
    {
        var book = new CheckBook();
        var cur = new Section { Key = "Read first", Title = "Read first" };
        book.Sections.Add(cur);

        Check? item = null;
        bool fence = false, fenceInItem = false;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd();
            bool indented = line.Length > 0 && char.IsWhiteSpace(line[0]);
            bool fenceMark = line.TrimStart().StartsWith("```");

            if (fence)
            {
                if (fenceInItem && item != null) item.Lines.Add(line.Trim()); else cur.Intro.Add(line);
                if (fenceMark) fence = false;
                continue;
            }
            if (fenceMark)
            {
                fence = true;
                fenceInItem = item != null && indented;
                if (fenceInItem) item!.Lines.Add(line.Trim());
                else { item = null; cur.Intro.Add(line); }
                continue;
            }
            if (line.StartsWith("## "))
            {
                var title = line[3..].Trim();
                var m = KeyRx.Match(title);
                cur = new Section { Key = m.Success ? m.Groups[1].Value : title, Title = title };
                book.Sections.Add(cur);
                item = null;
                continue;
            }
            if (line.StartsWith("# "))
            {
                book.Title = line[2..].Trim();
                continue;
            }
            var im = ItemRx.Match(line);
            if (im.Success)
            {
                item = new Check { Number = im.Groups[1].Value, Section = cur };
                item.Lines.Add(im.Groups[2].Value);
                cur.Checks.Add(item);
                book.ByNumber.TryAdd(item.Number, item);
                continue;
            }
            if (item != null && (line.Length == 0 || indented))
            {
                item.Lines.Add(line.Trim());
                continue;
            }
            item = null;
            cur.Intro.Add(line);
        }

        foreach (var s in book.Sections)
        {
            Tidy(s.Intro);
            foreach (var c in s.Checks) { Tidy(c.Lines); Describe(c); }
        }
        book.Sections.RemoveAll(s => s.Checks.Count == 0 && s.Intro.Count == 0);

        // "This REPLACES checks 66, 67, 79, 82 and 83" — those are no longer worth anyone's time.
        foreach (var s in book.Sections)
        {
            var m = ReplacesRx.Match(string.Join(" ", s.Intro));
            if (!m.Success) continue;
            foreach (Match n in Regex.Matches(m.Groups[1].Value, @"\d+"))
                if (book.ByNumber.TryGetValue(n.Value, out var old) && old.Section != s)
                    old.ReplacedBy = s.Key;
        }
        return book;
    }

    static void Tidy(List<string> lines)
    {
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && (lines[^1].Trim().Length == 0 || lines[^1].Trim() == "---")) lines.RemoveAt(lines.Count - 1);
    }

    static void Describe(Check c)
    {
        var first = string.Join(" ", c.Lines.TakeWhile(l => l.Length > 0));
        var done = DoneRx.Match(first);
        if (done.Success)
        {
            var date = DateRx.Match(done.Groups[1].Value);
            c.DoneInDoc = date.Success ? date.Value : "earlier";
            first = first[done.Length..];
        }

        string s;
        if (first.StartsWith("**") && first.IndexOf("**", 2) is var end and > 2)
            s = first[2..end];
        else
        {
            int stop = first.IndexOf(". ");
            s = stop > 0 ? first[..stop] : first;
        }
        s = s.Replace("**", "").Replace("`", "").Replace("*", "").Trim().TrimEnd('.', ':').Trim();
        c.Short = s.Length > 140 ? s[..137] + "…" : s;
    }
}
