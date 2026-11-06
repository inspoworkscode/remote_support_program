using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// Does a tenant actually get warned before its trial lapses?
//
// Answered on THIS machine alone, against a throwaway database in the temp folder, with a REAL
// SMTP server — a minimal one, listening on a local port, that accepts a message and keeps it.
// Clint's live server on 8090 and his real database are never touched.
//
// Why it sends real mail to a real socket rather than stubbing the send: the thing most likely to
// be wrong is not the arithmetic, it is the composing and the delivery. A test that asserts "the
// service decided to send" would have passed just as happily with an unroutable From address or a
// body that never mentions what actually happens. This one reads the message that arrived.
//
// What it covers, and what each would catch:
//
//   1. NOTHING AT 8 DAYS. A trial with more than a week left must produce silence. A warning that
//      fires on day one of a 14-day trial is read as marketing and then ignored on day thirteen.
//   2. THE 7-DAY NOTICE. It goes to EVERY admin, not just the creator, and says what will happen
//      rather than that something is ending.
//   3. IT ONLY GOES ONCE. The pass runs hourly; a notice that re-sends every hour is worse than
//      no notice.
//   4. THE 1-DAY NOTICE, and then the ON-EXPIRY one, each once.
//   5. A MAIL FAILURE DOES NOT CONSUME THE WARNING. With the mailbox pointed at a dead port the
//      notice must NOT be recorded as sent, or a mail outage silently eats the one warning that
//      mattered. This is the check that would catch the tempting "mark it sent and move on".
//
//   dotnet run -c Release --project tools/trialnoticecheck/trialnoticecheck.csproj -- [--server <dir>]
// ---------------------------------------------------------------------------------------------

const int SmtpPort = 32525;
string? serverDir = null;
for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--server") serverDir = args[i + 1];

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-trialnotice-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var dataDir = Path.Combine(work, "data");
Directory.CreateDirectory(dataDir);

if (serverDir == null)
{
    serverDir = Path.Combine(work, "server");
    Log($"building the platform into {serverDir} (the live server's bin is left alone)");
    var b = Run("dotnet", $"build -c Release \"{Path.Combine(repo, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj")}\" -v q --nologo -p:OutDir=\"{serverDir}\\\"", repo, 300000);
    if (b.code != 0) { Console.WriteLine(b.output); return 2; }
}
var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");
if (!File.Exists(exe)) { Console.WriteLine($"no RemoteSupport.Platform.exe in {serverDir}"); return 2; }

var env = new Dictionary<string, string> { ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false", ["RSP_Platform__PublicUrl"] = "http://localhost:8098" };
HarnessDb.Use(env, dataDir);
var dbPath = Path.Combine(dataDir, "platform.db");

// ---- the pretend mail server ------------------------------------------------------------
var sink = new SmtpSink(SmtpPort);
sink.Start();
Log($"SMTP sink listening on {SmtpPort}");

// ---- a throwaway tenant, two admins and a technician who must NOT be written to ----------
Log("creating a throwaway tenant");
foreach (var cmd in new[]
{
    "create-tenant \"Trial Local\" trial-local 5",
    "approve trial-local",
    "add-tech trial-local boss@local.test localtest1 \"The Boss\" --admin",
    "add-tech trial-local second@local.test localtest2 \"Second Admin\" --admin",
    "add-tech trial-local techie@local.test localtest3 \"A Technician\"",
})
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); sink.Stop(); return 2; }
}
// Point the platform mailbox at the sink. No SSL and no password: the sink speaks plain SMTP,
// and the password path is exercised by the owner page rather than here.
var setMail = Run(exe, $"platform-mail set --host 127.0.0.1 --port {SmtpPort} --from noreply@local.test --from-name \"The Remote Support Program\" --nossl", serverDir, 60000, env);
if (setMail.code != 0) { Console.WriteLine(setMail.output); sink.Stop(); return 2; }

