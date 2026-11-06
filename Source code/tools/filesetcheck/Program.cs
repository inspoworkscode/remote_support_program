using System.Security.Cryptography;
using System.Text;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Tests the whole-tree transfer engine on this machine alone, with no server, no session and no
// customer machine involved.
//
// Everything that is genuinely hard about copying folders is hard here, not on the wire: empty
// folders that carry meaning, zero-byte files that never produce a chunk, two selected folders
// with the same name, a name that already exists at the destination, and — the one that matters
// most — a relative path crafted to escape the destination. The receiving side on a customer
// machine runs as SYSTEM, so a path like "..\..\Windows\System32\x" would be written with full
// machine rights if the check were missing.
//
// Being able to run all of that in two seconds, without deploying anything, is the difference
// between finding these now and finding them on a customer's machine.
//
//   filesetcheck
// ---------------------------------------------------------------------------------------------

int passed = 0, failed = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { failed++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

var root = Path.Combine(Path.GetTempPath(), "filesetcheck-" + Guid.NewGuid().ToString("N")[..8]);
var source = Path.Combine(root, "source");
var dest = Path.Combine(root, "dest");
Directory.CreateDirectory(source);
Directory.CreateDirectory(dest);

// ---- build a tree with every awkward case in it ---------------------------------------------
var rnd = new Random(1234);
string MakeFile(string path, int size)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var bytes = new byte[size];
    rnd.NextBytes(bytes);
    File.WriteAllBytes(path, bytes);
    return Convert.ToHexString(SHA256.HashData(bytes));
}

var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var projects = Path.Combine(source, "Projects");
expected["Projects/notes.txt"] = MakeFile(Path.Combine(projects, "notes.txt"), 1200);
expected["Projects/big.bin"] = MakeFile(Path.Combine(projects, "big.bin"), 700_000);   // several chunks
expected["Projects/sub/deep/inner.dat"] = MakeFile(Path.Combine(projects, "sub", "deep", "inner.dat"), 4096);
expected["Projects/zero.txt"] = MakeFile(Path.Combine(projects, "zero.txt"), 0);        // never sends a chunk
expected["Projects/\u00e9\u00fc caf\u00e9.txt"] = MakeFile(Path.Combine(projects, "\u00e9\u00fc caf\u00e9.txt"), 300);
Directory.CreateDirectory(Path.Combine(projects, "empty folder"));                      // carries meaning
var loose = Path.Combine(source, "loose.txt");
expected["loose.txt"] = MakeFile(loose, 50);

Console.WriteLine("PLAN — walking the selection");
var plan = FileSetSender.Plan(new[] { projects, loose });
Check("every file was found", plan.FileCount == 6, $"{plan.FileCount} files, {FileSetLimits.Human(plan.TotalBytes)}");
Check("the empty folder is in the plan",
    plan.Entries.Any(e => e.IsDir && e.Path.Replace('\\', '/') == "Projects/empty folder"));
Check("nothing was skipped", plan.Skipped.Count == 0,
    plan.Skipped.Count == 0 ? "" : string.Join("; ", plan.Skipped));

// ---- run it through the receiver, exactly as the wire would ----------------------------------
Console.WriteLine();
Console.WriteLine("TRANSFER — through the real receiver, chunk by chunk");
var offer = new FileSetOfferMsg { SetId = "test1", Entries = plan.Entries, TotalBytes = plan.TotalBytes, Label = plan.Label };
var rx = new FileSetReceiver(offer);
if (!rx.Begin(dest, out var why)) { Check("the receiver accepted the offer", false, why); goto summary; }
Check("the receiver accepted the offer", true, rx.TargetDir);

await FileSetSender.SendAsync("test1", plan, frame =>
{
    var (id, off, data) = Bin.ParseFileChunk(frame);
    int hash = id.IndexOf('#');
    rx.Chunk(int.Parse(id[(hash + 1)..]), off, data);
    return true;
}, null, CancellationToken.None);
var done = rx.Done();

Check("the transfer reported success", done.Ok, done.Error ?? "");
Check("every file was written", rx.FilesDone == plan.FileCount, $"{rx.FilesDone} of {plan.FileCount}");

