using System.Security.Cryptography;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// END-TO-END ENCRYPTION, C# AGAINST THE BROWSER (2026-10-07, HANDOVER §112). For the one pairing no harness can
// drive: the browser as HOST (browser support, wwwroot/e2e.js) and the console's C# as viewer.
//
//   e2einterop hello <dir>          a viewer key (kept in <dir>), its hello printed for the page
//   e2einterop open <dir> <answer>  the page's answer and a sealed frame (JSON from the page), opened here
//
// In the page:  const h = await rspE2e.host(sid).ready; const k = await h.answer(hello);
//               h.seal.sealing = true; ... seal a frame ... → { sid, key: k, frame: base64 }
// ---------------------------------------------------------------------------------------------
if (args.Length >= 2 && args[0] == "hello")
{
    using var eph = E2eHandshake.NewEphemeral();
    Directory.CreateDirectory(args[1]);
    File.WriteAllBytes(Path.Combine(args[1], "viewer.key"), eph.ExportPkcs8PrivateKey());
    Console.WriteLine(Json.Serialize<Msg>(new E2eHelloMsg { Pub = E2eHandshake.PubOf(eph) }));
    return 0;
}
if (args.Length >= 3 && args[0] == "open")
{
    using var eph = ECDiffieHellman.Create();
    eph.ImportPkcs8PrivateKey(File.ReadAllBytes(Path.Combine(args[1], "viewer.key")), out _);
    var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[2])).RootElement;
    var sid = doc.GetProperty("sid").GetString()!;
    var key = Json.Deserialize<Msg>(doc.GetProperty("key").GetRawText()) as E2eKeyMsg;
    var got = key == null ? null : E2eHandshake.Accept(sid, eph, key, out var why0);
    Console.WriteLine($"accepted: {got != null}, signed: {got?.Signed}");
    if (got == null) return 1;
    var seal = new SessionSeal(sid); seal.UseKey(got.Key, got.SenderId);
    var frame = Convert.FromBase64String(doc.GetProperty("frame").GetString()!);
    var msgFrame = Convert.FromBase64String(doc.GetProperty("msg").GetString()!);
    var bin = seal.OpenBinary(frame);
    var msg = seal.OpenMessage(msgFrame);
    Console.WriteLine($"binary opened: {(bin == null ? "NO" : string.Join(",", bin))}");
    Console.WriteLine($"message opened: {(msg is ClipboardMsg c ? "clipboard '" + c.Text + "'" : msg?.GetType().Name ?? "NO")}");
    // And the other way: a frame sealed HERE with the browser's key, for the page to open.
    var back = new SessionSeal(sid); back.UseKey(got.Key, 9);
    Console.WriteLine("for the page: " + Convert.ToBase64String(back.SealMessage(new SubscribeMsg { Monitor = 0, On = true })));
    return bin != null && msg != null ? 0 : 1;
}
Console.WriteLine("e2einterop hello <dir> | open <dir> <page.json>");
return 2;
