using System.Security.Cryptography;
using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

/// <summary>
/// COPY ON ONE MACHINE, PASTE ON THE OTHER — STRAIGHT BETWEEN THEM (2026-10-15; docs/NEXT-SESSION-HOST2HOST.md, HANDOVER §144).
///
/// Clint: "build the copy and paste to work directly between both machines that are open instead so that it doesnt go
/// through the technician PC". The gesture is unchanged — Ctrl+C on A, click into B's window, Ctrl+V on B — but with two
/// windows open, A's copied files are no longer fetched down to this PC. A's status bar says where they can go; clicking
/// into B asks the server to join the two sessions for that one copy, and A's machine sends them to B's through it.
///
/// <list type="bullet">
/// <item><b>Only when the technician clicks into B</b>: a copy reaches a customer only through the window in front (§26) —
/// customer A's files are never put on customer B's disk before anybody chose B.</item>
/// <item><b>The key</b> is made here, for this copy only, and given to each machine inside its own sealed session
/// (<see cref="SessionLink.SendSealed"/>) — the server routes the files and can read none of them.</item>
/// <item><b>Clint's answers</b>: not to this PC as well (1 — "bring them to this PC" is a click on A's status bar); any two
/// of the company's machines (2); the customers are told "another computer" (3); the 50 MB rule kept (4); the free version
/// too (5); an older app or a refusal goes through this PC by itself, a copy that fails half-way OFFERS it (6).</item>
/// </list>
/// </summary>
public sealed partial class SessionForm
{
    /// <summary>Files this window's customer copied, waiting to go straight to the other window's machine.</summary>
    private sealed class CopiedHere
    {
        public required List<string> Paths;
        public required long Bytes;
        /// <summary>This PC's clipboard at the moment of the copy (emptied by ForgetMyClipboardForTheirs). Anything copied
        /// since — here, or on either machine — is newer, and the newest copy is what a paste means.</summary>
        public required uint Seq;
        public readonly HashSet<string> SentTo = new();
        public string Describe => Paths.Count == 1 ? "1 item" : $"{Paths.Count} items";
    }

    private CopiedHere? _copiedHere;
    private bool _hostCanBridge;
    /// <summary>The copy THIS window's machine is sending straight across, so its own end is reported here.</summary>
    private string? _bridgeOutId;
    private string _bridgeOutTo = "";

    /// <summary>A copy coming straight INTO this window's machine.</summary>
    private sealed class BridgeIn
    {
        public required string Id;
        public required SessionForm From;
        public required CopiedHere Copy;
        public required byte[] Key;
        public bool KeysSent;
    }

    /// <summary>How long the server has to say yes before the copy goes through this PC instead — an older server does not
    /// know the ask at all, and says nothing.</summary>
    private const int BridgeAskTimeoutMs = 10000;
    private BridgeIn? _bridgeIn;
    /// <summary>Offers on the status bar: a big copy to send straight here, or one that failed to send through the PC.</summary>
    private (SessionForm From, CopiedHere Copy)? _pendingBridgeFrom, _pendingViaPcFrom;

    /// <summary>This window can take part: its machine's app can, and its session is keyed end to end.</summary>
    internal bool CanBridge => _hostCanBridge && _link.Keyed && !_dead && !_session.ViewOnly;

    private SessionForm? OtherWindow() => s_openWindows.FirstOrDefault(w => w != this && !w.IsDisposed && !w._dead);

    /// <summary>
    /// This customer copied files. True when they are held for the other window instead of being fetched to this PC —
    /// having said so on the status bar. False (with <paramref name="note"/> saying why when there IS another window) for
    /// the PC route, as with one window open.
    /// </summary>
    private bool HoldForTheOtherWindow(List<string> files, long bytes, out string note)
    {
        note = "";
        _copiedHere = null;
        var other = OtherWindow();
        if (other == null) return false;
        if (!CanBridge || !other.CanBridge)
        {
            var older = !_hostCanBridge ? MachineName : !other._hostCanBridge ? other.MachineName : null;
            note = older != null ? $"through this PC — {older}'s app is older" : "through this PC — a session is not end-to-end encrypted";
            Trace.Write($"bridge: {files.Count} item(s) copied on {MachineName} go {note}");
            return false;
        }
        uint seq = 0;
        try { seq = GetClipboardSequenceNumber(); } catch { }
        _copiedHere = new CopiedHere { Paths = files, Bytes = bytes, Seq = seq };
        // The click brings them to this PC, for when the technician wants them here (Clint's answer 1).
        _pendingFetch = files;
        var size = bytes > 0 ? $" ({FileSetLimits.Human(bytes)})" : "";
        OfferOnStatusBar($"they copied {_copiedHere.Describe}{size} — click into {other.MachineName}'s window and paste, or click here to bring them to this PC");
        Trace.Write($"bridge: holding {files.Count} item(s), {FileSetLimits.Human(bytes)}, copied on {MachineName} for {other.MachineName} (seq {seq})");
        return true;
    }