// ---- the only answer that matters: are the bytes the same? -----------------------------------
Console.WriteLine();
Console.WriteLine("VERIFY — byte for byte");
int good = 0;
var wrong = new List<string>();
foreach (var (rel, hash) in expected)
{
    var landed = Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(landed)) { wrong.Add(rel + " (missing)"); continue; }
    var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(landed)));
    if (actual == hash) good++;
    else wrong.Add(rel + " (different bytes)");
}
Check("every file arrived identical", wrong.Count == 0, wrong.Count == 0 ? $"{good} files" : string.Join("; ", wrong));
Check("the zero-byte file exists", File.Exists(Path.Combine(dest, "Projects", "zero.txt")),
    "no chunk is ever sent for it, so it has to be created up front");
Check("the empty folder survived", Directory.Exists(Path.Combine(dest, "Projects", "empty folder")));
Check("the non-ASCII name survived", File.Exists(Path.Combine(dest, "Projects", "\u00e9\u00fc caf\u00e9.txt")));

// ---- what the sender is told landed, which becomes the far clipboard -------------------------
// SavedPaths is not bookkeeping. It is what the technician's console puts on the customer's
// clipboard after a paste, so a SavedPaths that names one item when three arrived is a Ctrl+V
// that pastes one file \u2014 reported exactly that way on 2026-09-18. Nothing checked it until now:
// it is produced here, consumed two projects away, and fabricated by every harness in between.
{
    var tops = plan.Entries.Select(e => e.Path.Replace('\\', '/').Split('/')[0])
                   .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    Check("every top-level item is reported back as saved, not just the first",
        rx.SavedPaths.Count == tops.Count,
        $"{rx.SavedPaths.Count} reported, {tops.Count} sent: {string.Join(" | ", rx.SavedPaths.Select(Path.GetFileName))}");
    Check("and every path it reports actually exists",
        rx.SavedPaths.All(p => File.Exists(p) || Directory.Exists(p)),
        "a path that does not exist is a clipboard pointing at nothing");
}

// ---- a target name this machine does not know ------------------------------------------------
// A newer console against an older client. .NET reads "%PASTE%" as a RELATIVE path, so without a
// refusal the transfer succeeds into a literal folder of that name under the receiver's working
// directory — Program Files, on the host — and the technician is told it arrived. That is
// docs/BUGS.md #6 wearing a different name, and it has been paid for once already.
{
    var sentinelOffer = new FileSetOfferMsg
    {
        SetId = "sentinel", Entries = plan.Entries, TotalBytes = plan.TotalBytes,
        TargetDir = RemotePaths.PasteStaging
    };
    var srx = new FileSetReceiver(sentinelOffer);
    bool refused = !srx.Begin(Path.Combine(root, "sentinel-dest"), out var swhy);
    Check("an unresolved sentinel target is refused rather than taken as a relative path", refused,
        refused ? swhy : "IT WAS ACCEPTED — the files would land in a folder called %PASTE%");
    Check("and nothing was written into a folder named after the sentinel",
        !Directory.Exists(Path.Combine(Environment.CurrentDirectory, RemotePaths.PasteStaging)));
}

// ---- several loose files, which is what a multi-select copy really is ------------------------
// The tree above has ONE top-level folder, so it cannot tell a list that carries everything from
// one that carries its first entry. A desktop multi-select is several tops and no folder at all.
{
    var looseDir = Path.Combine(root, "loose");
    Directory.CreateDirectory(looseDir);
    var names = new[] { "first.txt", "second file with spaces.txt", "th\u00ecrd \u00e9.txt" };
    foreach (var n in names) File.WriteAllText(Path.Combine(looseDir, n), n);

    var lplan = FileSetSender.Plan(names.Select(n => Path.Combine(looseDir, n)));
    Check("three separately-selected files plan as three", lplan.FileCount == 3,
        $"{lplan.FileCount} files, {FileSetLimits.Human(lplan.TotalBytes)}");

    var ldest = Path.Combine(root, "loose-dest");
    var lrx = new FileSetReceiver(new FileSetOfferMsg
    {
        SetId = "loose", Entries = lplan.Entries, TotalBytes = lplan.TotalBytes, Label = lplan.Label
    });
    if (!lrx.Begin(ldest, out var lwhy)) Check("the receiver accepted three loose files", false, lwhy);
    else
    {
        Check("the receiver accepted three loose files", true, lrx.TargetDir);
        for (int i = 0; i < lplan.Entries.Count; i++)
        {
            if (lplan.Entries[i].IsDir) continue;
            var bytes = File.ReadAllBytes(lplan.Sources[i]);
            lrx.Chunk(i, 0, bytes);
        }
        Check("all three were written", lrx.FilesDone == 3, $"{lrx.FilesDone} of 3");
        Check("all three come back as saved paths, so Ctrl+V would paste three",
            lrx.SavedPaths.Count == 3,
            $"{lrx.SavedPaths.Count} of 3: {string.Join(" | ", lrx.SavedPaths.Select(Path.GetFileName))}");
        Check("including the one with a space and the one with a non-ASCII name",
            lrx.SavedPaths.Any(p => Path.GetFileName(p) == "second file with spaces.txt")
            && lrx.SavedPaths.Any(p => Path.GetFileName(p) == "th\u00ecrd \u00e9.txt"),
            string.Join(" | ", lrx.SavedPaths.Select(Path.GetFileName)));
    }
}

