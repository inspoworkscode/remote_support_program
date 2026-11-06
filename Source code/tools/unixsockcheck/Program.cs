using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RemoteSupport.Platform.Services;

// ---------------------------------------------------------------------------------------------
// unixsockcheck — Caddy reaches the platform through a Unix socket (2026-10-13, BUGS #12).
//
// Over TCP to 127.0.0.1:8090 every proxied WebSocket held one of Linux's ~28,000 local ports, and the xneelo load
// test (HANDOVER §126) ran out at ~27 companies: then EVERY request was answered 502. A socket uses no ports. What
// can be proved on this PC (Kestrel will not take a socket from Urls on Windows; the socket itself is checked by
// go-live.sh on the server, as the caddy user, and by the load test):
//
//   WHO IS CALLING   a connection through a socket has NO remote address. The platform's own forwarded-headers
//                    settings (UnixSocket.TrustCaddy, the very ones Program.cs uses) must still take the caller's
//                    address and scheme from Caddy's headers — or every caller is "?", ONE address for the sign-in
//                    guard, and thirty wrong passwords from anybody lock out everybody. The control: without the
//                    header, "?" is exactly what it would be. And from a real address that is not loopback, a
//                    forwarded header is still IGNORED (nobody but Caddy may say who is calling).
//   THE SOCKET       found in go-live's Urls; and go-live.sh / the unit file wire it: Kestrel listens on it, BOTH
//                    Caddy lines proxy through it and none through the port, caddy joins group rsp before Caddy
//                    restarts, systemd makes /run/rsp 0750, and step 7 asks the socket AS caddy.
//
//   dotnet run -c Release --project tools/unixsockcheck/unixsockcheck.csproj
// ---------------------------------------------------------------------------------------------

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) pass++; else fail++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
}
// The platform's own key for "who is calling" (Program.cs From, the sign-in guard, website chat, guests).
string From(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "?";

var opts = new ForwardedHeadersOptions();
UnixSocket.TrustCaddy(opts);
var mw = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(opts));

async Task<HttpContext> Arrive(IPAddress? remote, string? forwardedFor, string? proto = "https")
{
    var c = new DefaultHttpContext();
    c.Connection.RemoteIpAddress = remote;
    c.Request.Scheme = "http";
    if (forwardedFor != null) c.Request.Headers["X-Forwarded-For"] = forwardedFor;
    if (forwardedFor != null && proto != null) c.Request.Headers["X-Forwarded-Proto"] = proto;
    await mw.Invoke(c);
    return c;
}

Console.WriteLine("unixsockcheck — Caddy through a Unix socket");
Console.WriteLine();
Console.WriteLine("== who is calling, through the socket (no remote address at all) ==");
var a = await Arrive(null, "203.0.113.7");
Check("Caddy's X-Forwarded-For is the caller", From(a) == "203.0.113.7", From(a));
Check("and X-Forwarded-Proto makes it https (Secure cookies, HSTS)", a.Request.Scheme == "https", a.Request.Scheme);
var b = await Arrive(null, "198.51.100.20");
Check("two callers through the same socket are two addresses to the sign-in guard", From(b) == "198.51.100.20" && From(a) != From(b), $"{From(a)} / {From(b)}");
var none = await Arrive(null, null);
Check("control: without Caddy's header a socket caller is \"?\" — the one shared address this guards against", From(none) == "?", From(none));

Console.WriteLine();
Console.WriteLine("== the old way still right: TCP from Caddy on loopback, and nobody else ==");
var tcp = await Arrive(IPAddress.Loopback, "203.0.113.8");
Check("from 127.0.0.1 (Caddy over TCP, and the local checks) the header is taken", From(tcp) == "203.0.113.8", From(tcp));
var spoof = await Arrive(IPAddress.Parse("198.51.100.4"), "203.0.113.9");
Check("from anywhere else a header claiming to forward is IGNORED", From(spoof) == "198.51.100.4", From(spoof));

