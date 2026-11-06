using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>
/// Copying files straight from one machine to another the technician has open (2026-10-15; docs/NEXT-SESSION-HOST2HOST.md,
/// HANDOVER §144). This host is one END of a bridge: it SENDS what the person here copied, or it RECEIVES into its paste
/// staging and puts the files on the person's clipboard as them (§39) when the last byte lands. The frames go through the
/// hub, sealed under a key the console handed to each end inside its own session (<see cref="BridgeSeal"/>).
///
/// <para>The rules that keep it safe, all enforced HERE as well as at the hub:</para>
/// <list type="bullet">
/// <item>A key that arrived CLEAR is refused: in a sealed session the connection already drops clear content, and in a clear
/// one (an older console in it) nothing bridges at all — the server could have sent it.</item>
/// <item>The receiving end chooses where the files go — always its own paste staging, never a folder the other end names.</item>
/// <item>The sender stays within <see cref="BridgeSeal.Window"/> of what the receiver has acknowledged, so a slow receiving
/// line never makes the hub hold this socket — which would freeze this machine's picture.</item>
/// </list>
/// </summary>
public sealed partial class HostEngine
{
    private sealed class Bridge
    {
        public required BridgeSeal Seal;
        public required bool Sending;
        public List<string> Paths = new();
        public string Other = "";
        /// <summary>What was copied, as the sending end named it — for the line on the ticket.</summary>
        public string Label = "";
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<FileSetAcceptMsg> Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<BridgeEndMsg> Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Acked;
        public long LastActivityMs = Environment.TickCount64;
        // the receiving end
        public FileSetReceiver? Rx;
        public long AckedSent, LastProgressMs;
        public bool Finished;
    }

    private readonly Dictionary<string, Bridge> _bridges = new();
    /// <summary>Frames for a bridge whose key has not reached this end yet: the two keys travel down two different
    /// sessions, so the other end's first word can beat ours. Kept briefly, a few at most.</summary>
    private readonly Dictionary<string, (long At, List<byte[]> Frames)> _bridgeEarly = new();
    private readonly object _bridgeLock = new();

    private void OnBridgeKey(BridgeKeyMsg k)
    {
        // Only a key that came SEALED from a viewer of this session. While sealing, the connection drops clear content
        // before it reaches here; while clear, nothing here can tell the console from the server, so nothing bridges.
        if (_seal is not { Sealing: true, Keyed: true })
        {
            HostTrace.Write($"bridge {k.BridgeId}: refused — this session is not end-to-end encrypted, so the key could have come from anybody");
            _ws?.Send(new BridgeEndMsg { BridgeId = k.BridgeId, Why = "the session is not end-to-end encrypted" });
            return;
        }
        byte[] key;
        try { key = Convert.FromBase64String(k.Key); } catch (FormatException) { key = Array.Empty<byte>(); }
        if (!BridgeSeal.IsId(k.BridgeId) || key.Length != 32 || (k.Send && k.Paths.Count == 0))
        {
            HostTrace.Write($"bridge: refused a key that was not one (id ok: {BridgeSeal.IsId(k.BridgeId)}, {key.Length} bytes, {k.Paths.Count} path(s))");
            if (BridgeSeal.IsId(k.BridgeId)) _ws?.Send(new BridgeEndMsg { BridgeId = k.BridgeId, Why = "the copy could not be started" });
            return;
        }
        var b = new Bridge { Seal = new BridgeSeal(k.BridgeId, key, k.Send), Sending = k.Send, Paths = k.Paths, Other = k.Other };
        List<byte[]>? early = null;
        lock (_bridgeLock)
        {
            if (_bridges.ContainsKey(k.BridgeId)) { HostTrace.Write($"bridge {k.BridgeId}: a second key for it — ignored"); return; }
            _bridges[k.BridgeId] = b;
            if (_bridgeEarly.Remove(k.BridgeId, out var e)) early = e.Frames;
        }
        HostTrace.Write($"bridge {k.BridgeId}: {(k.Send ? $"sending {k.Paths.Count} item(s) straight to" : "receiving straight from")} {k.Other}");

        if (b.Sending) _ = SendOverBridgeAsync(b);
        else
        {
            // Their clipboard is emptied NOW: a Ctrl+V before the files land then pastes nothing, never this machine's own
            // old files looking like the copy arrived wrong (the same rule as §54, on this machine).
            if (!_clipboard.SetText("", out var why)) HostTrace.Write($"bridge {k.BridgeId}: could not empty the clipboard first — {why}");
            else _lastMirroredText = "";
            SendInBridge(b, new BridgeReadyMsg());
            _ = WatchBridgeStallAsync(b);
        }
        if (early != null) foreach (var f in early) OnBridgeFrame(f);
    }