// ---- helpers ----------------------------------------------------------------------------
void SetDaysLeft(double days)
{
    // Straight into the database: the trial end is set by approval and there is deliberately no CLI to
    // move it, because nothing in the product should. A test is allowed to.
    using var c = HarnessDb.Connect($"Data Source={dbPath}");
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = HarnessDb.Portable("UPDATE Tenants SET TrialEndsUtc = $e WHERE Slug = 'trial-local'");
    // Stored the way EF writes a DateTime to SQLite, so it reads back identically; PostgreSQL takes the DateTime itself.
    cmd.With("@e", HarnessDb.Postgres ? (object)DateTime.UtcNow.AddDays(days) : DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd HH:mm:ss.fffffff"));
    cmd.ExecuteNonQuery();
}
string NoticesSent()
{
    using var c = HarnessDb.Connect($"Data Source={dbPath}");
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = HarnessDb.Portable("SELECT TrialNoticesSent FROM Tenants WHERE Slug = 'trial-local'");
    return cmd.ExecuteScalar() as string ?? "";
}
(int code, string output) RunPass() => Run(exe, "trial-notices", serverDir, 120000, env);

try
{
    // =====================================================================================
    Console.WriteLine();
    Log("CHECK 1: more than a week left — say nothing");
    SetDaysLeft(8);
    RunPass();
    await Task.Delay(400);
    Check("no notice is sent 8 days out", sink.Messages.Count == 0,
          sink.Messages.Count == 0 ? "silence, as it should be" : $"{sink.Messages.Count} message(s) went out too early");
    Check("...and nothing is recorded as sent", NoticesSent().Length == 0, $"TrialNoticesSent = \"{NoticesSent()}\"");

    // =====================================================================================
    Console.WriteLine();
    Log("CHECK 2: seven days left");
    sink.Messages.Clear();
    SetDaysLeft(6.5);                       // inside the 7-day milestone
    RunPass();
    await Task.Delay(400);
    Check("a notice is sent at the 7-day mark", sink.Messages.Count == 1, $"{sink.Messages.Count} message(s)");
    var m = sink.Messages.FirstOrDefault();
    if (m != null)
    {
        Check("it goes to BOTH admins", m.Recipients.Contains("boss@local.test") && m.Recipients.Contains("second@local.test"),
              string.Join(", ", m.Recipients));
        Check("...and NOT to the ordinary technician", !m.Recipients.Contains("techie@local.test"),
              m.Recipients.Contains("techie@local.test") ? "a technician was emailed about billing" : "correctly left out");
        Check("the subject says the trial is ending", m.Data.Contains("trial ends", StringComparison.OrdinalIgnoreCase),
              FirstLine(m.Data, "Subject:"));
        // The wording is the point of the feature. "Your trial is ending" gets filed; "every
        // computer stops connecting" gets acted on.
        Check("the body says what actually happens to their machines",
              m.Data.Contains("refused", StringComparison.OrdinalIgnoreCase) || m.Data.Contains("stop connecting", StringComparison.OrdinalIgnoreCase),
              "it names the consequence rather than just the date");
        Check("the body says nothing is deleted", m.Data.Contains("deleted", StringComparison.OrdinalIgnoreCase));
    }
    Check("the 7-day notice is recorded", NoticesSent().Contains('7'), $"TrialNoticesSent = \"{NoticesSent()}\"");

    // =====================================================================================
    Console.WriteLine();
    Log("CHECK 3: the pass runs again an hour later");
    sink.Messages.Clear();
    RunPass();
    await Task.Delay(400);
    Check("the same notice is NOT sent twice", sink.Messages.Count == 0,
          sink.Messages.Count == 0 ? "silence" : "it would re-send every hour until the trial ended");

    // =====================================================================================
    Console.WriteLine();
    Log("CHECK 4: one day left, then the day it lapses");
    sink.Messages.Clear();
    SetDaysLeft(0.5);
    RunPass();
    await Task.Delay(400);
    Check("a notice is sent at the 1-day mark", sink.Messages.Count == 1, $"{sink.Messages.Count} message(s)");
    Check("it says tomorrow", sink.Messages.Any(x => x.Data.Contains("tomorrow", StringComparison.OrdinalIgnoreCase)));

    sink.Messages.Clear();
    SetDaysLeft(-1);
    RunPass();
    await Task.Delay(400);
    Check("a notice is sent once it has lapsed", sink.Messages.Count == 1, $"{sink.Messages.Count} message(s)");
    Check("...and it is in the past tense — the outage has already started",
          sink.Messages.Any(x => x.Data.Contains("has ended", StringComparison.OrdinalIgnoreCase) || x.Data.Contains("stopped working", StringComparison.OrdinalIgnoreCase)));
    Check("all three milestones are recorded", "710".All(ch => NoticesSent().Replace(",", "").Contains(ch)),
          $"TrialNoticesSent = \"{NoticesSent()}\"");

    sink.Messages.Clear();
    RunPass();
    await Task.Delay(400);
    Check("nothing more is sent after all three have gone", sink.Messages.Count == 0);

    // =====================================================================================
    // The one that matters most, and the one a tempting shortcut breaks.
    Console.WriteLine();
    Log("CHECK 5: a mail failure must NOT consume the warning");
    var fresh = Run(exe, "create-tenant \"Trial Broken\" trial-broken 5", serverDir, 60000, env);
    Run(exe, "approve trial-broken", serverDir, 60000, env);
    Run(exe, "add-tech trial-broken boss@broken.test localtest4 \"Boss\" --admin", serverDir, 60000, env);
    if (fresh.code != 0) Check("a second tenant could be created", false);
    else
    {
        using (var c = HarnessDb.Connect($"Data Source={dbPath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = HarnessDb.Portable("UPDATE Tenants SET TrialEndsUtc = $e WHERE Slug = 'trial-broken'");
            cmd.With("@e", HarnessDb.Postgres ? (object)DateTime.UtcNow.AddDays(0.5) : DateTime.UtcNow.AddDays(0.5).ToString("yyyy-MM-dd HH:mm:ss.fffffff"));
            cmd.ExecuteNonQuery();
        }
        // Point the mailbox at a port nothing is listening on.
        Run(exe, "platform-mail set --host 127.0.0.1 --port 39999 --nossl", serverDir, 60000, env);
        RunPass();
        await Task.Delay(400);
        string broken;
        using (var c = HarnessDb.Connect($"Data Source={dbPath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = HarnessDb.Portable("SELECT TrialNoticesSent FROM Tenants WHERE Slug = 'trial-broken'");
            broken = cmd.ExecuteScalar() as string ?? "";
        }
        Check("a notice that could not be sent is NOT marked as sent", broken.Length == 0,
              broken.Length == 0 ? "it will be retried on the next pass" : $"recorded \"{broken}\" despite failing — the warning has been eaten");

        // ...and it really does go out once the mailbox works again.
        sink.Messages.Clear();
        Run(exe, $"platform-mail set --host 127.0.0.1 --port {SmtpPort} --nossl", serverDir, 60000, env);
        RunPass();
        await Task.Delay(400);
        Check("...and it is delivered once the mailbox works again", sink.Messages.Count >= 1,
              $"{sink.Messages.Count} message(s) after the mailbox was fixed");
    }
}
finally { sink.Stop(); }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"database and mail kept at {work}");
// SHOW_MAIL=1 prints what actually arrived. The assertions above check that the wording contains
// the things that matter; this is for reading the prose, which no assertion can judge.
if (Environment.GetEnvironmentVariable("SHOW_MAIL") == "1")
    foreach (var msg in sink.Messages)
        Console.WriteLine("\n----- message -----\n" + msg.Data);
return fail == 0 ? 0 : 1;

// =============================================================================================
static string FirstLine(string data, string prefix)
{
    var line = data.Split('\n').FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return line?.Trim() ?? "(no " + prefix + " line)";
}

static (int code, string output) Run(string file, string argLine, string cwd, int ms, Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(file, argLine) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit(ms);
    return (p.HasExited ? p.ExitCode : -1, o);
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "src", "RemoteSupport.Platform"))) return d.FullName;
    throw new InvalidOperationException("could not find the repository root");
}

