using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Why is a machine on the Unattended tab still asking the customer to click Allow?
//
// Written 2026-09-21 for Clint's report: "the unattended switch... switches between attended and
// unattended on the console, but does not allow for unattended access. the client still has to
// click on allow."
//
// Unattended access needs TWO things and the console only ever shows one of them. The tab a
// machine sits on is decided by the PERMISSION alone (DeviceList.Belongs reads UnattendedEnabled),
// deliberately, so the list holds still while agents come and go. But the hub will only hand a
// technician-initiated session to the agent when the permission AND a live agent connection are
// both there (TenantHub.StartSession, `mayUseAgent`). Without the agent it quietly falls back to
// the tray app, and the tray app's host is launched with --consent ask.
//
// So "Unattended tab, still asks for Allow" is exactly what a machine with no agent connection
// looks like from the console, and NOTHING in the console distinguishes it: the dot is
// AgentOnline OR ClientOnline, so the tray app alone keeps it green.
//
// This asks the live hub, as a technician, for the flags the console is carrying but not
// drawing — and then says, for each machine, what would actually happen if somebody connected.
//
// Read-only. It opens a technician socket, reads the first state message and closes. It starts
// no session, changes no permission and writes nothing.
//
//   dotnet run -c Release --project tools/agentcheck/agentcheck.csproj
//   dotnet run -c Release --project tools/agentcheck/agentcheck.csproj -- DESKTOP-OPHQIDH
// ---------------------------------------------------------------------------------------------

string http = Environment.GetEnvironmentVariable("RS_HTTP") ?? "http://localhost:8090";
string ws = http.StartsWith("https://") ? "wss://" + http[8..] : "ws://" + http[7..];
string? only = args.Length > 0 ? args[0] : null;

Console.WriteLine($"Asking the hub at {http} what it knows about each machine.");
Console.WriteLine();

string token;
try
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var creds = RemoteSupport.Tools.RsCredentials.Load();
    var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
    if (!lr.IsSuccessStatusCode)
    {
        Console.WriteLine($"Could not sign in ({(int)lr.StatusCode}). Is the platform running, and are the credentials in tools/rs-credentials.json right?");
        return 1;
    }
    token = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
}
catch (Exception ex)
{
    Console.WriteLine("Could not reach the platform: " + ex.Message);
    Console.WriteLine("Start it with:  dotnet run -c Release --project src/RemoteSupport.Platform");
    return 1;
}

var q = new ConcurrentQueue<Msg>();
using var sock = new ClientWebSocket();
await sock.ConnectAsync(new Uri($"{ws}/ws/tech?token={token}"), default);
var conn = new WsConnection(sock);
conn.OnMessage += m => q.Enqueue(m);
_ = conn.RunAsync();

StateMsg? state = null;
var until = DateTime.UtcNow.AddSeconds(10);
while (DateTime.UtcNow < until && state == null)
{
    while (q.TryDequeue(out var m)) if (m is StateMsg s) { state = s; break; }
    if (state == null) await Task.Delay(50);
}
await conn.CloseAsync("done");

if (state == null) { Console.WriteLine("The hub sent no state in ten seconds."); return 1; }

var devices = state.Devices
    .Where(d => only == null || d.MachineName.Contains(only, StringComparison.OrdinalIgnoreCase))
    .OrderBy(d => d.MachineName, StringComparer.OrdinalIgnoreCase)
    .ToList();

if (devices.Count == 0)
{
    Console.WriteLine(only == null ? "The hub knows no machines at all." : $"No machine whose name contains \"{only}\".");
    return 1;
}

// Widths from the data, so a long machine name cannot push the columns apart.
int w = Math.Max(12, devices.Max(d => d.MachineName.Length));
Console.WriteLine($"{"MACHINE".PadRight(w)}  TRAY APP  AGENT SVC  PERMISSION  CONSOLE TAB");
Console.WriteLine(new string('-', w + 45));
foreach (var d in devices)
    Console.WriteLine($"{d.MachineName.PadRight(w)}  {YesNo(d.ClientOnline),-8}  {YesNo(d.AgentOnline),-9}  {YesNo(d.UnattendedEnabled),-10}  {(d.UnattendedEnabled ? "Unattended" : "Attended")}");

Console.WriteLine();
Console.WriteLine("What would happen if a technician connected right now:");
Console.WriteLine();
foreach (var d in devices)
{
    Console.WriteLine($"  {d.MachineName}");
    // Mirrors TenantHub.StartSession: the agent is only offered when the permission is granted,
    // and whatever is left hosts it. The tray app's host always asks (Branding.RequireConsent).
    if (d.UnattendedEnabled && d.AgentOnline)
        Console.WriteLine("    THE AGENT HOSTS IT. No prompt on the customer's screen — this is working.");
    else if (d.UnattendedEnabled && !d.AgentOnline && d.ClientOnline)
        Console.WriteLine("    THE TRAY APP HOSTS IT, so the customer IS asked to click Allow.\n"
                        + "    The permission is granted but the agent service is not connected, which is\n"
                        + "    the half the console does not draw. THIS IS THE FAULT REPORTED.");
    else if (d.UnattendedEnabled && !d.AgentOnline && !d.ClientOnline)
        Console.WriteLine("    Nothing — the machine is offline. The console would say \"Device is offline\".");
    else if (!d.UnattendedEnabled && d.ClientOnline)
        Console.WriteLine("    The tray app hosts it and the customer is asked. Correct: they have not allowed unattended access.");
    else if (!d.UnattendedEnabled && d.AgentOnline)
        Console.WriteLine("    Refused: an agent is there but nobody has ticked the box. Correct.");
    else
        Console.WriteLine("    Nothing — the machine is offline.");
}

Console.WriteLine();
int broken = devices.Count(d => d.UnattendedEnabled && !d.AgentOnline && d.ClientOnline);
if (broken > 0)
    Console.WriteLine($"{broken} machine(s) say Unattended in the console and cannot actually do it.\n"
                    + "Next: run tools/agentstate.ps1 ON that machine to find out why its agent service is not connected.");
else
    Console.WriteLine("No machine is claiming unattended access it cannot deliver.");
return 0;

static string YesNo(bool b) => b ? "yes" : "NO";
