// =====================================================================================
// portallink - asks a platform for a one-use portal link, exactly the way the console's Tickets
// button does, and prints it. For opening the portal as a given person on a TEST platform
// without anybody typing a password into a browser.
//
//   set PORTALLINK_PASSWORD=...        (never an argument, so it is not in shell history)
//   dotnet run -c Debug --project tools/portallink/portallink.csproj -- <port> <email> [path]
//
// Console roles only (technician, supervisor, owner/supervisor) — the same people the console
// itself admits. The link lasts a minute and works once.
// =====================================================================================

using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;

if (args.Length < 2) { Console.WriteLine("portallink <port> <email> [path]"); return 1; }
var port = int.Parse(args[0]);
var password = Environment.GetEnvironmentVariable("PORTALLINK_PASSWORD") ?? "";
using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email = args[1], password });
if (!lr.IsSuccessStatusCode) { Console.WriteLine("Sign-in refused: " + await lr.Content.ReadAsStringAsync()); return 1; }
var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}"), CancellationToken.None);
var conn = new WsConnection(ws);
var got = new TaskCompletionSource<PortalLinkMsg>();
conn.OnMessage += m => { if (m is PortalLinkMsg p) got.TrySetResult(p); };
_ = conn.RunAsync(CancellationToken.None);
await Task.Delay(500);
conn.Send(new PortalLinkRequestMsg { Path = args.Length > 2 ? args[2] : "" });
var done = await Task.WhenAny(got.Task, Task.Delay(10000));
if (done != got.Task) { Console.WriteLine("No link came back."); return 1; }
var msg = got.Task.Result;
if (!string.IsNullOrEmpty(msg.Error)) { Console.WriteLine(msg.Error); return 1; }
Console.WriteLine($"http://127.0.0.1:{port}{msg.Url}");
return 0;
