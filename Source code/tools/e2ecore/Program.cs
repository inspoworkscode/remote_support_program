using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// END-TO-END ENCRYPTION, THE CORE (2026-10-07, docs/NEXT-SESSION-E2E.md, HANDOVER §112).
//
// SessionSeal, E2eHandshake, MachinePins and WsConnection.Seal on their own — no server:
//   1. tags and the clear list;
//   2. the handshake: a viewer gets the host's key, signed by the machine's identity; an answer for somebody
//      else, a signature over another session, a sender id changed, an identity swapped, all refused;
//   3. the seal: round trips, nothing readable in the frame, the kind visible; altered, replayed, older,
//      another session, the wrong key — all refused; AND frames built HERE by hand with the session key (an
//      independent reading of the format): a tag that lies, a clear type sealed — refused;
//   4. the pins: first time, matches, changed (by device id, and by name + owner under a NEW id), a pinned
//      machine answering unsigned, persistence, a damaged file;
//   5. two real WebSockets with seals: content sealed and opened, the clear list clear, the server's end (no
//      seal) passing sealed frames through untouched, a host refusing clear content while sealing.
//
//   dotnet run -c Release --project tools/e2ecore/e2ecore.csproj
// ---------------------------------------------------------------------------------------------

internal static class E2eCore
{
    private static int _pass, _fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    private const string Sid = "session-1234";

