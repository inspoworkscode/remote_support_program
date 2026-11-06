namespace RemoteSupport.Shared;

/// <summary>
/// Moving whole trees — several files, folders, and folders inside folders — in either direction.
///
/// This lives in Shared and not in the host or the console because both ends do both jobs. The
/// technician sends a tree when you paste or drop onto the session, and receives one when you
/// copy from the customer; the host does exactly the mirror image. Writing it twice would mean
/// two subtly different ideas of what an empty folder, a locked file or a duplicate name does.
///
/// Chunks reuse BinKind.FileChunk with a file id of "&lt;setId&gt;#&lt;entry index&gt;", so nothing
/// about the binary framing had to change to carry this.
/// </summary>
public static class FileSetLimits
{
    /// <summary>A support technician moving more than this has reached for the wrong tool, and a
    /// runaway recursion would otherwise take the session down with it.</summary>
    public const int MaxEntries = 20_000;
    public const long MaxBytes = 5L * 1024 * 1024 * 1024;
    public const int ChunkSize = 64 * 1024;

    /// <summary>
    /// How much a copied selection may be before it stops moving on its own (stage 3.5.1, Clint's
    /// decision of 2026-09-18).
    ///
    /// Copying files puts PATHS on a clipboard, so for a paste to work on the other machine the
    /// bytes have to be fetched before the paste happens — there is nowhere to put the decision
    /// except here, ahead of time. Under this, the fetch is silent and Ctrl+V simply works. Over
    /// it, the far end is told what is on offer and how big, and nothing moves until it is asked
    /// for: one careless Ctrl+C over a folder of videos must not stall the session.
    ///
    /// Fifty megabytes is a starting figure, not a measured one. It is about a minute on a poor
    /// ADSL uplink and unnoticeable on anything better, and it is comfortably larger than every
    /// log, screenshot, installer and document this tool exists to move.
    /// </summary>
    public const long AutoFetchBytes = 50L * 1024 * 1024;

    public static string Human(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.##} GB" :
        b >= 1L << 20 ? $"{b / (double)(1L << 20):0.##} MB" :
        b >= 1L << 10 ? $"{b / (double)(1L << 10):0.##} KB" : $"{b} B";
}

/// <summary>What a selection turned into: the entries to announce, and where each one is read from.</summary>
public sealed class FileSetPlan
{
    public List<FileSetEntry> Entries { get; } = new();
    /// <summary>Absolute source path for each entry, by the same index. Directories included.</summary>
    public List<string> Sources { get; } = new();
    public long TotalBytes { get; set; }
    /// <summary>What could not be read, and why. Never silently dropped.</summary>
    public List<string> Skipped { get; } = new();
    public string Label { get; set; } = "";
    public int FileCount => Entries.Count(e => !e.IsDir);
}

public static class FileSetSender
{
    /// <summary>
    /// Walks a selection into a flat list of entries with paths relative to the root of the drop.
    ///
    /// Reparse points — junctions, symlinks, OneDrive placeholders — are skipped rather than
    /// followed. Following them can loop for ever (a junction pointing at its own parent) or
    /// quietly pull in a whole other drive, and neither is what anyone meant by "copy this folder".
    /// </summary>
    public static FileSetPlan Plan(IEnumerable<string> selection)
    {
        var plan = new FileSetPlan();
        var roots = selection.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Top-level names have to be unique or two folders called "Logs" would merge into one.
        var usedTop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string UniqueTop(string name)
        {
            if (usedTop.Add(name)) return name;
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (int n = 2; ; n++)
            {
                var candidate = $"{stem} ({n}){ext}";
                if (usedTop.Add(candidate)) return candidate;
            }
        }

        foreach (var root in roots)
        {
            try
            {
                if (File.Exists(root))
                {
                    var fi = new FileInfo(root);
                    if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) { plan.Skipped.Add($"{fi.Name} (a shortcut to somewhere else)"); continue; }
                    var name = UniqueTop(fi.Name);
                    plan.Entries.Add(new FileSetEntry { Path = name, IsDir = false, Size = fi.Length });
                    plan.Sources.Add(fi.FullName);
                    plan.TotalBytes += fi.Length;
                }
                else if (Directory.Exists(root))
                {
                    var di = new DirectoryInfo(root);
                    if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) { plan.Skipped.Add($"{di.Name} (a junction to somewhere else)"); continue; }
                    var name = UniqueTop(di.Name);
                    plan.Entries.Add(new FileSetEntry { Path = name, IsDir = true });
                    plan.Sources.Add(di.FullName);
                    Walk(di, name, plan);
                }
                else plan.Skipped.Add($"{Path.GetFileName(root)} (not found)");
            }
            catch (Exception ex) { plan.Skipped.Add($"{Path.GetFileName(root)} ({ex.GetType().Name})"); }

