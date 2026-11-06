using System.Text;

namespace TestChecklist;

// The end-of-day report, written for Claude to read: what was marked in the period and by whom,
// every fault and upgrade raised or touched in it, then what is still open across all days.
// Markdown, one file, with any pictures copied into a folder beside it.

static class Exporter
{
    public static string Write(MainForm host, string path, DateTime? from, DateTime? to, string label)
    {
        var board = host.Board;
        var book = host.Book;
        bool In(Entry e)
        {
            var t = e.At.ToLocalTime();
            return from == null || (t >= from && t < to);
        }
        static string When(DateTime utc) => utc.ToLocalTime().ToString("ddd d MMM HH:mm");
        static string Quote(string s) => string.Join("\n", s.Split('\n').Select(l => "  > " + l.TrimEnd('\r')));

        var md = new StringBuilder();
        var all = board.Checks.Values.ToList();
        string Tally(IEnumerable<CheckState> s) =>
            string.Join(", ", Results.All.Select(r => $"{r.Name} {s.Count(c => c.Status == r.Code)}"));

        md.AppendLine($"# Internal testing report — {label}");
        md.AppendLine();
        md.AppendLine($"Exported {DateTime.Now:yyyy-MM-dd HH:mm} by {host.Me} on {Environment.MachineName}, from `{host.Store.Root}`.");
        md.AppendLine($"Checklist: *{book.Title}* — checks.md last changed {File.GetLastWriteTime(host.Store.ChecksPath):yyyy-MM-dd HH:mm}.");
        md.AppendLine();

        // ---- summary ------------------------------------------------------------------------
        var marks = all.SelectMany(c => c.History).Where(In).ToList();
        var markedChecks = all.Where(c => c.History.Any(In)).ToList();
        var faults = board.ItemsOf(Kinds.Fault).ToList();
        var upgrades = board.ItemsOf(Kinds.Upgrade).ToList();
        bool Touched(ItemState i) => In(i.Created) || i.Changes.Any(In);

        md.AppendLine("## Summary");
        md.AppendLine();
        md.AppendLine($"- **All checks ({all.Count}):** {Tally(all)}.");
        if (from != null)
        {
            var testers = marks.GroupBy(m => m.By).Select(g => $"{g.Key} ({g.Count()})");
            md.AppendLine($"- **Marked in this report:** {markedChecks.Count} checks, {marks.Count} marks" +
                          (marks.Count > 0 ? $" — by {string.Join(", ", testers)}" : "") + ".");
        }
        md.AppendLine($"- **Faults:** {faults.Count(i => In(i.Created))} new, {faults.Count(i => !In(i.Created) && i.Changes.Any(In))} changed; " +
                      $"{faults.Count(i => i.IsOpen)} open in all.");
        md.AppendLine($"- **Upgrades:** {upgrades.Count(i => In(i.Created))} new, {upgrades.Count(i => !In(i.Created) && i.Changes.Any(In))} changed; " +
                      $"{upgrades.Count(i => i.IsOpen)} open in all.");
        md.AppendLine();

        // ---- checks marked ------------------------------------------------------------------
        md.AppendLine(from == null ? "## Every check marked so far" : "## Checks marked");
        md.AppendLine();
        if (markedChecks.Count == 0) md.AppendLine("None.").AppendLine();
        foreach (var sec in book.Sections)
        {
            var here = sec.Checks.Select(c => board.Checks[c.Number]).Where(s => s.History.Any(In)).ToList();
            if (here.Count == 0) continue;
            md.AppendLine($"### {sec.Title}");
            md.AppendLine();
            foreach (var s in here)
            {
                var inRange = s.History.Where(In).ToList();
                var last = inRange[^1];
                md.AppendLine($"- **{s.Check.Number}. {s.Check.Short}** — **{Results.Name(last.Status!)}** — {last.By}, {When(last.At)}");
                if (!string.IsNullOrWhiteSpace(last.Note)) md.AppendLine(Quote(last.Note));
                foreach (var e in inRange.Take(inRange.Count - 1).Reverse())
                {
                    md.AppendLine($"  - earlier: {Results.Name(e.Status!)} — {e.By}, {When(e.At)}");
                    if (!string.IsNullOrWhiteSpace(e.Note)) md.AppendLine("  " + Quote(e.Note));
                }
                if (s.Latest != last)
                    md.AppendLine($"  - *since then: {Results.Name(s.Status)} — {s.By}, {When(s.Latest!.At)}*");
                var logged = board.ItemsForCheck(s.Check.Number).Select(i => $"{i.Code} ({i.Status})").ToList();
                if (logged.Count > 0) md.AppendLine($"  - logged against it: {string.Join(", ", logged)}");
            }
            md.AppendLine();
        }

        // ---- faults and upgrades ------------------------------------------------------------
        string picDirName = Path.GetFileNameWithoutExtension(path) + " pictures";
        string picDir = Path.Combine(Path.GetDirectoryName(path)!, picDirName);
        int copied = 0;

        void Items(string heading, List<ItemState> list, string kind)
        {
            var touched = list.Where(Touched).OrderBy(i => i.Created.Seq).ThenBy(i => i.Created.At).ToList();
            md.AppendLine(from == null ? $"## {heading} — all of them" : $"## {heading} — new or changed");
            md.AppendLine();
            if (touched.Count == 0) { md.AppendLine("None.").AppendLine(); return; }
            foreach (var i in touched)
            {
                md.AppendLine($"### {i.Code} — {i.Title}");
                md.AppendLine();
                md.AppendLine($"- **Status:** {i.Status} · **{Kinds.LevelLabel(kind)}:** {i.Level} · **Area:** {(i.Area.Length > 0 ? i.Area : "—")}" +
                              (i.Check.Length > 0 ? $" · **Check:** {i.Check}" : ""));
                md.AppendLine($"- Raised by {i.Created.By} on {i.Created.Machine}, {When(i.Created.At)}" + (In(i.Created) ? "" : " (before this report)") + $" · id `{i.Id}`");
                md.AppendLine();
                if (i.Details.Length > 0)
                {
                    foreach (var l in i.Details.Split('\n')) md.AppendLine("> " + l);
                    md.AppendLine();
                }
                foreach (var p in i.Pictures)
                {
                    var src = Path.Combine(host.Store.PicturesDir, p);
                    if (!File.Exists(src)) { md.AppendLine($"*Picture missing from the share: {p}*"); continue; }
                    Directory.CreateDirectory(picDir);
                    File.Copy(src, Path.Combine(picDir, p), overwrite: true);
                    copied++;
                    md.AppendLine($"![{i.Code}](<{picDirName}/{p}>)");
                }
                if (i.Pictures.Count > 0) md.AppendLine();
                if (i.Changes.Count > 0)
                {
                    md.AppendLine("History:");
                    foreach (var e in i.Changes)
                    {
                        string what = e.Type == "edit" ? "edited it" : !string.IsNullOrEmpty(e.Status) ? "status → " + e.Status : "commented";
                        md.AppendLine($"- {When(e.At)}, {e.By}: {what}" + (In(e) ? "" : " *(before this report)*"));
                        if (!string.IsNullOrWhiteSpace(e.Comment)) md.AppendLine(Quote(e.Comment));
                    }
                    md.AppendLine();
                }
            }
        }
        Items("Faults", faults, Kinds.Fault);
        Items("Upgrades", upgrades, Kinds.Upgrade);

        // ---- still open, whenever it was raised -----------------------------------------------
        md.AppendLine("## Still open, across all days");
        md.AppendLine();
        void OpenList(string title, IEnumerable<ItemState> list)
        {
            var open = list.Where(i => i.IsOpen).OrderBy(i => i.Created.Seq).ToList();
            md.AppendLine($"**{title}:** " + (open.Count == 0 ? "none." : ""));
            foreach (var i in open)
                md.AppendLine($"- {i.Code} — {i.Title} — {i.Level}, {i.Status}" + (i.Check.Length > 0 ? $", check {i.Check}" : "") +
                              $" (raised {i.Created.At.ToLocalTime():d MMM} by {i.Created.By})");
            md.AppendLine();
        }
        OpenList("Faults", faults);
        OpenList("Upgrades", upgrades);

        var bad = all.Where(c => c.Status is "fault" or "partly" or "blocked").ToList();
        md.AppendLine("**Checks whose latest result is Fault, Partly works or Can't test yet:** " + (bad.Count == 0 ? "none." : ""));
        foreach (var s in bad.OrderBy(s => s.Check.Order))
        {
            md.AppendLine($"- {s.Check.Number}. {s.Check.Short} — {Results.Name(s.Status)}, {s.By} {When(s.Latest!.At)}");
            if (!string.IsNullOrWhiteSpace(s.Latest.Note)) md.AppendLine(Quote(s.Latest.Note));
        }
        md.AppendLine();

        md.AppendLine("**Not tested yet, by section:**");
        md.AppendLine();
        foreach (var sec in book.Sections)
        {
            var left = sec.Checks.Where(c => board.Checks[c.Number].Status == "untested").Select(c => c.Number).ToList();
            if (left.Count > 0) md.AppendLine($"- {sec.Key}: {string.Join(", ", left)}");
        }
        md.AppendLine();

        File.WriteAllText(path, md.ToString(), new UTF8Encoding(false));
        return $"{markedChecks.Count} checks marked, {faults.Count(Touched)} faults and {upgrades.Count(Touched)} upgrades" +
               (copied > 0 ? $", {copied} pictures copied beside it." : ".");
    }
}
