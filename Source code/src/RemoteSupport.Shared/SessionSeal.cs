using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RemoteSupport.Shared;

/// <summary>
/// End-to-end encryption of a session (2026-10-07, roadmap 4.4; docs/NEXT-SESSION-E2E.md, HANDOVER §112).
///
/// <para>One per session socket, at each END — the host and each console. Never on the server: it relays the
/// frames this makes without being able to open them. <see cref="WsConnection.Seal"/> calls it as frames are
/// queued and as they arrive, so no call site above the connection knows it is there.</para>
///
/// <para><b>What is sealed: everything except <see cref="StaysClear(Type)"/></b> — the short list the server ACTS on
/// (the consent gate, the audit trail, the disclosures, the handshake). Safe by default: a message added next
/// year is sealed unless somebody decides the server must read it.</para>
///
/// <para>Frames, AES-256-GCM with the visible header and the session id as associated data:</para>
/// <list type="bullet">
/// <item>a message: <c>[7][tagLen][tag][sender:u32][counter:u64][ciphertext+tag16]</c> — the tag is its JSON type
/// name, visible for the allow-lists; the receiver refuses one whose opened type is not its tag, or is a clear
/// type (so nothing is dressed up as a mouse movement, and nothing the hub must see goes past it sealed);</item>
/// <item>a binary frame: <c>[kind|0x80][sender][counter][ciphertext+tag16]</c> — the kind still visible for the
/// drop rule and the allow-lists (<see cref="BinKind.Of"/>).</item>
/// </list>
/// <para>Nonce = sender ‖ counter: the host is sender 0, each viewer has its own number from the host. A receiver
/// keeps the last counter per sender and refuses anything not higher, so the server cannot replay a frame.</para>
/// </summary>
public sealed class SessionSeal : IDisposable
{
    private const int TagBytes = 16;
    private readonly byte[] _sessionId;
    private AesGcm? _aes;
    private uint _sender;
    private ulong _counter;
    private readonly object _sendLock = new();
    private readonly Dictionary<uint, ulong> _last = new();
    private readonly object _recvLock = new();

    public SessionSeal(string sessionId) => _sessionId = Encoding.UTF8.GetBytes(sessionId ?? "");

    /// <summary>The session key is in hand: sealed frames can be opened.</summary>
    public bool Keyed => _aes != null;

    /// <summary>Seal what this end SENDS. The host: while the hub says every viewer can open it
    /// (<see cref="E2eModeMsg"/>). A console: once keyed.</summary>
    public volatile bool Sealing;

    /// <summary>Refuse CONTENT that arrives clear. The host, while sealing: clear content could then only have come
    /// from the server. A console never refuses — it SHOWS it instead (<see cref="ClearContentArrived"/>).</summary>
    public volatile bool RefuseClearContent;

    /// <summary>Content opened under the session key — the badge's "encrypted" evidence.</summary>
    public long Opened;
    /// <summary>Content that arrived clear — the badge's "not end-to-end encrypted" evidence, for the session.</summary>
    public long ClearContent;
    /// <summary>Frames refused: wrong key, altered, replayed, a tag that lied, a clear type sealed.</summary>
    public long Refused;

    /// <summary>Raised the first time content arrives clear.</summary>
    public event Action? ClearContentArrived;
    /// <summary>Raised the first time content is opened.</summary>
    public event Action? FirstOpened;

    public void UseKey(byte[] key, uint sender)
    {
        if (key.Length != 32) throw new ArgumentException("A session key is 32 bytes.");
        lock (_sendLock) { _aes?.Dispose(); _aes = new AesGcm(key, TagBytes); _sender = sender; _counter = 0; }
    }

    public void Dispose() { lock (_sendLock) { _aes?.Dispose(); _aes = null; } }

    // ------------------------------------------------------------------ what stays clear