    /// <summary>A bridge frame from the hub: opened under that bridge's key, or kept a moment if the key is not here yet.</summary>
    private void OnBridgeFrame(ReadOnlyMemory<byte> frame)
    {
        var id = BridgeSeal.IdOf(frame.Span);
        if (id == null) return;
        Bridge? b;
        lock (_bridgeLock)
        {
            if (!_bridges.TryGetValue(id, out b))
            {
                long now = Environment.TickCount64;
                foreach (var stale in _bridgeEarly.Where(kv => now - kv.Value.At > 15000).Select(kv => kv.Key).ToList()) _bridgeEarly.Remove(stale);
                if (!_bridgeEarly.TryGetValue(id, out var e))
                {
                    if (_bridgeEarly.Count >= 4) return;
                    e = (now, new List<byte[]>());
                    _bridgeEarly[id] = e;
                }
                if (e.Frames.Count < 16) e.Frames.Add(frame.ToArray());
                return;
            }
        }
        var (m, chunk) = b.Seal.Open(frame.Span);
        if (m == null && chunk == null)
        {
            if (b.Seal.Refused == 1) HostTrace.Write($"bridge {id}: refused a frame that did not open under this copy's key");
            return;
        }
        b.LastActivityMs = Environment.TickCount64;
        if (chunk != null) { if (!b.Sending) BridgeChunk(b, chunk); return; }
        switch (m)
        {
            case BridgeReadyMsg when b.Sending: b.Ready.TrySetResult(); break;
            case FileSetAcceptMsg acc when b.Sending: b.Accepted.TrySetResult(acc); break;
            case BridgeAckMsg ack when b.Sending: Interlocked.Exchange(ref b.Acked, Math.Max(Interlocked.Read(ref b.Acked), ack.Bytes)); break;
            case FileSetOfferMsg offer when !b.Sending: BridgeOffer(b, offer); break;
        }
    }

    /// <summary>A message inside the bridge. Waits for room rather than losing it, as a file chunk does — almost always
    /// there is room at once.</summary>
    private void SendInBridge(Bridge b, Msg m)
    {
        bool sent;
        try
        {
            var t = FileSetSender.SendChunkAsync(f => _ws?.SendBinary(f) ?? false, b.Seal.SealMessage(m), b.Stop.Token);
            sent = t.Wait(TimeSpan.FromSeconds(95)) && t.Result;
        }
        catch { sent = false; }
        if (!sent) HostTrace.Write($"bridge {b.Seal.BridgeId}: could not send {m.GetType().Name}");
    }

    /// <summary>The hub says it is over: refused, finished, cancelled, or a session or the technician gone.</summary>
    private void OnBridgeEnd(BridgeEndMsg e)
    {
        Bridge? b; lock (_bridgeLock) _bridges.TryGetValue(e.BridgeId, out b);
        if (b == null) return;
        b.Ended.TrySetResult(e);
        if (e.Ok) return;
        try { b.Stop.Cancel(); } catch { }
        if (!b.Sending) AbandonReceive(b, string.IsNullOrWhiteSpace(e.Why) ? "the copy was stopped" : e.Why);
    }

    private void EndAllBridges(string why)
    {
        List<Bridge> all; lock (_bridgeLock) { all = _bridges.Values.ToList(); }
        foreach (var b in all) OnBridgeEnd(new BridgeEndMsg { BridgeId = b.Seal.BridgeId, Why = why });
    }

