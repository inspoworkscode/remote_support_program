using System.Globalization;

namespace TestChecklist;

// What the logs add up to: each check's result and history, and each fault and upgrade as it
// stands after its edits and updates. Rebuilt from scratch on every refresh — it is cheap.

static class Results
{
    public static readonly (string Code, string Name, Color Back, Color Fore)[] All =
    {
        ("untested", "Not tested",     Color.Empty,                    Color.Empty),
        ("works",    "Works",          Color.FromArgb(0xDD, 0xF2, 0xDB), Color.FromArgb(0x1E, 0x6B, 0x2A)),
        ("partly",   "Partly works",   Color.FromArgb(0xFF, 0xEF, 0xC4), Color.FromArgb(0x8A, 0x5A, 0x00)),
        ("fault",    "Fault",          Color.FromArgb(0xF8, 0xD9, 0xD6), Color.FromArgb(0xA3, 0x1D, 0x14)),
        ("blocked",  "Can't test yet", Color.FromArgb(0xE2, 0xE9, 0xF4), Color.FromArgb(0x33, 0x4E, 0x7A)),
        ("skip",     "Skip",           Color.FromArgb(0xEC, 0xEC, 0xEC), Color.FromArgb(0x6B, 0x6B, 0x6B)),
    };

    public static string Name(string code) => All.FirstOrDefault(r => r.Code == code).Name ?? code;
    public static Color Back(string code) => All.FirstOrDefault(r => r.Code == code).Back;
    public static Color Fore(string code) => All.FirstOrDefault(r => r.Code == code).Fore;
}

static class Kinds
{
    public const string Fault = "fault", Upgrade = "upgrade";

    public static string[] Statuses(string kind) => kind == Fault
        ? new[] { "Open", "Fixed — please retest", "Closed", "Not a fault" }
        : new[] { "Open", "Planned", "Done", "Won't do" };

    public static bool IsOpen(string kind, string status) => kind == Fault
        ? status is "Open" or "Fixed — please retest"
        : status is "Open" or "Planned";

    public static string[] Levels(string kind) => kind == Fault
        ? new[] { "Stops work", "Serious", "Minor", "Cosmetic" }
        : new[] { "Must have", "Should have", "Nice to have" };

    public static string LevelLabel(string kind) => kind == Fault ? "Severity" : "Priority";
    public static string Noun(string kind) => kind == Fault ? "fault" : "upgrade";
    public static string Prefix(string kind) => kind == Fault ? "F" : "U";

    public static readonly string[] Areas =
    {
        "Console", "Session window", "Tray app (customer)", "Portal", "Tickets", "Installer",
        "Health check", "Server / platform", "Other",
    };
}

sealed class CheckState
{
    public required Check Check { get; init; }
    public List<Entry> History { get; } = new();
    public Entry? Latest => History.Count > 0 ? History[^1] : null;

    public string Status => Latest?.Status
        ?? (Check.DoneInDoc != null ? "works" : Check.ReplacedBy != null ? "skip" : "untested");

    public string By => Latest?.By ?? (Check.DoneInDoc != null || Check.ReplacedBy != null ? "checklist" : "");

    public DateTime? When
    {
        get
        {
            if (Latest != null) return Latest.At.ToLocalTime();
            if (Check.DoneInDoc != null && DateTime.TryParseExact(Check.DoneInDoc, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            return null;
        }
    }

    /// What the checklist itself says, for a check nobody has marked yet.
    public string? DocNote => Latest != null ? null
        : Check.DoneInDoc != null ? $"The checklist says this was done by hand ({Check.DoneInDoc})."
        : Check.ReplacedBy != null ? $"Section {Check.ReplacedBy} of the checklist replaces this check — do that one instead."
        : null;
}

sealed class ItemState
{
    public required Entry Created { get; init; }
    public string Id => Created.Id;
    public string Kind => Created.Kind ?? Kinds.Fault;
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Details { get; set; } = "";
    public string Area { get; set; } = "";
    public string Level { get; set; } = "";
    public string Check { get; set; } = "";
    public List<string> Pictures { get; } = new();
    public string Status { get; set; } = "Open";
    public List<Entry> Changes { get; } = new();
    public DateTime Last { get; set; }
    public bool IsOpen => Kinds.IsOpen(Kind, Status);

    public void Apply(Entry e)
    {
        if (e.Title != null) Title = e.Title;
        if (e.Details != null) Details = e.Details;
        if (e.Area != null) Area = e.Area;
        if (e.Level != null) Level = e.Level;
        if (e.Check != null) Check = e.Check;
        if (e.Pictures != null) Pictures.AddRange(e.Pictures.Where(p => !Pictures.Contains(p)));
        Last = e.At;
    }
}

sealed class Board
{
    public CheckBook Book { get; }
    public Dictionary<string, CheckState> Checks { get; } = new();
    public Dictionary<string, ItemState> Items { get; } = new();

    public Board(CheckBook book, IReadOnlyList<Entry> entries)
    {
        Book = book;
        foreach (var c in book.All) Checks.TryAdd(c.Number, new CheckState { Check = c });

        foreach (var e in entries)
        {
            switch (e.Type)
            {
                case "result":
                    if (e.Check != null && e.Status != null && Checks.TryGetValue(e.Check, out var cs))
                        cs.History.Add(e);
                    break;
                case "item":
                    if (!Items.ContainsKey(e.Id))
                    {
                        var it = new ItemState { Created = e };
                        it.Apply(e);
                        Items[e.Id] = it;
                    }
                    break;
                case "edit":
                    if (e.Target != null && Items.TryGetValue(e.Target, out var ed)) { ed.Apply(e); ed.Changes.Add(e); }
                    break;
                case "update":
                    if (e.Target != null && Items.TryGetValue(e.Target, out var up))
                    {
                        if (!string.IsNullOrEmpty(e.Status)) up.Status = e.Status;
                        up.Last = e.At;
                        up.Changes.Add(e);
                    }
                    break;
            }
        }

        // Numbers are handed out as "one more than the highest anyone can see", so two people saving
        // in the same instant could both take F12. Nobody's number changes because of it: the later
        // one simply shows as F12b.
        foreach (var kind in new[] { Kinds.Fault, Kinds.Upgrade })
            foreach (var g in Items.Values.Where(i => i.Kind == kind).GroupBy(i => i.Created.Seq ?? 0))
            {
                int n = 0;
                foreach (var i in g.OrderBy(i => i.Created.At))
                    i.Code = Kinds.Prefix(kind) + g.Key + (n++ == 0 ? "" : ((char)('a' + n - 1)).ToString());
            }
    }

    public int NextSeq(string kind) =>
        Items.Values.Where(i => i.Kind == kind).Select(i => i.Created.Seq ?? 0).DefaultIfEmpty(0).Max() + 1;

    public IEnumerable<ItemState> ItemsOf(string kind) => Items.Values.Where(i => i.Kind == kind);

    public IEnumerable<ItemState> ItemsForCheck(string number) =>
        Items.Values.Where(i => i.Check == number).OrderBy(i => i.Created.At);
}