    private static async Task<int> Main()
    {
        Console.WriteLine("== 1. tags and the clear list ==");
        Check("a message's tag is its JSON type name", SessionSeal.Tag(new MouseMsg()) == "mouse" && SessionSeal.Tag(new ScriptRunMsg()) == "script_run");
        Check("a tag probes back to an empty message of that type, for the allow-lists", SessionSeal.Probe("mouse") is MouseMsg && SessionSeal.Probe("no_such") == null);
        Check("the server's messages stay clear: chat, consent, disclosures, the handshake",
            SessionSeal.StaysClear(new ChatMsg()) && SessionSeal.StaysClear(new ConsentGrantedMsg()) && SessionSeal.StaysClear(new FileActivityMsg())
            && SessionSeal.StaysClear(new E2eKeyMsg()) && SessionSeal.StaysClear(new ScriptAuditMsg()) && SessionSeal.StaysClear(new EndSessionMsg()));
        Check("content does not: keys, mouse, clipboard, scripts, files, printing",
            !SessionSeal.StaysClear(new KeyMsg()) && !SessionSeal.StaysClear(new MouseMsg()) && !SessionSeal.StaysClear(new ClipboardMsg())
            && !SessionSeal.StaysClear(new ScriptRunMsg()) && !SessionSeal.StaysClear(new ScriptOutputMsg()) && !SessionSeal.StaysClear(new FileListMsg())
            && !SessionSeal.StaysClear(new PrintFileMsg()));
        Check("every binary kind is content", Enumerable.Range(1, 6).All(k => SessionSeal.IsContentBinary((byte)k)));
        // The browser's copy of the clear list (wwwroot/e2e.js) must be THIS list, word for word — a drift would let a
        // browser seal something the hub must read (refused on arrival) or send content the hub could read.
        var js = File.ReadAllText(Path.Combine(FindRepoRoot()!, "src", "RemoteSupport.Platform", "wwwroot", "e2e.js"));
        var jsList = System.Text.RegularExpressions.Regex.Match(js, @"var CLEAR = \[(.*?)\];", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        var jsTags = System.Text.RegularExpressions.Regex.Matches(jsList, "'([a-z_0-9]+)'").Select(m => m.Groups[1].Value).OrderBy(t => t, StringComparer.Ordinal).ToList();
        var csTags = SessionSeal.ClearTags.ToList();
        Check("the browser's clear list (e2e.js) is exactly SessionSeal's", jsTags.SequenceEqual(csTags),
            $"only in C#: {string.Join(",", csTags.Except(jsTags))}; only in e2e.js: {string.Join(",", jsTags.Except(csTags))}");

        // ---- 2. the handshake ------------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("== 2. the handshake ==");
        using var identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var hostEph = E2eHandshake.NewEphemeral();
        using var viewerEph = E2eHandshake.NewEphemeral();
        var K = RandomNumberGenerator.GetBytes(32);
        var vPub = E2eHandshake.PubOf(viewerEph);
        Check("a public key is 65 raw bytes, as WebCrypto's", Convert.FromBase64String(vPub) is { Length: 65 } raw && raw[0] == 4);
        var answer = E2eHandshake.Answer(Sid, hostEph, K, vPub, 1, identity)!;
        var got = E2eHandshake.Accept(Sid, viewerEph, answer, out var why);
        Check("the viewer opens the key the host wrapped for it", got != null && got.Key.SequenceEqual(K) && got.SenderId == 1, why);
        Check("signed by the machine's identity, which it reports", got is { Signed: true } && got.IdentityPub == E2eHandshake.PubOf(identity));
        Check("the key is not in the answer in the clear", !Json.Serialize(answer).Contains(Convert.ToBase64String(K)));

        using var otherEph = E2eHandshake.NewEphemeral();
        Check("an answer for another viewer is not taken", E2eHandshake.Accept(Sid, otherEph, answer, out why) == null && why.Contains("not for this viewer"), why);
        Check("an answer from another session does not verify", E2eHandshake.Accept("session-other", viewerEph, answer, out why) == null, why);
        var renumbered = Json.Deserialize<E2eKeyMsg>(Json.Serialize(answer))!; renumbered.SenderId = 0;
        Check("a sender id changed by the server (to impersonate the host) does not verify", E2eHandshake.Accept(Sid, viewerEph, renumbered, out why) == null, why);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var swapped = Json.Deserialize<E2eKeyMsg>(Json.Serialize(answer))!; swapped.IdentityPub = E2eHandshake.PubOf(stranger);
        Check("another identity named on the same signature does not verify", E2eHandshake.Accept(Sid, viewerEph, swapped, out why) == null, why);
        // A server pretending to be the host: its own host key, wrapped for the viewer, signed by its own identity.
        using var fakeHost = E2eHandshake.NewEphemeral();
        var forged = E2eHandshake.Answer(Sid, fakeHost, RandomNumberGenerator.GetBytes(32), vPub, 1, stranger)!;
        var forgedGot = E2eHandshake.Accept(Sid, viewerEph, forged, out _);
        Check("a server answering as the host CAN only do it under its own identity — which the pin then catches",
            forgedGot != null && forgedGot.IdentityPub != E2eHandshake.PubOf(identity));
        var unsigned = E2eHandshake.Answer(Sid, hostEph, K, vPub, 2, null)!;
        var ug = E2eHandshake.Accept(Sid, viewerEph, unsigned, out why);
        Check("an unsigned answer (no identity) still opens, and says it was not signed", ug is { Signed: false } && ug.Key.SequenceEqual(K), why);
        var halfSigned = Json.Deserialize<E2eKeyMsg>(Json.Serialize(answer))!; halfSigned.Signature = "";
        Check("an identity named with its signature removed is refused, not taken as unsigned", E2eHandshake.Accept(Sid, viewerEph, halfSigned, out why) == null, why);
        Check("a fingerprint is eight groups of four", E2eHandshake.Fingerprint(E2eHandshake.PubOf(identity)).Split(' ') is { Length: 8 } g && g.All(x => x.Length == 4));

        // ---- 3. the seal -------------------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("== 3. the seal ==");
        var host = new SessionSeal(Sid); host.UseKey(K, 0);
        var viewer = new SessionSeal(Sid); viewer.UseKey(K, 1);
        const string Secret = "Tr0ub4dor&3-the-password";
        var frame = host.SealMessage(new ClipboardMsg { Text = Secret });
        Check("a sealed message: kind 7, its tag visible, nothing of the content readable",
            frame[0] == BinKind.Sealed && SessionSeal.TagOfFrame(frame) == "clipboard" && !Encoding.UTF8.GetString(frame).Contains(Secret) && !Encoding.UTF8.GetString(frame).Contains("Tr0ub"));
        Check("opened by a viewer with the key", viewer.OpenMessage(frame) is ClipboardMsg c && c.Text == Secret);
        Check("the same frame twice: the replay is refused", viewer.OpenMessage(frame) == null && viewer.Refused == 1);
        var a1 = host.SealMessage(new KeyMsg()); var a2 = host.SealMessage(new KeyMsg());
        Check("a newer frame opens; an OLDER one after it is refused", viewer.OpenMessage(a2) != null && viewer.OpenMessage(a1) == null);
        var vid = new byte[] { BinKind.Video, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var sv = host.SealBinary(vid);
        Check("a sealed picture keeps its kind visible with the sealed bit", sv[0] == (BinKind.Video | BinKind.SealedBit) && BinKind.Of(sv[0]) == BinKind.Video);
        Check("and opens back to exactly the frame", viewer.OpenBinary(sv) is { } ov && ov.SequenceEqual(vid));
        var bent = host.SealMessage(new MouseMsg { X = 5 }); bent[^20] ^= 1;
        Check("one bit altered: refused", viewer.OpenMessage(bent) == null);
        var relabelled = host.SealMessage(new ScriptRunMsg { Script = "whoami" });
        var asMouse = Relabel(relabelled, "mouse");
        Check("the visible tag rewritten by the server: refused (the tag is in the associated data)", viewer.OpenMessage(asMouse) == null);
        var otherSession = new SessionSeal("session-other"); otherSession.UseKey(K, 0);
        Check("a frame from another session under the same key: refused", viewer.OpenMessage(otherSession.SealMessage(new MouseMsg())) == null);
        var wrong = new SessionSeal(Sid); wrong.UseKey(RandomNumberGenerator.GetBytes(32), 0);
        Check("the wrong key: refused", viewer.OpenMessage(wrong.SealMessage(new MouseMsg())) == null);
        var unkeyed = new SessionSeal(Sid);
        Check("an end with no key opens nothing", unkeyed.OpenMessage(host.SealMessage(new MouseMsg())) == null && !unkeyed.Keyed);

        // Built by hand, with the session key, as a visitor holding it could.
        var hostSide = new SessionSeal(Sid); hostSide.UseKey(K, 0);
        var lie = Handmade(K, 7, 1, "mouse", Json.Serialize<Msg>(new ScriptRunMsg { Script = "format c:" }));
        Check("(the hand-built frame is well formed: an honest one opens)", hostSide.OpenMessage(Handmade(K, 7, 2, "mouse", Json.Serialize<Msg>(new MouseMsg { X = 9 }))) is MouseMsg { X: 9 });
        Check("a tag that LIES (a script dressed as a mouse movement, sealed properly): refused", hostSide.OpenMessage(lie) == null);
        var smuggled = Handmade(K, 7, 3, "chat", Json.Serialize<Msg>(new ChatMsg { Text = "hi" }));
        Check("a clear type sent sealed (past the server's handling): refused", hostSide.OpenMessage(smuggled) == null);

        // ---- 4. the pins ---------------------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("== 4. the pins ==");
        var pinFile = Path.Combine(Path.GetTempPath(), "rs-e2ecore-pins-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var idA = E2eHandshake.PubOf(identity); var idB = E2eHandshake.PubOf(stranger);
            var pins = new MachinePins(pinFile);
            Check("a machine never seen, signed: first time", pins.Check("dev-1", "SUE-PC", "Smith Ltd", idA).Verdict == MachinePins.Verdict.FirstTime);
            Check("never seen, unsigned: encrypted, identity not checked", pins.Check("dev-1", "SUE-PC", "Smith Ltd", "").Verdict == MachinePins.Verdict.NoIdentity);
            pins.Remember("dev-1", "SUE-PC", "Smith Ltd", idA);
            Check("remembered: the same key matches", pins.Check("dev-1", "SUE-PC", "Smith Ltd", idA).Verdict == MachinePins.Verdict.Matches);
            Check("a different key for the same device: CHANGED", pins.Check("dev-1", "SUE-PC", "Smith Ltd", idB) is { Verdict: MachinePins.Verdict.Changed, Previous.IdentityPub: var p } && p == idA);
            Check("a NEW device id under the same name and customer, another key: CHANGED (a reinstall, or an impostor)",
                pins.Check("dev-2", "sue-pc", "Smith Ltd", idB).Verdict == MachinePins.Verdict.Changed);
            Check("the same name at ANOTHER customer is another machine: first time", pins.Check("dev-3", "SUE-PC", "Jones & Co", idB).Verdict == MachinePins.Verdict.FirstTime);
            Check("a pinned machine answering unsigned: held, like a change", pins.Check("dev-1", "SUE-PC", "Smith Ltd", "").Verdict == MachinePins.Verdict.Unsigned);
            var again = new MachinePins(pinFile);
            Check("kept on disk: a new console session still knows it", again.Check("dev-1", "SUE-PC", "Smith Ltd", idA).Verdict == MachinePins.Verdict.Matches);
            again.Remember("dev-2", "SUE-PC", "Smith Ltd", idB);
            Check("the technician accepts the change: the new key matches under both ids, the old is gone",
                again.Check("dev-2", "SUE-PC", "Smith Ltd", idB).Verdict == MachinePins.Verdict.Matches
                && again.Check("dev-1", "SUE-PC", "Smith Ltd", idA).Verdict == MachinePins.Verdict.Changed);
            File.WriteAllText(pinFile, "{ not json");
            Check("a damaged file forgets — first contact again, never a wrong trust", new MachinePins(pinFile).Check("dev-2", "SUE-PC", "Smith Ltd", idB).Verdict == MachinePins.Verdict.FirstTime);
        }
        finally { try { File.Delete(pinFile); } catch { } }

        // ---- 5. two real WebSockets --------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("== 5. WsConnection with seals ==");
        var (left, right) = await Pair();
        var relayL = await Pair();   // a third end with NO seal, standing in for the server's relay
        var hostWs = new WsConnection(left); var viewWs = new WsConnection(right);
        hostWs.Seal = new SessionSeal(Sid) { Sealing = true }; hostWs.Seal.UseKey(K, 0);
        viewWs.Seal = new SessionSeal(Sid) { Sealing = true }; viewWs.Seal.UseKey(K, 1);
        var gotMsgs = new List<Msg>(); var gotBin = new List<byte[]>();
        viewWs.OnMessage += m => { lock (gotMsgs) gotMsgs.Add(m); };
        viewWs.OnBinary += b => { lock (gotBin) gotBin.Add(b.ToArray()); };
        _ = hostWs.RunAsync(); _ = viewWs.RunAsync();
        hostWs.Send(new ClipboardMsg { Text = Secret });
        hostWs.Send(new ChatMsg { Text = "clear on purpose" });
        hostWs.SendBinary(vid);
        await Task.Delay(500);
        Check("content sealed at one end arrives opened at the other", gotMsgs.OfType<ClipboardMsg>().Any(x => x.Text == Secret) && gotBin.Any(b => b.SequenceEqual(vid)));
        Check("the clear list travels clear and is not counted as clear content", gotMsgs.OfType<ChatMsg>().Any() && viewWs.Seal.ClearContent == 0);
        Check("and what arrived was opened (the badge's evidence)", viewWs.Seal.Opened >= 2);

        // The server's end: no seal. A sealed frame must pass through it untouched.
        var srvIn = new WsConnection(relayL.Item1); var srvOut = new WsConnection(relayL.Item2);
        byte[]? relayed = null;
        srvOut.OnBinary += b => relayed = b.ToArray();
        _ = srvIn.RunAsync(); _ = srvOut.RunAsync();
        var sealedFrame = host.SealBinary(vid);
        srvIn.SendBinary(sealedFrame);
        await Task.Delay(300);
        Check("an end with no seal (the server) passes a sealed frame on untouched", relayed != null && relayed.SequenceEqual(sealedFrame));

        // A host that is sealing refuses clear content: it could only have come from the server.
        hostWs.Seal.RefuseClearContent = true;
        var atHost = new List<Msg>();
        hostWs.OnMessage += m => { lock (atHost) atHost.Add(m); };
        viewWs.Seal.Sealing = false;
        viewWs.Send(new MouseMsg { X = 77 });            // clear: refused at the host
        viewWs.Send(new ChatMsg { Text = "still clear" }); // a clear type: fine
        viewWs.Seal.Sealing = true;
        viewWs.Send(new MouseMsg { X = 88 });            // sealed: opened
        await Task.Delay(500);
        Check("a host sealing REFUSES clear content, takes clear-list messages and sealed content",
            !atHost.OfType<MouseMsg>().Any(x => x.X == 77) && atHost.OfType<ChatMsg>().Any() && atHost.OfType<MouseMsg>().Any(x => x.X == 88));
        // A console is never refused content: it SHOWS that it came clear.
        hostWs.Seal.Sealing = false;
        bool flagged = false; viewWs.Seal.ClearContentArrived += () => flagged = true;
        hostWs.Send(new ClipboardMsg { Text = "plain" });
        await Task.Delay(400);
        Check("a console shown clear content takes it but marks the session (the badge)", viewWs.Seal.ClearContent > 0 && flagged
            && gotMsgs.OfType<ClipboardMsg>().Any(x => x.Text == "plain"));

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>A sealed message frame with its tag rewritten, as a server could.</summary>
    private static byte[] Relabel(byte[] frame, string tag)
    {
        int oldLen = frame[1];
        var t = Encoding.UTF8.GetBytes(tag);
        var rest = frame.AsSpan(2 + oldLen).ToArray();
        return new[] { BinKind.Sealed, (byte)t.Length }.Concat(t).Concat(rest).ToArray();
    }

    /// <summary>The frame format worked out here, independently of SessionSeal.</summary>
    private static byte[] Handmade(byte[] key, uint sender, ulong counter, string tag, string json)
    {
        var t = Encoding.UTF8.GetBytes(tag);
        var header = new byte[2 + t.Length]; header[0] = BinKind.Sealed; header[1] = (byte)t.Length; t.CopyTo(header, 2);
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(nonce, sender); BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        var plain = Encoding.UTF8.GetBytes(json);
        var ct = new byte[plain.Length]; var tg = new byte[16];
        using var g = new AesGcm(key, 16);
        g.Encrypt(nonce, plain, ct, tg, header.Concat(Encoding.UTF8.GetBytes(Sid)).ToArray());
        return header.Concat(nonce).Concat(ct).Concat(tg).ToArray();
    }

    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
        return d?.FullName;
    }

    /// <summary>Two connected WebSockets over loopback TCP, framed by .NET itself (no HTTP needed).</summary>
    private static async Task<(WebSocket, WebSocket)> Pair()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var client = new TcpClient();
        var accept = l.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)l.LocalEndpoint).Port);
        var server = await accept; l.Stop();
        return (WebSocket.CreateFromStream(server.GetStream(), true, null, TimeSpan.FromSeconds(30)),
                WebSocket.CreateFromStream(client.GetStream(), false, null, TimeSpan.FromSeconds(30)));
    }
}