    /// <summary>
    /// THE list of what the server may read in a session: what it acts on. Everything else is sealed.
    /// See docs/NEXT-SESSION-E2E.md for why each one is here. Add to it only for something the HUB acts on.
    /// </summary>
    private static readonly HashSet<Type> ClearTypes = new()
    {
        // the session's own life
        typeof(PingMsg), typeof(ErrorMsg), typeof(SessionEndedMsg), typeof(ParticipantsMsg), typeof(EndSessionMsg),
        // the consent gate
        typeof(DeclineSessionMsg), typeof(ConsentGrantedMsg),
        // replayed to a late joiner: the layout and the machine's description, no content
        typeof(MonitorsMsg), typeof(SysInfoMsg),
        // written on the ticket and/or told to the customer's tray app — the disclosures
        typeof(PrivacyStateMsg), typeof(AudioStatusMsg), typeof(VoiceStateMsg), typeof(FileActivityMsg),
        typeof(RecordingStateMsg), typeof(VoiceTalkMsg),
        // audit, and Ctrl+Alt+Del goes to the agent
        typeof(SysActionMsg), typeof(SysActionResultMsg),
        // only the customer answers about their microphone: the hub drops a technician's
        typeof(VoiceMicAnswerMsg),
        // routed to the tray app; the customer's record
        typeof(ChatMsg),
        // the ticket's line for a script (the script itself is sealed)
        typeof(ScriptAuditMsg),
        // the hub's own request for a complete picture, after it dropped a frame to a viewer (§142)
        typeof(KeyFrameRequestMsg),
        // the handshake
        typeof(E2eReadyMsg), typeof(E2eHelloMsg), typeof(E2eKeyMsg), typeof(E2eModeMsg),
        // copying straight from one machine to another (2026-10-15): the hub checks the ask and ends the bridge.
        // NEVER BridgeKeyMsg — the key travels sealed, or the server could read every file it carries.
        typeof(BridgeAskMsg), typeof(BridgeEndMsg),
    };

    /// <summary>The clear list by tag — <c>wwwroot/e2e.js</c> keeps a copy, and tools/e2ecore fails if the two differ.</summary>
    public static IEnumerable<string> ClearTags => ClearTypes.Select(t => TagOf[t]).OrderBy(t => t, StringComparer.Ordinal);

    public static bool StaysClear(Msg m) => ClearTypes.Contains(m.GetType());
    public static bool StaysClear(Type t) => ClearTypes.Contains(t);

    /// <summary>Binary frames that carry content: everything a session sends as binary.</summary>
    public static bool IsContentBinary(byte kind) => kind is BinKind.Tiles or BinKind.FileChunk or BinKind.Audio
        or BinKind.Video or BinKind.Cursor or BinKind.Voice;

    // ------------------------------------------------------------------ tags: a message's JSON type name

    private static readonly Dictionary<Type, string> TagOf = new();
    private static readonly Dictionary<string, Type> TypeOf = new(StringComparer.Ordinal);

    static SessionSeal()
    {
        foreach (var a in typeof(Msg).GetCustomAttributes<JsonDerivedTypeAttribute>())
            if (a.TypeDiscriminator is string tag) { TagOf[a.DerivedType] = tag; TypeOf[tag] = a.DerivedType; }
    }

    public static string Tag(Msg m) => TagOf.TryGetValue(m.GetType(), out var t) ? t : throw new InvalidOperationException($"{m.GetType().Name} has no tag");

    /// <summary>An empty message of a sealed frame's visible type, for the server's allow-lists — the same
    /// questions (<c>m is MouseMsg</c>) it asks of a clear one. Null for a tag that is not a message.</summary>
    public static Msg? Probe(string tag) =>
        TypeOf.TryGetValue(tag, out var t) && t.GetConstructor(Type.EmptyTypes) != null ? (Msg?)Activator.CreateInstance(t) : null;