    /// <summary>
    /// Asked by <see cref="SyncMyClipboard"/> FIRST, as this window comes to the front: the other window's customer copied
    /// files that are still the newest copy and have not come here — send them straight across. True when handled.
    /// </summary>
    private bool TakeOtherWindowsCopyStraight(string why)
    {
        uint seq = 0;
        try { seq = GetClipboardSequenceNumber(); } catch { }
        var from = s_openWindows.FirstOrDefault(w => w != this && !w.IsDisposed && w._copiedHere is { } c && c.Seq == seq);
        if (from == null) return false;
        var copy = from._copiedHere!;
        if (copy.SentTo.Contains(_session.SessionId))
        {
            // Already on its way here, or here: an alt-tab, not a new copy.
            if (_bridgeIn != null && _bridgeIn.Copy == copy) SetTransferStatus($"{copy.Describe} from {from.MachineName} still arriving — don't paste yet");
            return true;
        }
        if (_dead || _session.ViewOnly) return true;
        if (!CanBridge || !from.CanBridge)
        {
            SendThroughThisPc(from, copy, !_hostCanBridge ? $"{MachineName}'s app is older" : "a session is not end-to-end encrypted");
            return true;
        }
        if (copy.Bytes > FileSetLimits.AutoFetchBytes)
        {
            // The 50 MB rule (Clint's answer 4): it no longer costs this PC's line, but it still costs both customers'.
            _pendingBridgeFrom = (from, copy);
            OfferOnStatusBar($"{copy.Describe} ({FileSetLimits.Human(copy.Bytes)}) copied on {from.MachineName} — click here to send it straight to {MachineName}");
            return true;
        }
        StartBridge(from, copy, why);
        return true;
    }

    private void StartBridge(SessionForm from, CopiedHere copy, string why)
    {
        var id = Guid.NewGuid().ToString("N");
        copy.SentTo.Add(_session.SessionId);
        _bridgeIn = new BridgeIn { Id = id, From = from, Copy = copy, Key = RandomNumberGenerator.GetBytes(32) };
        from._bridgeOutId = id; from._bridgeOutTo = MachineName;
        // The server says yes by sending the ask back (OnBridgeAsked); no to its answer, BridgeEndMsg.Refused.
        _link.Send(new BridgeAskMsg { BridgeId = id, FromSessionId = from._session.SessionId, Bytes = copy.Bytes, Items = copy.Paths.Count });
        SetTransferStatus($"asking to send {copy.Describe} from {from.MachineName} straight here…");
        _xferIdle.Stop();
        Trace.Write($"bridge {id}: asked to copy {copy.Paths.Count} item(s) straight from {from.MachineName} to {MachineName} ({why})");
        var noAnswer = new System.Windows.Forms.Timer { Interval = BridgeAskTimeoutMs };
        noAnswer.Tick += (_, _) =>
        {
            noAnswer.Stop(); noAnswer.Dispose();
            var b = _bridgeIn;
            if (b == null || b.Id != id || b.KeysSent || IsDisposed) return;
            _bridgeIn = null;
            try { _link.Send(new BridgeEndMsg { BridgeId = id, Why = "the server did not answer" }); } catch { }
            SendThroughThisPc(from, copy, "the server did not answer");
        };
        noAnswer.Start();
    }

    /// <summary>The server joined the two sessions: each machine is given the key, inside its own sealed session.</summary>
    private void OnBridgeAsked(BridgeAskMsg yes)
    {
        var b = _bridgeIn;
        if (b == null || b.Id != yes.BridgeId || b.KeysSent) return;
        b.KeysSent = true;
        var key = Convert.ToBase64String(b.Key);
        bool sent = !b.From.IsDisposed
            && _link.SendSealed(new BridgeKeyMsg { BridgeId = b.Id, Key = key, Send = false, Other = b.From.MachineName })
            && b.From._link.SendSealed(new BridgeKeyMsg { BridgeId = b.Id, Key = key, Send = true, Paths = b.Copy.Paths, Other = MachineName });
        Array.Clear(b.Key);
        if (!sent)
        {
            _link.Send(new BridgeEndMsg { BridgeId = b.Id, Why = "a session was not sealed when the key was due" });
            _bridgeIn = null;
            SendThroughThisPc(b.From, b.Copy, "a session was not end-to-end encrypted at that moment");
            return;
        }
        SetTransferStatus($"{b.Copy.Describe} from {b.From.MachineName} coming straight here — don't paste yet");
        _xferIdle.Stop();
        b.From.SetTransferStatus($"sending {b.Copy.Describe} straight to {MachineName}…");
        Trace.Write($"bridge {b.Id}: the server joined the sessions; keys sent to both machines, sealed");
    }