// ---- a second transfer into the same place must not overwrite the first ----------------------
Console.WriteLine();
Console.WriteLine("COLLISION — sending the same thing again");
var rx2 = new FileSetReceiver(new FileSetOfferMsg { SetId = "test2", Entries = plan.Entries, TotalBytes = plan.TotalBytes });
if (!rx2.Begin(dest, out var why2)) Check("the second transfer was accepted", false, why2);
else
{
    await FileSetSender.SendAsync("test2", plan, frame =>
    {
        var (id, off, data) = Bin.ParseFileChunk(frame);
        int hash = id.IndexOf('#');
        rx2.Chunk(int.Parse(id[(hash + 1)..]), off, data);
        return true;
    }, null, CancellationToken.None);
    rx2.Done();
    bool renamed = Directory.Exists(Path.Combine(dest, "Projects (2)"));
    Check("the second copy was renamed rather than merged", renamed,
        renamed ? "Projects (2)" : "it overwrote or merged into the first");
    bool childrenFollowed = File.Exists(Path.Combine(dest, "Projects (2)", "sub", "deep", "inner.dat"));
    Check("the renamed folder's children followed it", childrenFollowed,
        childrenFollowed ? "" : "the folder was renamed but its contents went to the original");
    bool firstIntact = File.Exists(Path.Combine(dest, "Projects", "notes.txt"));
    Check("the first copy was left alone", firstIntact);
}

// ---- the security check ----------------------------------------------------------------------
// The receiving side on a customer machine runs as SYSTEM. A crafted relative path must never be
// written, and one bad entry must condemn the whole set rather than writing the plausible part.
Console.WriteLine();
Console.WriteLine("SAFETY — paths that try to escape the destination");
var escapes = new (string path, string what)[]
{
    (@"..\escaped.txt",                     "a parent reference"),
    ("../escaped.txt",                      "a parent reference with forward slashes"),
    (@"Projects\..\..\escaped.txt",         "a parent reference buried in the middle"),
    (@"C:\Windows\Temp\escaped.txt",        "an absolute path"),
    (@"\\server\share\escaped.txt",         "a UNC path"),
};
foreach (var (bad, what) in escapes)
{
    var evil = new FileSetOfferMsg
    {
        SetId = "evil",
        Entries = new List<FileSetEntry> { new() { Path = bad, IsDir = false, Size = 4 } },
        TotalBytes = 4
    };
    var er = new FileSetReceiver(evil);
    bool refused = !er.Begin(dest, out var reason);
    er.Dispose();
    Check($"refused {what}", refused, refused ? "" : "IT WAS ACCEPTED — this would write outside the destination");
}

// One good entry alongside one bad one must take the whole set down with it.
var mixed = new FileSetOfferMsg
{
    SetId = "mixed",
    Entries = new List<FileSetEntry>
    {
        new() { Path = "fine.txt", IsDir = false, Size = 4 },
        new() { Path = @"..\alsoescaped.txt", IsDir = false, Size = 4 },
    },
    TotalBytes = 8
};
var mr = new FileSetReceiver(mixed);
bool mixedRefused = !mr.Begin(dest, out _);
mr.Dispose();
Check("one bad path condemns the whole set", mixedRefused,
    mixedRefused ? "" : "the good half was written and the bad half rejected");
Check("nothing escaped the destination", !File.Exists(Path.Combine(root, "escaped.txt"))
                                       && !File.Exists(Path.Combine(root, "alsoescaped.txt")));

// ---- BUGS #8: a link that loses a chunk ------------------------------------------------------
// The fault was never in the tree engine; it was in what happens when the thing carrying the
// chunks cannot take one. Nothing acknowledges, sequences or re-sends a chunk, so a lost one is
// lost for good — and before 2026-09-17 the consequences were silent in two different ways.
//
// A SET never completed: _written never reached the file's size, the file was never closed, and
// Done() was never called, so the transfer hung for ever holding an open handle on a file with a
// zero-filled hole in it. A SINGLE file was worse: the sender announced Ok = true the moment it
// finished READING, so the technician was told a file had arrived whole when it had not.
//
// Both halves are checked here, and each is checked BOTH WAYS -- with the link refusing and with
// it accepting -- because a check that cannot tell those apart is not a check.
Console.WriteLine();
Console.WriteLine("LOSSY LINK — what happens when the link will not take a chunk (BUGS #8)");

