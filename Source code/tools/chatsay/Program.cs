// =====================================================================================
// chatsay - signs in to a TEST platform as one person's console and says one thing in office chat
// (stage 3.8.1), so a portal page or another console can be watched receiving it — the pop-up, the
// count, the browser notification — with nobody at a second machine.
//
//   set PORTALLINK_PASSWORD=...        (never an argument, so it is not in shell history)
//   dotnet run -c Debug --project tools/chatsay/chatsay.csproj -- <port> <email> <to> <text>
//
// <to> is a person's display name, or "" for Everyone. Console roles only, like portallink.
// =====================================================================================

using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;

if (args.Length < 4) { Console.WriteLine("chatsay <port> <email> <to, or \"\" for Everyone> <text>"); return 1; }
var port = int.Parse(args[0]);
var password = Environment.GetEnvironmentVariable("PORTALLINK_PASSWORD") ?? "";
using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email = args[1], password });
if (!lr.IsSuccessStatusCode) { Console.WriteLine("Sign-in refused: " + await lr.Content.ReadAsStringAsync()); return 1; }
var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}"), CancellationToken.None);
var conn = new WsConnection(ws);
var got = new TaskCompletionSource<StaffChatMsg>();
conn.OnMessage += m => { if (m is StaffChatMsg s && (s.Error.Length > 0 || s.Message?.Text == args[3])) got.TrySetResult(s); };
_ = conn.RunAsync(CancellationToken.None);
await Task.Delay(500);
conn.Send(new StaffChatSendMsg { To = args[2], Text = args[3] });
var done = await Task.WhenAny(got.Task, Task.Delay(10000));
if (done != got.Task) { Console.WriteLine("No answer came back."); return 1; }
var r = got.Task.Result;
Console.WriteLine(r.Error.Length > 0 ? "Refused: " + r.Error : $"Sent #{r.Message!.Id} to {(args[2].Length == 0 ? "Everyone" : args[2])}.");
await conn.CloseAsync();
return r.Error.Length > 0 ? 1 : 0;