Console.WriteLine();
Console.WriteLine("== the socket ==");
var urls = "http://127.0.0.1:8090;http://unix:/run/rsp/platform.sock".Split(';');
var paths = UnixSocket.PathsIn(urls).ToList();
Check("go-live's Urls name exactly one socket, /run/rsp/platform.sock", paths.Count == 1 && paths[0] == "/run/rsp/platform.sock", string.Join(", ", paths));
Check("and the port alone names none", !UnixSocket.PathsIn(new[] { "http://127.0.0.1:8090" }).Any());

var root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "RemoteSupport.sln"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
var goLive = File.ReadAllText(Path.Combine(root, "deploy", "go-live.sh"));
var unit = File.ReadAllText(Path.Combine(root, "deploy", "remotesupport-platform.service"));
Check("go-live: SOCK=/run/rsp/platform.sock", goLive.Contains("\nSOCK=/run/rsp/platform.sock\n"));
Check("go-live: the platform listens on the port AND the socket", goLive.Contains("Environment=RSP_Urls=http://127.0.0.1:$PORT;http://unix:$SOCK"));
var proxies = goLive.Split('\n').Where(l => l.Contains("reverse_proxy ") && !l.TrimStart().StartsWith('#')).ToList();
Check("go-live: both Caddy lines proxy through the socket, none through the port",
      proxies.Count == 2 && proxies.All(l => l.Contains("reverse_proxy unix/$SOCK")), string.Join(" | ", proxies.Select(l => l.Trim())));
int join = goLive.IndexOf("usermod -aG \"$USER_NAME\" caddy", StringComparison.Ordinal), restart = goLive.IndexOf("systemctl restart caddy", StringComparison.Ordinal);
Check("go-live: caddy joins group rsp BEFORE Caddy restarts (groups are read at start)", join > 0 && restart > join);
// One cut per deploy, not two (2026-10-26): the platform, stopped in step 2, starts only once Caddy is back, and a
// trap starts it should anything in between fail.
int stop = goLive.IndexOf("systemctl stop \"$SERVICE\"", StringComparison.Ordinal), start = goLive.IndexOf("systemctl start \"$SERVICE\"\n", StringComparison.Ordinal),
    trap = goLive.IndexOf("trap 'systemctl start \"$SERVICE\" || true' EXIT", StringComparison.Ordinal);
Check("go-live: the platform starts AFTER Caddy restarts, never before", !goLive.Contains("enable --now \"$SERVICE\"") && stop > 0 && restart > stop && start > restart,
      $"stop {stop}, caddy {restart}, start {start}");
Check("go-live: a trap starts the platform if a step between its stop and its start fails", trap > stop && trap < restart && goLive.IndexOf("trap - EXIT", StringComparison.Ordinal) > start);
Check("go-live: step 7 asks the socket AS caddy", goLive.Contains("runuser -u caddy -- curl") && goLive.Contains("--unix-socket \"$SOCK\""));
Check("the unit: systemd makes /run/rsp for it, 0750", unit.Contains("\nRuntimeDirectory=rsp") && unit.Contains("\nRuntimeDirectoryMode=0750"));
Check("go-live.sh is plain ASCII where it changed (no stray dashes in the new lines)",
      goLive.Split('\n').Where(l => l.Contains("SOCK") || l.Contains("usermod")).All(l => l.All(ch => ch < 128)));
try
{
    var p = Process.Start(new ProcessStartInfo("bash", "-n \"" + Path.Combine(root, "deploy", "go-live.sh").Replace('\\', '/') + "\"") { RedirectStandardError = true, UseShellExecute = false })!;
    var err = p.StandardError.ReadToEnd(); p.WaitForExit();
    Check("go-live.sh parses in bash", p.ExitCode == 0, err.Trim());
}
catch (Exception ex) { Check("go-live.sh parses in bash (bash not found)", false, ex.Message); }

Console.WriteLine();
Console.WriteLine($"unixsockcheck: {pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