// Big enough to span several chunks. The first attempt used an existing test file, which is
// under the 64 KB chunk size -- so the whole transfer was one chunk, there was no second chunk to
// lose, and the check reported a fault that was its own.
var bigPath = Path.Combine(source, "Projects", "big.bin");
var bigHash = expected["Projects/big.bin"];

{
    // A link that refuses every chunk after the first few. The sender must wait rather than skip.
    var lossyPlan = FileSetSender.Plan(new[] { bigPath });
    var lossyOffer = new FileSetOfferMsg { SetId = "lossy", Entries = lossyPlan.Entries, TotalBytes = lossyPlan.TotalBytes };
    var lrx = new FileSetReceiver(lossyOffer);
    var lossyDest = Path.Combine(root, "lossy");
    Directory.CreateDirectory(lossyDest);
    lrx.Begin(lossyDest, out _);

    // Deliver the first chunk, then drop one, then deliver the rest -- which is exactly what an
    // evicting queue used to do. The sender is NOT told, so this reproduces the old behaviour
    // rather than the new one, and what it proves is that the RECEIVER now notices.
    int seen = 0;
    await FileSetSender.SendAsync("lossy", lossyPlan, frame =>
    {
        var (id, off, data) = Bin.ParseFileChunk(frame);
        int h = id.IndexOf('#');
        seen++;
        if (seen == 2) return true;           // the lost chunk: accepted by the link, never delivered
        lrx.Chunk(int.Parse(id[(h + 1)..]), off, data);
        return true;
    }, null, CancellationToken.None);

    bool noticed = lrx.Skipped.Count > 0;
    Check("a lost chunk is NOTICED rather than hanging the transfer", noticed && seen > 2,
          noticed ? lrx.Skipped[0] : $"{seen} chunks offered, nothing reported — this is the hang");
    var doneL = lrx.Done();
    Check("and the transfer reports failure rather than success", !doneL.Ok, doneL.Error ?? "it said Ok");
    Check("and the half-written file is not left behind pretending to be whole",
          !File.Exists(Path.Combine(lossyDest, "big.bin")));
}

{
    // The other way round: the link REFUSES, the sender waits, nothing is lost. This is what the
    // fix actually does, and it is the case that must not look like the one above.
    var okPlan = FileSetSender.Plan(new[] { bigPath });
    var okOffer = new FileSetOfferMsg { SetId = "backoff", Entries = okPlan.Entries, TotalBytes = okPlan.TotalBytes };
    var brx = new FileSetReceiver(okOffer);
    var okDest = Path.Combine(root, "backoff");
    Directory.CreateDirectory(okDest);
    brx.Begin(okDest, out _);

    int refusals = 0, offered = 0;
    await FileSetSender.SendAsync("backoff", okPlan, frame =>
    {
        offered++;
        // Refuse the second chunk twice, then take it. A sender that skipped on refusal would
        // lose it; one that waits delivers everything, in order.
        if (offered == 2 && refusals < 2) { refusals++; offered--; return false; }
        var (id, off, data) = Bin.ParseFileChunk(frame);
        int h = id.IndexOf('#');
        brx.Chunk(int.Parse(id[(h + 1)..]), off, data);
        return true;
    }, null, CancellationToken.None);

    var doneB = brx.Done();
    Check("a refused chunk is waited for, not skipped", refusals == 2 && brx.Skipped.Count == 0,
          $"{refusals} refusals, {brx.Skipped.Count} skipped");
    Check("and the transfer then succeeds", doneB.Ok, doneB.Error ?? "");
    var landedB = Path.Combine(okDest, "big.bin");
    bool identical = File.Exists(landedB)
        && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(landedB))) == bigHash;
    Check("and the file that arrived is identical, byte for byte", identical);
}

summary:
Console.WriteLine();
Console.WriteLine(new string('-', 70));
Console.WriteLine($"{passed} passed, {failed} failed");
try { Directory.Delete(root, true); } catch { }
Console.WriteLine(failed == 0
    ? "the tree engine handles files, folders, empty folders, duplicates and hostile paths."
    : "the tree engine is NOT sound — see the failures above.");
return failed == 0 ? 0 : 1;