    private void OnBridgeProgress(BridgeProgressMsg p)
    {
        var b = _bridgeIn;
        if (b == null || b.Id != p.BridgeId) return;
        var line = $"{p.FilesDone}/{p.FilesTotal} files, {FileSetLimits.Human(p.BytesDone)} of {FileSetLimits.Human(p.TotalBytes)}";
        SetTransferStatus($"{line} from {b.From.MachineName} — don't paste yet", p.BytesDone, p.TotalBytes);
        _xferIdle.Stop();
        if (!b.From.IsDisposed) b.From.SetTransferStatus($"{line} sent straight to {MachineName}", p.BytesDone, p.TotalBytes);
    }

    /// <summary>The server says the copy is over — told to both windows; each says its own half.</summary>
    private void OnBridgeEnded(BridgeEndMsg e)
    {
        if (e.BridgeId == _bridgeOutId)
        {
            _bridgeOutId = null;
            if (e.Ok) SetTransferStatus($"sent {Count(e)} straight to {_bridgeOutTo}");
            else if (!e.Refused) SetTransferStatus($"the copy to {_bridgeOutTo} did not finish — {e.Why}");
        }
        var b = _bridgeIn;
        if (b == null || b.Id != e.BridgeId) return;
        _bridgeIn = null;
        Trace.Write($"bridge {e.BridgeId}: {(e.Ok ? $"landed, {e.Files} file(s), {FileSetLimits.Human(e.Bytes)}" : (e.Refused ? "REFUSED — " : "ended — ") + e.Why)}");
        if (e.Ok) { SetTransferStatus($"{Count(e)} from {b.From.MachineName} on {MachineName}'s clipboard — paste it now"); return; }
        // Refused, or the server is older and does not know the ask: through this PC, unasked (Clint's answer 6).
        if (e.Refused) { SendThroughThisPc(b.From, b.Copy, e.Why); return; }
        // Failed half-way: say so, and OFFER the PC — never double the traffic without asking.
        b.Copy.SentTo.Remove(_session.SessionId);
        _pendingViaPcFrom = (b.From, b.Copy);
        OfferOnStatusBar($"the copy from {b.From.MachineName} did not finish ({e.Why}) — click here to send it through this PC instead");
    }

    private static string Count(BridgeEndMsg e) =>
        $"{e.Files} file{(e.Files == 1 ? "" : "s")}{(e.Bytes > 0 ? $" ({FileSetLimits.Human(e.Bytes)})" : "")}";

    /// <summary>The old way: fetched to this PC from <paramref name="from"/>'s machine, then here as with any copy — this
    /// window waits for it (SessionForm.Between.cs) and sends it the moment it lands if it is still in front.</summary>
    private void SendThroughThisPc(SessionForm from, CopiedHere copy, string why)
    {
        if (from.IsDisposed) { SetTransferStatus($"the files copied on {from.MachineName} cannot be sent — its window has closed"); return; }
        Trace.Write($"bridge: {copy.Paths.Count} item(s) from {from.MachineName} to {MachineName} go through this PC — {why}");
        from._copiedHere = null;
        from.SetTransferStatus($"fetching {copy.Describe} through this PC for {MachineName}…");
        _ = from.PullFileSet(copy.Paths, thenOnMyClipboard: true);
        _waitingOn = from;
        SetTransferStatus($"files copied on {from.MachineName} are coming through this PC ({why}) — don't paste yet; they come here the moment they land");
        _xferIdle.Stop();
    }

    /// <summary>A click on one of this file's offers. True when it was one of them.</summary>
    private bool TakeBridgeOffer()
    {
        var big = _pendingBridgeFrom;
        var viaPc = _pendingViaPcFrom;
        _pendingBridgeFrom = null; _pendingViaPcFrom = null;
        if (big is { } b && !b.From.IsDisposed)
        {
            if (b.From._copiedHere == b.Copy) StartBridge(b.From, b.Copy, "clicked");
            else SetTransferStatus($"something newer was copied since — that copy from {b.From.MachineName} was not sent");
            return true;
        }
        if (viaPc is { } v) { SendThroughThisPc(v.From, v.Copy, "asked for"); return true; }
        return big != null;
    }

    /// <summary>A window closed. Files held for it here go the old way now — to this PC — as with one window open.</summary>
    private void OtherWindowGone()
    {
        var c = _copiedHere;
        if (c == null || OtherWindow() != null) return;
        _copiedHere = null;
        uint seq = 0;
        try { seq = GetClipboardSequenceNumber(); } catch { }
        if (c.SentTo.Count > 0 || seq != c.Seq) return;
        if (c.Bytes > FileSetLimits.AutoFetchBytes) return;   // its "bring them to this PC" offer is still on the status bar
        Trace.Write($"bridge: the other window closed; fetching the {c.Paths.Count} item(s) copied on {MachineName} to this PC as usual");
        SetTransferStatus($"they copied {c.Describe}; fetching…");
        _ = PullFileSet(c.Paths, thenOnMyClipboard: true);
    }
}