            if (plan.Entries.Count >= FileSetLimits.MaxEntries) break;
        }

        plan.Label = roots.Count == 1
            ? Path.GetFileName(roots[0].TrimEnd('\\', '/'))
            : $"{roots.Count} items";
        return plan;
    }

    private static void Walk(DirectoryInfo dir, string rel, FileSetPlan plan)
    {
        if (plan.Entries.Count >= FileSetLimits.MaxEntries) return;

        FileInfo[] files;
        DirectoryInfo[] subs;
        // A folder we are not allowed to read is a fact to report, not a reason to abandon the
        // whole transfer. The rest of the tree is still worth copying.
        try { files = dir.GetFiles(); } catch (Exception ex) { plan.Skipped.Add($"{rel} ({ex.GetType().Name})"); return; }
        try { subs = dir.GetDirectories(); } catch { subs = Array.Empty<DirectoryInfo>(); }

        foreach (var f in files)
        {
            if (plan.Entries.Count >= FileSetLimits.MaxEntries) return;
            try
            {
                if (f.Attributes.HasFlag(FileAttributes.ReparsePoint)) { plan.Skipped.Add($"{rel}/{f.Name} (a shortcut)"); continue; }
                plan.Entries.Add(new FileSetEntry { Path = $"{rel}/{f.Name}", IsDir = false, Size = f.Length });
                plan.Sources.Add(f.FullName);
                plan.TotalBytes += f.Length;
            }
            catch (Exception ex) { plan.Skipped.Add($"{rel}/{f.Name} ({ex.GetType().Name})"); }
        }

        foreach (var d in subs)
        {
            if (plan.Entries.Count >= FileSetLimits.MaxEntries) return;
            try
            {
                if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) { plan.Skipped.Add($"{rel}/{d.Name} (a junction)"); continue; }
                // Empty folders are announced in their own right. They carry meaning, and a
                // receiver that only created folders on the way to a file would lose them.
                plan.Entries.Add(new FileSetEntry { Path = $"{rel}/{d.Name}", IsDir = true });
                plan.Sources.Add(d.FullName);
                Walk(d, $"{rel}/{d.Name}", plan);
            }
            catch (Exception ex) { plan.Skipped.Add($"{rel}/{d.Name} ({ex.GetType().Name})"); }
        }
    }

    /// <summary>
    /// How long a chunk may wait for room on the link before the transfer gives up on that file.
    ///
    /// Generous on purpose. The only thing that makes a chunk wait is a link that is genuinely
    /// behind, and a transfer that survives a bad minute is worth far more than one that fails
    /// fast; the deadline is here so a dead socket ends the transfer rather than hanging it for
    /// ever, not to enforce any kind of throughput.
    /// </summary>
    public static readonly TimeSpan SendDeadline = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Streams every file in the plan. The offer must already have been sent and accepted.
    ///
    /// <paramref name="sendBin"/> returns FALSE when the link has no room, and this waits rather
    /// than continuing. That is the whole of BUGS #8: a chunk that is dropped is not recoverable
    /// by anything downstream, because nothing acknowledges, re-requests or re-sends one. The
    /// receiver's gap check will now notice and say so, but noticing is the second line of
    /// defence — not sending it into a full queue is the first.
    /// </summary>
    public static async Task SendAsync(string setId, FileSetPlan plan, Func<byte[], bool> sendBin,
                                       Action<long, int, string>? progress, CancellationToken ct)
    {
        var buf = new byte[FileSetLimits.ChunkSize];
        long sent = 0;
        int filesDone = 0;
        for (int i = 0; i < plan.Entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = plan.Entries[i];
            if (e.IsDir) continue;

            // A zero-byte file gets no chunks at all, so the receiver creates it up front. Do not
            // send an empty frame for it: the receiver would have no way to tell that from a
            // stream that simply has not started yet.
            if (e.Size == 0) { filesDone++; progress?.Invoke(sent, filesDone, e.Path); continue; }

            try
            {
                await using var fs = new FileStream(plan.Sources[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long offset = 0;
                int read;
                while ((read = await fs.ReadAsync(buf, ct)) > 0)
                {
                    if (!await SendChunkAsync(sendBin, Bin.FileChunk($"{setId}#{i}", offset, buf.AsSpan(0, read)), ct))
                        throw new IOException($"the link would not take the file after {SendDeadline.TotalSeconds:0} seconds");
                    offset += read;
                    sent += read;
                    // Yield so the send channel drains rather than being buried; the single-file
                    // path does the same.
                    if ((offset / FileSetLimits.ChunkSize) % 16 == 0) await Task.Delay(1, ct);
                    progress?.Invoke(sent, filesDone, e.Path);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Locked by another program is the common one. Say which file, carry on with the
                // rest, and let the receiver's Skipped list carry it to the user.
                plan.Skipped.Add($"{e.Path} ({ex.GetType().Name}: {ex.Message})");
            }
            filesDone++;
            progress?.Invoke(sent, filesDone, e.Path);
        }
    }

    /// <summary>
    /// Offers one chunk, waiting for room rather than losing it. False means the link never took
    /// it within <see cref="SendDeadline"/>, which the caller must treat as a failed file — never
    /// as a chunk to skip over, because a gap is exactly what cannot be recovered from.
    ///
    /// The backoff is deliberately coarse. A queue that is full is waiting on a socket, and a
    /// socket that is behind is measured in hundreds of milliseconds, not microseconds; spinning
    /// faster would burn a core on a machine that is already struggling to send.
    /// </summary>
    public static async Task<bool> SendChunkAsync(Func<byte[], bool> sendBin, byte[] chunk, CancellationToken ct)
    {
        if (sendBin(chunk)) return true;
        var until = DateTime.UtcNow + SendDeadline;
        int wait = 5;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(wait, ct);
            if (sendBin(chunk)) return true;
            if (wait < 200) wait = Math.Min(200, wait * 2);
        }
        return false;
    }
}

/// <summary>Rebuilds a tree on this machine from the entries and chunks the other end sends.</summary>
public sealed class FileSetReceiver : IDisposable
{
    private readonly Dictionary<int, FileStream> _open = new();
    private readonly Dictionary<int, long> _written = new();
    // Files given up on, so a later chunk for one is ignored rather than reopening it and
    // counting it done a second time.
    private readonly HashSet<int> _failed = new();
    private readonly object _lock = new();
    private readonly FileSetOfferMsg _offer;
    private string[] _absolute = Array.Empty<string>();
    private int _filesTotal, _filesDone;

    public string SetId => _offer.SetId;
    public string TargetDir { get; private set; } = "";
    public List<string> SavedPaths { get; } = new();
    public List<string> Skipped { get; } = new();
    public long TotalBytes => _offer.TotalBytes;
    public long BytesWritten { get; private set; }
    public int FilesTotal => _filesTotal;
    public int FilesDone => _filesDone;

    public FileSetReceiver(FileSetOfferMsg offer) => _offer = offer;

    /// <summary>
    /// Refuses anything that is not a plain relative path underneath the target.
    ///
    /// This is the security check, and it is not theoretical: on the customer's machine the
    /// receiving process runs as SYSTEM, so an entry path of "..\..\Windows\System32\..." would
    /// otherwise be written with full machine rights. Rooted paths, drive letters, UNC paths and
    /// any ".." segment are rejected, and then the combined path is canonicalised and checked to
    /// be under the target anyway — belt and braces, because the first check is easy to fool.
    /// </summary>
    /// <summary>
    /// Is this a plain relative path and nothing else?
    ///
    /// This runs on the RAW entry path, before anything else touches it. That ordering is the
    /// whole point and was a real bug: renaming for collisions first turned "..\evil.txt" into a
    /// legal-looking file name, which then sailed through every check below it. The traversal was
    /// sanitised into an odd file rather than refused, and an absolute path like
    /// "C:\Windows\Temp\x" was accepted the same way, because Directory.Exists("C:") is true and
    /// the collision logic duly renamed the drive letter into something harmless-looking.
    /// Validate first, rename second.
    /// </summary>
    private static bool IsSafeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return false;
        if (relative.Contains('\0')) return false;
        var norm = relative.Replace('\\', '/');
        // A drive letter, a leading slash or a UNC prefix all mean "not relative".
        if (norm.StartsWith('/') || norm.StartsWith("//")) return false;
        if (Path.IsPathRooted(norm) || Path.IsPathRooted(relative)) return false;
        if (norm.Length >= 2 && norm[1] == ':') return false;
        foreach (var seg in norm.Split('/'))
        {
            if (seg.Length == 0) return false;
            if (seg == "." || seg == "..") return false;
            if (seg.Trim() != seg && seg.Trim().Length == 0) return false;
            if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Belt and braces after IsSafeRelative: canonicalise the combined path and confirm it really
    /// does sit under the target. The first check is the one that refuses; this one catches
    /// anything the operating system resolves differently from how it reads.
    /// </summary>
    private static string? SafeCombine(string targetDir, string relative)
    {
        if (!IsSafeRelative(relative)) return null;
        var norm = relative.Replace('/', Path.DirectorySeparatorChar);
        string full;
        try { full = Path.GetFullPath(Path.Combine(targetDir, norm)); }
        catch { return null; }
        var root = Path.GetFullPath(targetDir);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>
    /// Creates the folders and the empty files, and works out where everything goes.
    /// Returns false with a reason rather than throwing, so the far end can be told why.
    /// </summary>
    public bool Begin(string defaultDir, out string error)
    {
        error = "";
        if (_offer.Entries.Count == 0) { error = "there was nothing in the transfer"; return false; }
        if (_offer.Entries.Count > FileSetLimits.MaxEntries) { error = $"too many items ({_offer.Entries.Count:N0}); the limit is {FileSetLimits.MaxEntries:N0}"; return false; }
        if (_offer.TotalBytes > FileSetLimits.MaxBytes) { error = $"too big ({FileSetLimits.Human(_offer.TotalBytes)}); the limit is {FileSetLimits.Human(FileSetLimits.MaxBytes)}"; return false; }

        // A sentinel that got this far was never resolved, which means the sending end knows a
        // name this machine does not — a newer console against an older client, which 4.6
        // auto-update exists to stop happening and cannot stop having already happened.
        //
        // Refusing is not pedantry. .NET treats "%PASTE%" as a RELATIVE path, so without this the
        // transfer succeeds and writes into a literal folder called %PASTE% under whatever the
        // receiver's working directory happens to be — on the host that is its install directory,
        // under Program Files. The files arrive, the technician is told they arrived, and they are
        // somewhere nobody will ever look. That is docs/BUGS.md #6 exactly, which has been paid
        // for once already.
        if (RemotePaths.IsSentinel(_offer.TargetDir))
        {
            error = $"this machine does not know where {_offer.TargetDir} is — it is running an older client and needs updating";
            return false;
        }

        var dir = string.IsNullOrWhiteSpace(_offer.TargetDir) ? defaultDir : _offer.TargetDir!;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { error = $"could not use {dir}: {ex.Message}"; return false; }
        TargetDir = dir;

        // VALIDATE FIRST, before anything rewrites these paths. The collision rename below turns
        // a path into a new one, and a ".." or a drive letter fed through it comes out looking
        // like an ordinary file name — which is exactly how four hostile paths were accepted
        // until filesetcheck caught it. One bad entry condemns the whole set: writing the part
        // that looked reasonable is not a safe half-measure.
        foreach (var e in _offer.Entries)
        {
            if (IsSafeRelative(e.Path)) continue;
            error = $"the transfer contained an unsafe path: {e.Path}";
            return false;
        }

        // Do not overwrite what is already there. Rename each top-level item that collides, and
        // carry the rename through every entry underneath it, or a folder's children would go to
        // the original folder while the folder itself went somewhere else.
        var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _offer.Entries)
        {
            var top = e.Path.Replace('\\', '/').Split('/')[0];
            if (rename.ContainsKey(top)) continue;
            var candidate = top;
            var stem = Path.GetFileNameWithoutExtension(top);
            var ext = Path.GetExtension(top);
            for (int n = 2; File.Exists(Path.Combine(dir, candidate)) || Directory.Exists(Path.Combine(dir, candidate)); n++)
                candidate = $"{stem} ({n}){ext}";
            rename[top] = candidate;
        }

        _absolute = new string[_offer.Entries.Count];
        for (int i = 0; i < _offer.Entries.Count; i++)
        {
            var e = _offer.Entries[i];
            var parts = e.Path.Replace('\\', '/').Split('/');
            if (parts.Length > 0 && rename.TryGetValue(parts[0], out var newTop)) parts[0] = newTop;
            var abs = SafeCombine(dir, string.Join('/', parts));
            if (abs == null)
            {
                // One bad path means the sender is not one of ours, or is broken. Refuse the whole
                // set rather than writing the part of it that looked reasonable.
                error = $"the transfer contained an unsafe path: {e.Path}";
                return false;
            }
            _absolute[i] = abs;
            if (!e.IsDir) _filesTotal++;
        }

        try
        {
            for (int i = 0; i < _offer.Entries.Count; i++)
            {
                var e = _offer.Entries[i];
                if (e.IsDir) { Directory.CreateDirectory(_absolute[i]); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(_absolute[i])!);
                // No chunks will ever arrive for an empty file, so create it now or it would be
                // missing from an otherwise complete transfer.
                if (e.Size == 0) { File.WriteAllBytes(_absolute[i], Array.Empty<byte>()); _filesDone++; }
            }
        }
        catch (Exception ex) { error = $"could not create the folders: {ex.Message}"; return false; }

        foreach (var top in rename.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            SavedPaths.Add(Path.Combine(dir, top));
        return true;
    }

    /// <summary>Writes one chunk. Returns true when every file in the set is complete.</summary>
    public bool Chunk(int index, long offset, ReadOnlyMemory<byte> data)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _offer.Entries.Count) return false;
            var e = _offer.Entries[index];
            if (e.IsDir) return false;
            if (_failed.Contains(index)) return _filesDone >= _filesTotal;
            try
            {
                if (!_open.TryGetValue(index, out var fs))
                {
                    fs = new FileStream(_absolute[index], FileMode.Create, FileAccess.Write, FileShare.None);
                    _open[index] = fs;
                    _written[index] = 0;
                }

                // A chunk that does not start where the last one ended means one went missing:
                // the sender streams a file front to back down a single ordered socket, so there
                // is no legitimate way to arrive out of order. Nothing acknowledges or re-sends a
                // chunk, so this file cannot be completed — and saying so at once is the entire
                // difference between a transfer that fails and one that HANGS. Before this check,
                // a lost chunk meant _written never reached the size, the file was never closed,
                // Done() was never called, and the set sat there for ever holding a lock on a
                // file with a zero-filled hole in it. See BUGS #8.
                if (offset != _written[index])
                {
                    Skipped.Add($"{e.Path} (the link lost part of this file — expected the next piece at " +
                                $"{_written[index]:N0} bytes and it arrived at {offset:N0})");
                    _failed.Add(index);
                    try { fs.Dispose(); } catch { }
                    _open.Remove(index);
                    try { File.Delete(_absolute[index]); } catch { }
                    _filesDone++;
                    return _filesDone >= _filesTotal;
                }

                fs.Position = offset;
                fs.Write(data.Span);
                _written[index] += data.Length;
                BytesWritten += data.Length;

                if (_written[index] >= e.Size)
                {
                    fs.Flush();
                    fs.Dispose();
                    _open.Remove(index);
                    _filesDone++;
                }
            }
            catch (Exception ex)
            {
                Skipped.Add($"{e.Path} ({ex.GetType().Name}: {ex.Message})");
                try { if (_open.TryGetValue(index, out var bad)) { bad.Dispose(); _open.Remove(index); } } catch { }
                _filesDone++;
            }
            return _filesDone >= _filesTotal;
        }
    }

    public FileSetDoneMsg Done()
    {
        Dispose();
        var msg = new FileSetDoneMsg
        {
            SetId = SetId,
            Ok = Skipped.Count == 0,
            SavedDir = TargetDir,
            Error = Skipped.Count == 0 ? null : $"{Skipped.Count} item(s) could not be copied"
        };
        msg.SavedPaths.AddRange(SavedPaths);
        msg.Skipped.AddRange(Skipped);
        return msg;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var fs in _open.Values) { try { fs.Dispose(); } catch { } }
            _open.Clear();
        }
    }
}
