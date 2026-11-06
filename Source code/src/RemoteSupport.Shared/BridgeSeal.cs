using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RemoteSupport.Shared;

// ---- copying files straight from one machine to the other (2026-10-15; docs/NEXT-SESSION-HOST2HOST.md, HANDOVER §144) ----
//
// Clint: with two machines open, "build the copy and paste to work directly between both machines ... so that it doesnt
// go through the technician PC". A copy on A used to come down to the technician's PC and go back up to B. Now the
// console asks the hub to BRIDGE the two sessions for one copy: host A sends, host B receives, through the server — never
// the technician's line or disk.
//
// End-to-end encryption is per session (SessionSeal), so neither host can read the other's frames. Each copy gets a key of
// its own: the console makes it and hands it to each host INSIDE that host's own sealed session (BridgeKeyMsg is never on
// the clear list), so the server relays the files and can read none of them. The console vouches for the pairing — it has
// already checked both machines' identities (MachinePins), and it is the technician's own console, which could pull those
// same files anyway. A host-to-host exchange signed with the machines' identities would need each to learn the other's
// identity from somebody, and the only candidates are that same console or the server we are hiding from.

/// <summary>What a host says in its <see cref="SysInfoMsg"/> when it can take part in a bridge. Read by the hub (which
/// never asks an older host) and the console (which then sends files through the PC as before).</summary>
public static class HostCan
{
    public const string BridgeKey = "Copy straight to another machine";
    public const string Yes = "yes";
    public static bool Bridge(SysInfoMsg? si) => si?.Info != null && si.Info.TryGetValue(BridgeKey, out var v) && v == Yes;
}

/// <summary>Console -> hub, on the RECEIVING session's socket, clear: "join the session I have open on
/// <see cref="FromSessionId"/> to this one, for one copy." The hub checks who may (TenantHub.Bridge.cs) and answers with a
/// <see cref="BridgeEndMsg"/> (Refused) when it refuses, and with this same message SENT BACK when it agrees — only then does
/// the console hand each machine the key.</summary>
public sealed class BridgeAskMsg : Msg
{
    public string BridgeId { get; set; } = "";
    public string FromSessionId { get; set; } = "";
    /// <summary>What the copy weighs, as the sending machine measured it — for the server's size rule.</summary>
    public long Bytes { get; set; }
    public int Items { get; set; }
}

/// <summary>Console -> host, SEALED in that host's session (never on the clear list): the key for one copy, and which
/// end this machine is. The host refuses one that arrived clear — only the server could have sent it.</summary>
public sealed class BridgeKeyMsg : Msg
{
    public string BridgeId { get; set; } = "";
    /// <summary>32 random bytes, base64, made by the console for this copy only.</summary>
    public string Key { get; set; } = "";
    /// <summary>True: this machine SENDS (<see cref="Paths"/>). False: it receives, into its paste staging, and puts the
    /// files on the person's clipboard when the last byte lands.</summary>
    public bool Send { get; set; }
    public List<string> Paths { get; set; } = new();
    /// <summary>The other machine's name, for this host's trace only.</summary>
    public string Other { get; set; } = "";
}

/// <summary>The bridge is over — clear, because the hub acts on it. From a host (done, or failed), from the console
/// (cancelled), or from the hub (refused, a session or technician gone, nothing moving); the hub passes it to both hosts
/// and the technician's windows. Counts and a reason, never a file name.</summary>
public sealed class BridgeEndMsg : Msg
{
    public string BridgeId { get; set; } = "";
    /// <summary>Every byte landed on the receiving machine and is on its clipboard.</summary>
    public bool Ok { get; set; }
    /// <summary>The server would not join the two sessions: the console sends it through the PC instead, unasked
    /// (Clint's answer 6).</summary>
    public bool Refused { get; set; }
    public string Why { get; set; } = "";
    public int Files { get; set; }
    public long Bytes { get; set; }
}

/// <summary>Receiving host -> its own session's console, sealed: how far the copy has got.</summary>
public sealed class BridgeProgressMsg : Msg
{
    public string BridgeId { get; set; } = "";
    public long BytesDone { get; set; }
    public long TotalBytes { get; set; }
    public int FilesDone { get; set; }
    public int FilesTotal { get; set; }
}

/// <summary>INSIDE a bridge, receiver -> sender: I hold the key and my clipboard is emptied; offer.</summary>
public sealed class BridgeReadyMsg : Msg { }

/// <summary>INSIDE a bridge, receiver -> sender: this many bytes are written. The sender stays within
/// <see cref="BridgeSeal.Window"/> of it, so a slow receiving line never makes the hub hold the SENDER's socket — which
/// would freeze the picture of the machine being copied from.</summary>
public sealed class BridgeAckMsg : Msg { public long Bytes { get; set; } }

/// <summary>
/// The seal of one bridge, at one host. AES-256-GCM, the frame <c>[8][idLen][id][sender:u32][counter:u64][ct+tag]</c>,
/// the visible header as associated data, nonce = sender ‖ counter. The sender is 1, the receiver 2; each end refuses its
/// OWN number (a server bouncing a host's frames back at it) and any counter not higher than the last it saw.
/// Inside: <c>[0][json]</c> for a message, or a whole <see cref="BinKind.FileChunk"/> frame.
/// </summary>
public sealed class BridgeSeal : IDisposable
{
    public const uint SenderNumber = 1, ReceiverNumber = 2;
    /// <summary>How far ahead of the receiver's acknowledgement the sender may be.</summary>
    public const long Window = 8L * 1024 * 1024;
    /// <summary>How often the receiver acknowledges.</summary>
    public const long AckEvery = 1L * 1024 * 1024;
    /// <summary>A bridge that moves nothing for this long is over (the same figure as SessionForm.ClipFetchStallMs).</summary>
    public const int StallMs = 30000;