    /// <summary>The visible tag of a sealed message frame, or null if this is not one.</summary>
    public static string? TagOfFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2 || frame[0] != BinKind.Sealed) return null;
        int len = frame[1];
        if (frame.Length < 2 + len + 12 + TagBytes) return null;
        return Encoding.UTF8.GetString(frame.Slice(2, len));
    }

    /// <summary>A sealed frame of any kind (a message, or binary with the sealed bit).</summary>
    public static bool IsSealedFrame(ReadOnlySpan<byte> frame) =>
        frame.Length > 0 && (frame[0] == BinKind.Sealed || (frame[0] & BinKind.SealedBit) != 0);

    // ------------------------------------------------------------------ sealing

    /// <summary>A message as a sealed frame. Caller holds the order (see <see cref="WsConnection"/>).</summary>
    public byte[] SealMessage(Msg m)
    {
        var tag = Encoding.UTF8.GetBytes(Tag(m));
        if (tag.Length > 255) throw new InvalidOperationException("tag too long");
        var header = new byte[2 + tag.Length];
        header[0] = BinKind.Sealed; header[1] = (byte)tag.Length; tag.CopyTo(header, 2);
        return Seal(header, Encoding.UTF8.GetBytes(Json.Serialize(m)));
    }

    /// <summary>A binary frame sealed, its kind kept visible with the sealed bit.</summary>
    public byte[] SealBinary(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0) throw new ArgumentException("empty frame");
        return Seal(new[] { (byte)(frame[0] | BinKind.SealedBit) }, frame[1..]);
    }

    private byte[] Seal(byte[] header, ReadOnlySpan<byte> plain)
    {
        lock (_sendLock)
        {
            if (_aes == null) throw new InvalidOperationException("no session key");
            var nonce = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(nonce, _sender);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), ++_counter);
            var frame = new byte[header.Length + 12 + plain.Length + TagBytes];
            header.CopyTo(frame, 0);
            nonce.CopyTo(frame, header.Length);
            var ct = frame.AsSpan(header.Length + 12, plain.Length);
            var tag = frame.AsSpan(header.Length + 12 + plain.Length, TagBytes);
            _aes.Encrypt(nonce, plain, ct, tag, Aad(header));
            return frame;
        }
    }

    private byte[] Aad(ReadOnlySpan<byte> header)
    {
        var aad = new byte[header.Length + _sessionId.Length];
        header.CopyTo(aad); _sessionId.CopyTo(aad.AsSpan(header.Length));
        return aad;
    }

    // ------------------------------------------------------------------ opening

    /// <summary>A sealed message frame opened, or null — refused (counted) or not openable yet.</summary>
    public Msg? OpenMessage(ReadOnlySpan<byte> frame)
    {
        var tag = TagOfFrame(frame);
        if (tag == null) { Interlocked.Increment(ref Refused); return null; }
        int headerLen = 2 + frame[1];
        var plain = Open(frame, headerLen, out _);
        if (plain == null) return null;
        Msg? m = null;
        try { m = Json.Deserialize<Msg>(Encoding.UTF8.GetString(plain)); } catch { }
        // The visible tag must be the truth, and nothing the server must see may arrive sealed.
        if (m == null || !TagOf.TryGetValue(m.GetType(), out var real) || real != tag || StaysClear(m))
        { Interlocked.Increment(ref Refused); return null; }
        NoteOpened();
        return m;
    }

    /// <summary>A sealed binary frame opened back to <c>[kind][payload]</c>, or null.</summary>
    public byte[]? OpenBinary(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 1 + 12 + TagBytes || (frame[0] & BinKind.SealedBit) == 0) { Interlocked.Increment(ref Refused); return null; }
        var plain = Open(frame, 1, out _);
        if (plain == null) return null;
        var kind = BinKind.Of(frame[0]);
        if (!IsContentBinary(kind)) { Interlocked.Increment(ref Refused); return null; }
        var outFrame = new byte[1 + plain.Length];
        outFrame[0] = kind; plain.CopyTo(outFrame, 1);
        NoteOpened();
        return outFrame;
    }

    private byte[]? Open(ReadOnlySpan<byte> frame, int headerLen, out uint sender)
    {
        sender = 0;
        var aes = _aes;
        if (aes == null || frame.Length < headerLen + 12 + TagBytes) { Interlocked.Increment(ref Refused); return null; }
        var nonce = frame.Slice(headerLen, 12);
        sender = BinaryPrimitives.ReadUInt32BigEndian(nonce);
        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(nonce[4..]);
        int ctLen = frame.Length - headerLen - 12 - TagBytes;
        var plain = new byte[ctLen];
        try { aes.Decrypt(nonce, frame.Slice(headerLen + 12, ctLen), frame.Slice(headerLen + 12 + ctLen, TagBytes), plain, Aad(frame[..headerLen])); }
        catch (CryptographicException) { Interlocked.Increment(ref Refused); return null; }
        // Only after it is proved genuine: a forged frame must not move the counter.
        lock (_recvLock)
        {
            if (_last.TryGetValue(sender, out var last) && counter <= last) { Interlocked.Increment(ref Refused); return null; }
            _last[sender] = counter;
        }
        return plain;
    }

    private void NoteOpened() { if (Interlocked.Increment(ref Opened) == 1) FirstOpened?.Invoke(); }

    /// <summary>Content arrived clear. Counted (and raised once) for the badge.</summary>
    public void NoteClearContent() { if (Interlocked.Increment(ref ClearContent) == 1) ClearContentArrived?.Invoke(); }
}