sealed class ReceivedMail
{
    public List<string> Recipients { get; } = new();
    public string Data { get; set; } = "";
}

/// <summary>
/// Just enough SMTP to accept a message and keep it: greeting, EHLO, MAIL FROM, RCPT TO, DATA
/// terminated by a lone dot, QUIT. No TLS and no authentication, which is why the settings under
/// test point at it with --nossl and no password.
///
/// It exists so the notice is genuinely composed by MailMessage, serialised by SmtpClient and
/// read back off a socket. Stubbing the send would leave the two things most likely to be wrong —
/// the addresses and the wording — completely untested.
/// </summary>
sealed class SmtpSink
{
    private readonly TcpListener _listener;
    private CancellationTokenSource _cts = new();
    public List<ReceivedMail> Messages { get; } = new();

    public SmtpSink(int port) => _listener = new TcpListener(IPAddress.Loopback, port);

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => Serve(client));
            }
        });
    }

    public void Stop() { try { _cts.Cancel(); _listener.Stop(); } catch { } }

    private void Serve(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" })
            {
                var mail = new ReceivedMail();
                writer.WriteLine("220 sink ready");
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    {
                        // One capability line then the terminator; SmtpClient is happy with this.
                        writer.WriteLine("250-sink");
                        writer.WriteLine("250 8BITMIME");
                    }
                    else if (line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase)) writer.WriteLine("250 sink");
                    else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase)) writer.WriteLine("250 ok");
                    else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                    {
                        var a = line.IndexOf('<'); var b = line.IndexOf('>');
                        if (a >= 0 && b > a) mail.Recipients.Add(line[(a + 1)..b]);
                        writer.WriteLine("250 ok");
                    }
                    else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WriteLine("354 send it");
                        var body = new StringBuilder();
                        string? d;
                        while ((d = reader.ReadLine()) != null && d != ".") body.AppendLine(d);
                        mail.Data = body.ToString();
                        lock (Messages) Messages.Add(mail);
                        writer.WriteLine("250 queued");
                    }
                    else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase)) { writer.WriteLine("221 bye"); break; }
                    else if (line.StartsWith("RSET", StringComparison.OrdinalIgnoreCase)) writer.WriteLine("250 ok");
                    else writer.WriteLine("250 ok");
                }
            }
        }
        catch { /* a hung up client is not a failure of the thing being tested */ }
    }
}
