using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestChecklist;

// Everything anyone records is one line appended to a file of their own:
// data\<name> on <machine>.jsonl. Nobody ever writes to anyone else's file, so several people on
// the shared drive at once cannot overwrite each other, and nothing needs locking. The current
// state is simply every file read and replayed in time order. A line half-written at the moment
// someone else reads it fails to parse and is skipped; the next refresh picks it up whole.
//
// Claude can add a file of its own here (Claude.jsonl) in the same shape — to mark a fault
// "Fixed — please retest", say — and it shows up like anyone else's.

sealed class Entry
{
    /// result | item | edit | update
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public DateTime At { get; set; }
    public string By { get; set; } = "";
    public string Machine { get; set; } = "";

    // result: Check, Status, Note
    public string? Check { get; set; }
    public string? Status { get; set; }
    public string? Note { get; set; }

    // item (a fault or an upgrade) and edit: the item's fields. edit carries them all again.
    public string? Kind { get; set; }
    public int? Seq { get; set; }
    public string? Title { get; set; }
    public string? Details { get; set; }
    public string? Area { get; set; }
    public string? Level { get; set; }
    public List<string>? Pictures { get; set; }

    // edit and update: which item. update carries Status (the item's new one) and/or Comment.
    public string? Target { get; set; }
    public string? Comment { get; set; }
}

sealed class Store
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Root { get; }
    public string DataDir { get; }
    public string PicturesDir { get; }
    public string ChecksPath { get; }
    public string ExportsDir { get; }
    public List<Entry> Entries { get; private set; } = new();

    string _myFile = "";
    Dictionary<string, (long, DateTime)> _stamps = new();

    public Store(string root)
    {
        Root = root;
        DataDir = Path.Combine(root, "data");
        PicturesDir = Path.Combine(DataDir, "pictures");
        ChecksPath = Path.Combine(root, "checks.md");
        ExportsDir = Path.Combine(root, "exports");
    }

    public void SetMe(string me) =>
        _myFile = Path.Combine(DataDir, Safe(me) + " on " + Safe(Environment.MachineName) + ".jsonl");

    static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    /// Re-reads every log if any of them has changed. Returns whether it did.
    public bool Reload(bool force)
    {
        var files = Directory.Exists(DataDir) ? Directory.GetFiles(DataDir, "*.jsonl") : Array.Empty<string>();
        var stamps = new Dictionary<string, (long, DateTime)>();
        foreach (var f in files)
        {
            try { var i = new FileInfo(f); stamps[f] = (i.Length, i.LastWriteTimeUtc); } catch (IOException) { }
        }
        if (!force && stamps.Count == _stamps.Count && stamps.All(kv => _stamps.TryGetValue(kv.Key, out var v) && v == kv.Value))
            return false;

        var seen = new HashSet<string>();
        var list = new List<Entry>();
        foreach (var f in stamps.Keys)
        {
            string text;
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs, Encoding.UTF8);
                text = r.ReadToEnd();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var line in text.Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                Entry? e;
                try { e = JsonSerializer.Deserialize<Entry>(line, Json); } catch (JsonException) { continue; }
                if (e == null || e.Id.Length == 0 || e.Type.Length == 0 || !seen.Add(e.Id)) continue;
                if (e.At.Kind == DateTimeKind.Unspecified) e.At = DateTime.SpecifyKind(e.At, DateTimeKind.Utc);
                list.Add(e);
            }
        }
        Entries = list.OrderBy(e => e.At).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        _stamps = stamps;
        return true;
    }

    public void Append(Entry e)
    {
        Directory.CreateDirectory(DataDir);
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(e, Json) + "\n");
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(_myFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                fs.Write(bytes);
                fs.Flush(true);
                return;
            }
            catch (IOException) when (attempt < 30)
            {
                // The same person with the program open twice on one machine: take turns.
                Thread.Sleep(100);
            }
        }
    }
}