    private const int TagBytes = 16;
    private const byte InnerMessage = 0;
    private readonly AesGcm _aes;
    private readonly byte[] _header;
    private readonly uint _me, _them;
    private ulong _counter, _lastTheirs;
    private readonly object _sendLock = new(), _recvLock = new();

    public string BridgeId { get; }
    public long Refused;

    public BridgeSeal(string bridgeId, byte[] key, bool sending)
    {
        if (!IsId(bridgeId)) throw new ArgumentException("not a bridge id");
        if (key.Length != 32) throw new ArgumentException("A bridge key is 32 bytes.");
        BridgeId = bridgeId;
        _aes = new AesGcm(key, TagBytes);
        var id = Encoding.ASCII.GetBytes(bridgeId);
        _header = new byte[2 + id.Length];
        _header[0] = BinKind.Bridge; _header[1] = (byte)id.Length; id.CopyTo(_header, 2);
        _me = sending ? SenderNumber : ReceiverNumber;
        _them = sending ? ReceiverNumber : SenderNumber;
    }

    public void Dispose() { lock (_sendLock) _aes.Dispose(); }

    /// <summary>A bridge id: 1–64 letters, digits or hyphens — it is routed by, so it must be plain.</summary>
    public static bool IsId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '-');

    /// <summary>The bridge id of a bridge frame, or null. What the hub routes by — it can read nothing else.</summary>
    public static string? IdOf(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2 || frame[0] != BinKind.Bridge) return null;
        int len = frame[1];
        if (len == 0 || frame.Length < 2 + len + 12 + TagBytes) return null;
        var id = Encoding.ASCII.GetString(frame.Slice(2, len));
        return IsId(id) ? id : null;
    }

    public byte[] SealMessage(Msg m)
    {
        var json = Encoding.UTF8.GetBytes(Json.Serialize(m));
        var plain = new byte[1 + json.Length];
        plain[0] = InnerMessage; json.CopyTo(plain, 1);
        return Seal(plain);
    }

    /// <summary>A <see cref="BinKind.FileChunk"/> frame (from <see cref="Bin.FileChunk"/>), sealed whole.</summary>
    public byte[] SealChunk(ReadOnlySpan<byte> chunkFrame)
    {
        if (chunkFrame.Length == 0 || chunkFrame[0] != BinKind.FileChunk) throw new ArgumentException("not a file chunk");
        return Seal(chunkFrame);
    }

    private byte[] Seal(ReadOnlySpan<byte> plain)
    {
        lock (_sendLock)
        {
            var frame = new byte[_header.Length + 12 + plain.Length + TagBytes];
            _header.CopyTo(frame, 0);
            var nonce = frame.AsSpan(_header.Length, 12);
            BinaryPrimitives.WriteUInt32BigEndian(nonce, _me);
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], ++_counter);
            _aes.Encrypt(nonce, plain, frame.AsSpan(_header.Length + 12, plain.Length),
                frame.AsSpan(_header.Length + 12 + plain.Length, TagBytes), _header);
            return frame;
        }
    }

    /// <summary>Opens a frame from the OTHER end: a message, or a file chunk frame — or (null, null), refused and counted:
    /// another bridge's, altered, replayed, reflected back, or not from the other end at all.</summary>
    public (Msg? Message, byte[]? Chunk) Open(ReadOnlySpan<byte> frame)
    {
        if (IdOf(frame) != BridgeId) { Interlocked.Increment(ref Refused); return (null, null); }
        int h = _header.Length;
        if (!frame[..h].SequenceEqual(_header)) { Interlocked.Increment(ref Refused); return (null, null); }
        var nonce = frame.Slice(h, 12);
        uint sender = BinaryPrimitives.ReadUInt32BigEndian(nonce);
        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(nonce[4..]);
        if (sender != _them) { Interlocked.Increment(ref Refused); return (null, null); }
        int ctLen = frame.Length - h - 12 - TagBytes;
        var plain = new byte[ctLen];
        try { _aes.Decrypt(nonce, frame.Slice(h + 12, ctLen), frame.Slice(h + 12 + ctLen, TagBytes), plain, _header); }
        catch (CryptographicException) { Interlocked.Increment(ref Refused); return (null, null); }
        lock (_recvLock)
        {
            // Only after it is proved genuine: a forged frame must not move the counter.
            if (counter <= _lastTheirs) { Interlocked.Increment(ref Refused); return (null, null); }
            _lastTheirs = counter;
        }
        if (plain.Length == 0) { Interlocked.Increment(ref Refused); return (null, null); }
        if (plain[0] == BinKind.FileChunk) return (null, plain);
        if (plain[0] != InnerMessage) { Interlocked.Increment(ref Refused); return (null, null); }
        Msg? m = null;
        try { m = Json.Deserialize<Msg>(Encoding.UTF8.GetString(plain, 1, plain.Length - 1)); } catch { }
        if (m == null) Interlocked.Increment(ref Refused);
        return (m, null);
    }
}