    private void Forget(Bridge b)
    {
        lock (_bridgeLock) _bridges.Remove(b.Seal.BridgeId);
        try { b.Seal.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------ sending

    private async Task SendOverBridgeAsync(Bridge b)
    {
        var id = b.Seal.BridgeId;
        string label = "";
        int files = 0; long bytes = 0;
        string? error = null;
        try
        {
            // The receiving end says it holds the key (and has emptied its clipboard) before anything is offered.
            if (await Task.WhenAny(b.Ready.Task, b.Ended.Task, Task.Delay(30000)) != b.Ready.Task)
                throw new IOException(b.Ended.Task.IsCompleted ? Why(b.Ended.Task.Result) : "the other computer did not answer");

            var plan = FileSetSender.Plan(HostPaths.ResolveAll(b.Paths));
            label = plan.Label; files = plan.FileCount; bytes = plan.TotalBytes;
            if (plan.Entries.Count == 0) throw new IOException("nothing could be read from this computer");
            SendInBridge(b, new FileSetOfferMsg { SetId = id, Entries = plan.Entries, TotalBytes = plan.TotalBytes, Label = plan.Label });
            HostTrace.Write($"bridge {id}: offered {plan.Entries.Count} entries, {FileSetLimits.Human(plan.TotalBytes)}");

            if (await Task.WhenAny(b.Accepted.Task, b.Ended.Task, Task.Delay(30000)) != b.Accepted.Task)
                throw new IOException(b.Ended.Task.IsCompleted ? Why(b.Ended.Task.Result) : "the other computer did not take the files");
            var acc = b.Accepted.Task.Result;
            if (!acc.Accept) throw new IOException(acc.Error ?? "the other computer would not take the files");

            long sent = 0;
            await FileSetSender.SendAsync(id, plan, chunk =>
            {
                // Within the window of what the other end has written, or not at all: SendChunkAsync waits and asks again.
                long payload = chunk.Length - 2 - chunk[1] - 8;
                if (sent - Interlocked.Read(ref b.Acked) > BridgeSeal.Window) return false;
                if (_ws?.SendBinary(b.Seal.SealChunk(chunk)) != true) return false;
                sent += payload;
                b.LastActivityMs = Environment.TickCount64;
                return true;
            }, null, b.Stop.Token);
            if (plan.Skipped.Count > 0) error = $"{plan.Skipped.Count} item(s) could not be read";
            HostTrace.Write($"bridge {id}: streamed {FileSetLimits.Human(sent)}; waiting for the other computer to finish");

            // Reported when the other end says it landed — a line saying they were copied when they did not arrive is worse
            // than none.
            if (await Task.WhenAny(b.Ended.Task, Task.Delay(TimeSpan.FromMinutes(2))) != b.Ended.Task)
                error ??= "the other computer never said the files had arrived";
            else if (!b.Ended.Task.Result.Ok) error = Why(b.Ended.Task.Result);
        }
        catch (OperationCanceledException) { error = b.Ended.Task.IsCompleted ? Why(b.Ended.Task.Result) : "the copy was stopped"; }
        catch (Exception ex) { error = ex.Message; }

        if (error != null && !b.Ended.Task.IsCompleted)
            _ws?.Send(new BridgeEndMsg { BridgeId = id, Why = error });
        HostTrace.Write($"bridge {id}: {(error == null ? "done" : "FAILED — " + error)}");
        ReportFileActivity(true, label.Length > 0 ? label : string.Join(", ", b.Paths.Take(3).Select(Path.GetFileName)),
                           files, error == null ? bytes : 0, error, id);
        Forget(b);
    }

    private static string Why(BridgeEndMsg e) => string.IsNullOrWhiteSpace(e.Why) ? "the copy was stopped" : e.Why;

    // ------------------------------------------------------------------ receiving

    private void BridgeOffer(Bridge b, FileSetOfferMsg offer)
    {
        var id = b.Seal.BridgeId;
        if (b.Rx != null) return;
        b.Label = offer.Label;
        // THIS end chooses where the files go: always a staging folder of its own under the person's temp, whatever the
        // other end said — it is another customer's machine, and must never name a folder on this one.
        offer.SetId = id;
        offer.TargetDir = HostPaths.Resolve(RemotePaths.PasteStaging);
        var rx = new FileSetReceiver(offer);
        if (!rx.Begin(_files.DefaultDir, out var why))
        {
            rx.Dispose();
            HostTrace.Write($"bridge {id}: refused the files — {why}");
            SendInBridge(b, new FileSetAcceptMsg { SetId = id, Accept = false, Error = why });
            FinishReceive(b, false, why, 0, 0);
            return;
        }
        b.Rx = rx;
        SendInBridge(b, new FileSetAcceptMsg { SetId = id, Accept = true });
        HostTrace.Write($"bridge {id}: accepting {offer.Entries.Count} entries, {FileSetLimits.Human(offer.TotalBytes)} into {rx.TargetDir}");
        // A set of nothing but folders and empty files is already complete.
        if (rx.FilesDone >= rx.FilesTotal) LandedOverBridge(b);
        else Progress(b, force: true);
    }

    private void BridgeChunk(Bridge b, byte[] chunk)
    {
        var rx = b.Rx;
        if (rx == null || b.Finished) return;
        var (fileId, off, data) = Bin.ParseFileChunk(chunk);
        int hash = fileId.IndexOf('#');
        if (hash <= 0 || fileId[..hash] != b.Seal.BridgeId || !int.TryParse(fileId[(hash + 1)..], out var index)) return;
        bool complete = rx.Chunk(index, off, data);
        if (rx.BytesWritten - b.AckedSent >= BridgeSeal.AckEvery || complete)
        {
            b.AckedSent = rx.BytesWritten;
            SendInBridge(b, new BridgeAckMsg { Bytes = rx.BytesWritten });
        }
        if (complete) LandedOverBridge(b);
        else Progress(b, force: false);
    }

    private void Progress(Bridge b, bool force)
    {
        var rx = b.Rx;
        if (rx == null) return;
        long now = Environment.TickCount64;
        if (!force && now - b.LastProgressMs < 400) return;
        b.LastProgressMs = now;
        // To this session's own console, sealed with everything else in it.
        _ws?.Send(new BridgeProgressMsg { BridgeId = b.Seal.BridgeId, BytesDone = rx.BytesWritten, TotalBytes = rx.TotalBytes, FilesDone = rx.FilesDone, FilesTotal = rx.FilesTotal });
    }

    /// <summary>The last byte is here: on the person's clipboard AS THEM (Explorer will not paste from SYSTEM's, §39), then
    /// everybody is told.</summary>
    private void LandedOverBridge(Bridge b)
    {
        if (b.Finished) return;
        b.Finished = true;
        var rx = b.Rx!;
        var done = rx.Done();
        Progress(b, force: true);
        // Off the receive loop: it starts a process as the person and waits for it.
        _ = Task.Run(() =>
        {
            string? problem = done.Skipped.Count > 0 ? $"{done.Skipped.Count} item(s) could not be written" : null;
            if (done.SavedPaths.Count > 0)
            {
                bool ok = _clipboard.SetFilesAsUser(done.SavedPaths, out var why);
                HostTrace.Write(ok ? $"bridge {b.Seal.BridgeId}: {done.SavedPaths.Count} item(s) on the person's clipboard"
                                   : $"bridge {b.Seal.BridgeId}: FAILED to put them on the clipboard — {why}");
                if (!ok) problem = "the files arrived but could not be put on the clipboard: " + why;
            }
            FinishReceive(b, problem == null, problem, rx.FilesDone, rx.TotalBytes);
        });
    }

    private void AbandonReceive(Bridge b, string why)
    {
        if (b.Finished) return;
        b.Finished = true;
        var rx = b.Rx;
        if (rx != null)
        {
            rx.Dispose();
            // Half a copy is not left lying in their temp: the folder is this copy's own.
            try { if (Directory.Exists(rx.TargetDir)) Directory.Delete(rx.TargetDir, recursive: true); } catch { }
        }
        HostTrace.Write($"bridge {b.Seal.BridgeId}: stopped — {why}{(rx != null ? "; the half-written copy was removed" : "")}");
        if (rx != null) ReportFileActivity(false, b.Label, rx.FilesDone, 0, why, b.Seal.BridgeId);
        Forget(b);
    }

    private void FinishReceive(Bridge b, bool ok, string? why, int files, long bytes)
    {
        b.Finished = true;
        // Clear, to the hub: it ends the bridge for both ends and both of the technician's windows.
        _ws?.Send(new BridgeEndMsg { BridgeId = b.Seal.BridgeId, Ok = ok, Why = why ?? "", Files = files, Bytes = bytes });
        ReportFileActivity(false, b.Label, files, ok ? bytes : 0, why, b.Seal.BridgeId);
        Forget(b);
    }

    /// <summary>Nothing moving for <see cref="BridgeSeal.StallMs"/>: a transfer that stalls must not leave the person's
    /// clipboard empty and a half-copy in their temp for ever.</summary>
    private async Task WatchBridgeStallAsync(Bridge b)
    {
        try
        {
            while (!b.Finished)
            {
                await Task.Delay(2000, b.Stop.Token);
                if (Environment.TickCount64 - b.LastActivityMs < BridgeSeal.StallMs) continue;
                if (b.Finished) return;
                _ws?.Send(new BridgeEndMsg { BridgeId = b.Seal.BridgeId, Why = "nothing arrived from the other computer for 30 seconds" });
                AbandonReceive(b, "nothing arrived from the other computer for 30 seconds");
                return;
            }
        }
        catch (OperationCanceledException) { }
    }
}
