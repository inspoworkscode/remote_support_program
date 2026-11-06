using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>Handles files pushed to the client machine and files pulled from it.</summary>
internal sealed class FileTransfer
{
    private sealed class Incoming { public FileStream Stream = null!; public string Path = ""; public long Size; public long Written; }
    private readonly Dictionary<string, Incoming> _incoming = new();
    private readonly object _lock = new();

    // Not Environment.SpecialFolder.UserProfile: the host runs as SYSTEM, whose profile is
    // C:WindowsSystem32nfigsystemprofile — not where anyone expects a received file.
    public string DefaultDir { get; } = Path.Combine(UserContext.ProfileDirectory, "Downloads");

    // ---- receiving a file onto this machine ----
    public FileDoneMsg? BeginReceive(FileOfferMsg offer)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(offer.TargetDir) ? DefaultDir : offer.TargetDir!;
            Directory.CreateDirectory(dir);
            var name = Path.GetFileName(offer.Name);
            var path = Path.Combine(dir, name);
            int n = 1; var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
            while (File.Exists(path)) path = Path.Combine(dir, $"{stem} ({n++}){ext}");
            var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            lock (_lock) _incoming[offer.FileId] = new Incoming { Stream = fs, Path = path, Size = offer.Size };
            return null;
        }
        catch (Exception ex) { return new FileDoneMsg { FileId = offer.FileId, Ok = false, Error = ex.Message }; }
    }

    public FileDoneMsg? Chunk(string fileId, long offset, ReadOnlyMemory<byte> data)
    {
        Incoming? inc; lock (_lock) _incoming.TryGetValue(fileId, out inc);
        if (inc == null) return null;
        try
        {
            // The same gap check the file-set receiver makes, and for the same reason: chunks
            // travel front to back down one ordered socket, so an offset that does not continue
            // from the last one means a chunk was lost and this file can never be completed.
            // Without it the transfer simply never finishes. See BUGS #8.
            if (offset != inc.Written)
            {
                lock (_lock) _incoming.Remove(fileId);
                try { inc.Stream.Dispose(); File.Delete(inc.Path); } catch { }
                return new FileDoneMsg { FileId = fileId, Ok = false, Error = $"the link lost part of this file (expected the next piece at {inc.Written:N0} bytes, it arrived at {offset:N0})" };
            }
            inc.Stream.Position = offset;
            inc.Stream.Write(data.Span);
            inc.Written += data.Length;
            if (inc.Written >= inc.Size)
            {
                lock (_lock) _incoming.Remove(fileId);
                inc.Stream.Flush(); inc.Stream.Dispose();
                return new FileDoneMsg { FileId = fileId, Ok = true, SavedPath = inc.Path };
            }
            return null;
        }
        catch (Exception ex)
        {
            lock (_lock) _incoming.Remove(fileId);
            try { inc.Stream.Dispose(); File.Delete(inc.Path); } catch { }
            return new FileDoneMsg { FileId = fileId, Ok = false, Error = ex.Message };
        }
    }

    // ---- sending a file from this machine ----
    public async Task SendFileAsync(string path, string fileId, Action<Msg> sendMsg, Func<byte[], bool> sendBin, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { sendMsg(new FileDoneMsg { FileId = fileId, Ok = false, Error = "File not found" }); return; }
            sendMsg(new FileOfferMsg { FileId = fileId, Name = fi.Name, Size = fi.Length });
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buf = new byte[64 * 1024];
            long offset = 0; int read;
            while ((read = await fs.ReadAsync(buf, ct)) > 0)
            {
                // Wait for room rather than losing the chunk. This path used to announce success
                // unconditionally the moment it finished READING the file, which on a link that
                // had dropped a chunk told the technician a file had arrived whole when it had
                // not — the one failure mode worse than no transfer at all. See BUGS #8.
                if (!await FileSetSender.SendChunkAsync(sendBin, Bin.FileChunk(fileId, offset, buf.AsSpan(0, read)), ct))
                {
                    sendMsg(new FileDoneMsg { FileId = fileId, Ok = false, Error = "the link would not carry the whole file" });
                    return;
                }
                offset += read;
                await Task.Delay(1, ct); // yield so the send channel drains
            }
            sendMsg(new FileDoneMsg { FileId = fileId, Ok = true, SavedPath = path });
        }
        catch (Exception ex) { sendMsg(new FileDoneMsg { FileId = fileId, Ok = false, Error = ex.Message }); }
    }

    public FileListMsg List(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path == "\\" || path == "/")
            {
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady)
                    .Select(d => new FileEntry { Name = d.Name, IsDir = true }).ToList();
                return new FileListMsg { Path = "", Entries = drives };
            }
            var dir = new DirectoryInfo(path);
            var entries = new List<FileEntry>();
            foreach (var d in dir.GetDirectories()) { try { entries.Add(new FileEntry { Name = d.Name, IsDir = true, Modified = d.LastWriteTime }); } catch { } }
            foreach (var f in dir.GetFiles()) { try { entries.Add(new FileEntry { Name = f.Name, IsDir = false, Size = f.Length, Modified = f.LastWriteTime }); } catch { } }
            return new FileListMsg { Path = dir.FullName, Entries = entries };
        }
        catch (Exception ex) { return new FileListMsg { Path = path, Error = ex.Message }; }
    }
}