/// <summary>
/// The handshake that gives each viewer the session key (docs/NEXT-SESSION-E2E.md, "The key"). Plain P-256 ECDH,
/// HKDF-SHA256, AES-GCM and ECDSA P-256 / SHA-256 with r‖s signatures — chosen because WebCrypto does exactly the
/// same, so the browser routes speak it too.
/// </summary>
public static class E2eHandshake
{
    private const string WrapInfo = "rsp-e2e wrap v1";
    private const string SignLabel = "rsp-e2e key v1";

    public static ECDiffieHellman NewEphemeral() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>A public key as 65 bytes (0x04 ‖ X ‖ Y), base64 — WebCrypto's "raw" format.</summary>
    public static string PubOf(ECDiffieHellman k) => Raw(k.ExportParameters(false).Q);
    public static string PubOf(ECDsa k) => Raw(k.ExportParameters(false).Q);

    private static string Raw(ECPoint q)
    {
        var raw = new byte[65]; raw[0] = 4; q.X!.CopyTo(raw, 1); q.Y!.CopyTo(raw, 33);
        return Convert.ToBase64String(raw);
    }

    private static ECParameters? Params(string b64)
    {
        try
        {
            var raw = Convert.FromBase64String(b64 ?? "");
            if (raw.Length != 65 || raw[0] != 4) return null;
            return new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = raw[1..33], Y = raw[33..] } };
        }
        catch (FormatException) { return null; }
    }

    private static ECDiffieHellman? ImportDh(string b64)
    {
        if (Params(b64) is not { } p) return null;
        try { var k = ECDiffieHellman.Create(); k.ImportParameters(p); return k; } catch (CryptographicException) { return null; }
    }

    public static ECDsa? ImportIdentity(string b64)
    {
        if (Params(b64) is not { } p) return null;
        try { var k = ECDsa.Create(); k.ImportParameters(p); return k; } catch (CryptographicException) { return null; }
    }

    private static byte[]? WrapKey(ECDiffieHellman mine, string theirs, string sessionId, string viewerPub, string hostPub)
    {
        using var other = ImportDh(theirs);
        if (other == null) return null;
        byte[] shared;
        try { shared = mine.DeriveRawSecretAgreement(other.PublicKey); } catch (CryptographicException) { return null; }
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, Encoding.UTF8.GetBytes(sessionId),
            Encoding.UTF8.GetBytes($"{WrapInfo}|{viewerPub}|{hostPub}"));
    }

    /// <summary>What the identity key signs: everything that makes this answer THIS viewer's, in THIS session.</summary>
    public static byte[] Signed(string sessionId, string viewerPub, string hostPub, uint senderId) =>
        Encoding.UTF8.GetBytes($"{SignLabel}|{sessionId}|{viewerPub}|{hostPub}|{senderId}");

    /// <summary>The host's answer to one viewer's hello. Null if their key is not a key.</summary>
    public static E2eKeyMsg? Answer(string sessionId, ECDiffieHellman hostEph, byte[] sessionKey, string viewerPub, uint senderId, ECDsa? identity)
    {
        var hostPub = PubOf(hostEph);
        var wrap = WrapKey(hostEph, viewerPub, sessionId, viewerPub, hostPub);
        if (wrap == null) return null;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[sessionKey.Length + 16];
        using (var g = new AesGcm(wrap, 16)) g.Encrypt(nonce, sessionKey, ct.AsSpan(0, sessionKey.Length), ct.AsSpan(sessionKey.Length), Encoding.UTF8.GetBytes(sessionId));
        var m = new E2eKeyMsg { For = viewerPub, HostPub = hostPub, SenderId = senderId, Nonce = Convert.ToBase64String(nonce), Wrapped = Convert.ToBase64String(ct) };
        if (identity != null)
        {
            m.IdentityPub = PubOf(identity);
            m.Signature = Convert.ToBase64String(identity.SignData(Signed(sessionId, viewerPub, hostPub, senderId), HashAlgorithmName.SHA256));
        }
        return m;
    }

    /// <summary>A viewer's reading of an answer meant for it.</summary>
    public sealed record Accepted(byte[] Key, uint SenderId, string IdentityPub, bool Signed);

    /// <summary>
    /// Opens an answer meant for this viewer: checks the identity signature when there is one, unwraps the key.
    /// Null (with why) when it is not ours, does not verify, or does not open. Whether the identity is the one
    /// PINNED is the caller's question (<c>MachinePins</c>), asked before the seal is used.
    /// </summary>
    public static Accepted? Accept(string sessionId, ECDiffieHellman viewerEph, E2eKeyMsg m, out string why)
    {
        why = "";
        var mine = PubOf(viewerEph);
        if (m.For != mine) { why = "not for this viewer"; return null; }
        bool signed = m.Signature.Length > 0 || m.IdentityPub.Length > 0;
        if (signed)
        {
            using var id = ImportIdentity(m.IdentityPub);
            byte[] sig;
            try { sig = Convert.FromBase64String(m.Signature); } catch (FormatException) { why = "the signature is not readable"; return null; }
            if (id == null || !id.VerifyData(Signed(sessionId, mine, m.HostPub, m.SenderId), sig, HashAlgorithmName.SHA256))
            { why = "the machine's signature does not verify"; return null; }
        }
        var wrap = WrapKey(viewerEph, m.HostPub, sessionId, mine, m.HostPub);
        if (wrap == null) { why = "the host's key is not a key"; return null; }
        try
        {
            var nonce = Convert.FromBase64String(m.Nonce);
            var ct = Convert.FromBase64String(m.Wrapped);
            if (nonce.Length != 12 || ct.Length != 48) { why = "the wrapped key is the wrong size"; return null; }
            var key = new byte[32];
            using var g = new AesGcm(wrap, 16);
            g.Decrypt(nonce, ct.AsSpan(0, 32), ct.AsSpan(32), key, Encoding.UTF8.GetBytes(sessionId));
            return new Accepted(key, m.SenderId, signed ? m.IdentityPub : "", signed);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { why = "the wrapped key does not open"; return null; }
    }

    /// <summary>A key as a person can compare it: the first 16 bytes of its SHA-256, in groups of four hex digits.</summary>
    public static string Fingerprint(string pubB64)
    {
        try
        {
            var h = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(pubB64)))[..32];
            return string.Join(" ", Enumerable.Range(0, 8).Select(i => h.Substring(i * 4, 4)));
        }
        catch (FormatException) { return "(not a key)"; }
    }
}
