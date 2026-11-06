using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// AUTO-UPDATE ON THE MACHINE (2026-10-16, stage D: Shared/UpdateSwap.cs, UpdateRunner.cs, Agent/AgentUpdate.cs).
//
// The parts that need no signed build, driven for real:
//   SWAP     a running program renamed aside and the new one put in; only the release's own files touched (never
//            branding\, device.json, a script the release does not carry); undone exactly — even when it failed half-way;
//            and a .NET program whose file was renamed still knows its path as the ORIGINAL name, so the tray app's
//            restart starts the NEW one.
//   RUNNER   a download from the machine's own server (a broken one carried on), its size and hash, every file checked
//            before anything is installed — one refusal and nothing is, and the version is never taken again; waits
//            for nobody connected; never another server's address, never an older version.
//   APPLY    the new agent comes back → kept; it does not → every file back and the old agent started again; the agent
//            that cannot be stopped → nothing changed; whichever agent runs next reports it, once.
//   REAL     the runner with the REAL checks refuses an unsigned release.
// The rest — the service stopping and starting, our signature on a stamped build — is the hardware test (TEST-3.6.md DE).
//
//   dotnet run -c Release --project tools/updateapply/updateapply.csproj
// ---------------------------------------------------------------------------------------------

internal static class UpdateApplyCheck
{
    private const string V1 = "2026.10.16.1400", V2 = "2026.10.16.1500";
    private static int _pass, _fail;
    private static string _work = "";

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }
    private static void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

    private static async Task<int> Main(string[] args)
    {
        // The child for the "renamed while running" check: waits for the signal, then says what it thinks its path is.
        if (args.Length == 3 && args[0] == "--say-path")
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(args[1]) && DateTime.UtcNow < until) Thread.Sleep(50);
            File.WriteAllText(args[2], Environment.ProcessPath ?? "");
            return 0;
        }

        _work = Path.Combine(Path.GetTempPath(), "rs-updateapply-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(_work);
        try
        {
            Swap();
            ProcessPathAfterRename();
            await Runner();
            Applier();
            await RealChecks();
        }
        finally
        {
            foreach (var p in _started) try { if (!p.HasExited) p.Kill(); } catch { }
            await Task.Delay(300);
            try { Directory.Delete(_work, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    private static readonly List<Process> _started = new();

    // ------------------------------------------------------------------ SWAP

    /// <summary>An install folder as a machine has it: the release's files, and things that are the machine's own.</summary>
    private static string Install(string name)
    {
        var dir = Path.Combine(_work, name);
        Directory.CreateDirectory(Path.Combine(dir, "branding"));
        Directory.CreateDirectory(Path.Combine(dir, "healthcheck"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), Path.Combine(dir, "RemoteSupport.Client.exe"));
        File.WriteAllText(Path.Combine(dir, "RemoteSupport.Host.exe"), "old host");
        File.WriteAllText(Path.Combine(dir, "RemoteSupport.Agent.exe"), "old agent");
        File.WriteAllText(Path.Combine(dir, "Install-Client.ps1"), "old install");
        File.WriteAllText(Path.Combine(dir, "Uninstall-Client.ps1"), "old uninstall");
        File.WriteAllText(Path.Combine(dir, "branding", "brand.json"), "{\"CompanyName\":\"The machine's own\"}");
        File.WriteAllText(Path.Combine(dir, "device.json"), "the machine's identity");
        File.WriteAllText(Path.Combine(dir, "config.json"), "the health check's kept settings");
        File.WriteAllText(Path.Combine(dir, "healthcheck", "Get-ClientHealthCheck.ps1"), "old check");
        File.WriteAllText(Path.Combine(dir, "healthcheck", "Only-On-This-Machine.ps1"), "not the release's");
        return dir;
    }

    /// <summary>A release as the packed app unpacks it: its files, and things that are NOT the machine's to take.</summary>
    private static string Release(string name, bool withAgent = true)
    {
        var dir = Path.Combine(_work, name);
        Directory.CreateDirectory(Path.Combine(dir, "branding"));
        Directory.CreateDirectory(Path.Combine(dir, "healthcheck"));
        File.WriteAllText(Path.Combine(dir, "RemoteSupport.Client.exe"), "new tray app");
        File.WriteAllText(Path.Combine(dir, "RemoteSupport.Host.exe"), "new host");
        if (withAgent) File.WriteAllText(Path.Combine(dir, "RemoteSupport.Agent.exe"), "new agent");
        File.WriteAllText(Path.Combine(dir, "Install-Client.ps1"), "new install");
        File.WriteAllText(Path.Combine(dir, "Uninstall-Client.ps1"), "new uninstall");
        File.WriteAllText(Path.Combine(dir, "Install-Client.bat"), "not the machine's to take");
        File.WriteAllText(Path.Combine(dir, "INSTALL - read me.txt"), "not the machine's to take");
        File.WriteAllText(Path.Combine(dir, "branding", "brand.json"), "{\"CompanyName\":\"the release's default\"}");
        File.WriteAllText(Path.Combine(dir, "healthcheck", "Get-ClientHealthCheck.ps1"), "new check");
        File.WriteAllText(Path.Combine(dir, "healthcheck", "New-In-This-Release.ps1"), "new script");
        File.WriteAllText(Path.Combine(dir, "healthcheck", "notes.txt"), "not a script");
        return dir;
    }

    private static string Read(string dir, string rel) => File.Exists(Path.Combine(dir, rel)) ? File.ReadAllText(Path.Combine(dir, rel)) : "(gone)";
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private static void Swap()
    {
        Head("The swap");
        var inst = Install("swap");
        var rel = Release("swap-release");
        var files = UpdateSwap.ReleaseFiles(rel);
        Check("only the release's own files are taken: the three programs, the two install scripts, the health check's scripts",
            files.Count == 7 && !files.Any(f => f.EndsWith(".bat") || f.EndsWith(".txt") || f.StartsWith("branding")), string.Join(", ", files));

        var pingHash = Hash(Path.Combine(inst, "RemoteSupport.Client.exe"));
        var running = Process.Start(new ProcessStartInfo(Path.Combine(inst, "RemoteSupport.Client.exe"), "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        _started.Add(running);
        Thread.Sleep(300);
        Check("(a program is running from the tray app's file, as a person's tray app would be)", !running.HasExited);

        UpdateSwap.Apply(inst, rel, files, V2);
        Check("every file of the release is in place", Read(inst, "RemoteSupport.Client.exe") == "new tray app" && Read(inst, "RemoteSupport.Agent.exe") == "new agent"
            && Read(inst, "healthcheck\\Get-ClientHealthCheck.ps1") == "new check" && Read(inst, "healthcheck\\New-In-This-Release.ps1") == "new script");
        Check("...even over the RUNNING program — renamed aside, still running", !running.HasExited
            && Hash(Path.Combine(inst, "RemoteSupport.Client.exe.old")) == pingHash);
        Check("the machine's own files untouched: branding, its identity, the health check's settings, a script the release does not carry",
            Read(inst, "branding\\brand.json").Contains("machine's own") && Read(inst, "device.json") == "the machine's identity"
            && Read(inst, "config.json") == "the health check's kept settings" && Read(inst, "healthcheck\\Only-On-This-Machine.ps1") == "not the release's");
        Check("nothing that is not the release's came in", !File.Exists(Path.Combine(inst, "Install-Client.bat")) && !File.Exists(Path.Combine(inst, "healthcheck", "notes.txt")));
        var record = UpdateSwap.Load(inst);
        Check("what was done is written down first (applied.json)", record != null && record.Version == V2 && record.Files.Count == 7 && record.Replaced.Count == 6,
            record == null ? "none" : $"{record.Files.Count} files, {record.Replaced.Count} replaced");

        var left = UpdateSwap.RollBack(inst);
        Check("rolled back: every replaced file is the old one again, byte for byte — the running one too", left.Count == 0
            && Hash(Path.Combine(inst, "RemoteSupport.Client.exe")) == pingHash && Read(inst, "RemoteSupport.Agent.exe") == "old agent"
            && Read(inst, "healthcheck\\Get-ClientHealthCheck.ps1") == "old check" && !running.HasExited, string.Join("; ", left));
        Check("...a file the release ADDED is gone, and the record with it", !File.Exists(Path.Combine(inst, "healthcheck", "New-In-This-Release.ps1"))
            && UpdateSwap.Load(inst) == null);
        Check("...and the machine's own files still untouched", Read(inst, "device.json") == "the machine's identity" && Read(inst, "branding\\brand.json").Contains("machine's own"));

        // Half-done: the release is missing a file it lists, so Apply throws part-way.
        var broken = Release("swap-broken");
        File.Delete(Path.Combine(broken, "Uninstall-Client.ps1"));
        var list = UpdateSwap.TopFiles.Concat(new[] { "healthcheck\\Get-ClientHealthCheck.ps1" }).ToList();
        bool threw = false;
        try { UpdateSwap.Apply(inst, broken, list, V2); } catch { threw = true; }
        var half = UpdateSwap.RollBack(inst);
        Check("a swap that failed half-way is undone completely", threw && half.Count == 0 && Hash(Path.Combine(inst, "RemoteSupport.Client.exe")) == pingHash
            && Read(inst, "RemoteSupport.Host.exe") == "old host" && Read(inst, "Install-Client.ps1") == "old install" && Read(inst, "Uninstall-Client.ps1") == "old uninstall",
            $"threw={threw} {string.Join("; ", half)}");

        bool outside = false;
        try { UpdateSwap.Apply(inst, rel, new[] { "..\\escaped.exe" }, V2); } catch (InvalidOperationException) { outside = true; }
        Check("a path outside the install folder is refused", outside && !File.Exists(Path.Combine(_work, "escaped.exe")));
        UpdateSwap.RollBack(inst);

        UpdateSwap.Apply(inst, rel, files, V2);
        UpdateSwap.Commit(inst);
        Check("kept: the old files are gone — except the one still running, kept until it stops",
            !File.Exists(Path.Combine(inst, "RemoteSupport.Agent.exe.old")) && File.Exists(Path.Combine(inst, "RemoteSupport.Client.exe.old")) && UpdateSwap.Load(inst) == null);
        running.Kill(); running.WaitForExit(); Thread.Sleep(200);
        UpdateSwap.ClearLeftovers(inst);
        Check("...and cleared once it has stopped (at the agent's next start)", !Directory.EnumerateFiles(inst, "*.old", SearchOption.AllDirectories).Any());
    }

    private static void ProcessPathAfterRename()
    {
        // The tray app restarts with Environment.ProcessPath. After the swap its own file is *.old — does it still name the
        // ORIGINAL path, where the new program now is? A copy of this harness asked, after its file was renamed under it.
        var dir = Path.Combine(_work, "pathcheck");
        Directory.CreateDirectory(dir);
        var me = Path.GetDirectoryName(Environment.ProcessPath!)!;
        foreach (var f in Directory.EnumerateFiles(me)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        var exe = Path.Combine(dir, Path.GetFileName(Environment.ProcessPath!));
        var signal = Path.Combine(_work, "renamed.signal"); var answer = Path.Combine(_work, "path.txt");
        var child = Process.Start(new ProcessStartInfo(exe, $"--say-path \"{signal}\" \"{answer}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        _started.Add(child);
        Thread.Sleep(1500);
        File.Move(exe, exe + ".old");
        File.WriteAllText(signal, "go");
        child.WaitForExit(30000);
        var said = File.Exists(answer) ? File.ReadAllText(answer) : "(nothing)";
        Check("a program renamed while running still names its ORIGINAL path — so the tray app restarts into the new file",
            string.Equals(said, exe, StringComparison.OrdinalIgnoreCase), said);
    }

    // ------------------------------------------------------------------ RUNNER

    private sealed class Server : IDisposable
    {
        private readonly HttpListener _l = new();
        public readonly string Base;
        public byte[] Body = Array.Empty<byte>();
        public readonly List<string> Ranges = new();
        public int Requests;
        public Server()
        {
            int port = FreePort();
            Base = $"http://127.0.0.1:{port}";
            _l.Prefixes.Add(Base + "/");
            _l.Start();
            _ = Task.Run(Loop);
        }
        private async Task Loop()
        {
            while (_l.IsListening)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                Requests++;
                var range = c.Request.Headers["Range"];
                long from = 0;
                if (range != null) { lock (Ranges) Ranges.Add(range); from = long.Parse(range.Split('=')[1].Split('-')[0]); c.Response.StatusCode = 206; }
                var part = Body.AsSpan((int)from).ToArray();
                c.Response.ContentLength64 = part.Length;
                await c.Response.OutputStream.WriteAsync(part);
                c.Response.Close();
            }
        }
        public void Dispose() { try { _l.Stop(); } catch { } }
    }

    private static UpdateRelease Rel(byte[] body, string version = V2, string path = "/update/client.exe") =>
        new() { Version = version, Path = path, Size = body.Length, Sha256 = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant() };

    private sealed class Seen
    {
        public readonly List<UpdateReportMsg> Reports = new();
        public readonly List<string> Applied = new();
        public string Steps { get { lock (Reports) return string.Join(" → ", Reports.Select(r => r.Step)); } }
        public UpdateReportMsg? Last { get { lock (Reports) return Reports.LastOrDefault(); } }
    }

    /// <summary>A runner whose checks say yes to everything but <paramref name="refuse"/> — the real ones are tried in REAL.</summary>
    private static (UpdateRunner, Seen) Runner(string dir, string serverBase, Func<bool>? idle = null, string? refuse = null, TimeSpan? idleFor = null)
    {
        var seen = new Seen();
        var r = new UpdateRunner(dir, serverBase, V1, m => { lock (seen.Reports) seen.Reports.Add(m); }, _ => { })
        {
            Jitter = TimeSpan.Zero, RetryDelays = new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100) },
            IdleFor = idleFor ?? TimeSpan.FromMilliseconds(300), IsIdle = idle ?? (() => true),
            Accept = (f, installed, expected) => Path.GetFileName(f) == refuse ? UpdateTrust.Verdict.No($"{refuse} is not ours") : new(true, "ok", expected ?? ""),
            Signature = f => Path.GetFileName(f) == refuse ? UpdateTrust.Verdict.No($"{refuse} is not ours") : new(true, "ok"),
            Unpack = (packed, into) => { CopyDir(Release("unpacked-" + Guid.NewGuid().ToString("N")[..6]), into); return true; },
            StartApply = (unpacked, version) => { lock (seen.Applied) seen.Applied.Add(version); return true; },
        };
        return (r, seen);
    }

    private static async Task Runner()
    {
        Head("The runner");
        using var server = new Server { Body = RandomNumberGenerator.GetBytes(200_000) };

        var (r, seen) = Runner(Install("runner-ok"), server.Base);
        r.Offer(Rel(server.Body));
        await (r.Running ?? Task.CompletedTask);
        Check("downloaded, every file checked, waited for nobody connected, handed to the apply step",
            seen.Steps == "downloading → ready → installing" && seen.Applied.SequenceEqual(new[] { V2 }), seen.Steps);

        // Carried on: half the file is already there.
        var inst = Install("runner-resume");
        var dl = Path.Combine(inst, "update", V2);
        Directory.CreateDirectory(dl);
        File.WriteAllBytes(Path.Combine(dl, "RemoteSupport.Client.exe"), server.Body.AsSpan(0, 80_000).ToArray());
        server.Ranges.Clear();
        (r, seen) = Runner(inst, server.Base);
        r.Offer(Rel(server.Body));
        await (r.Running ?? Task.CompletedTask);
        Check("a broken download carries on where it stopped (asks for the rest only)", server.Ranges.Contains("bytes=80000-") && seen.Applied.Count == 1,
            string.Join(", ", server.Ranges));

        // A broken file: the hash does not match the offer.
        (r, seen) = Runner(Install("runner-bad"), server.Base);
        var bad = Rel(server.Body); bad.Sha256 = new string('0', 64);
        r.Offer(bad);
        await (r.Running ?? Task.CompletedTask);
        Check("a download whose hash is not the offer's is thrown away, tried again, and given up as Failed — nothing installed",
            seen.Last?.Step == UpdateSteps.Failed && seen.Last.Detail.Contains("hash") && seen.Applied.Count == 0, seen.Last?.Detail ?? "");

        // One file of the release refused.
        inst = Install("runner-refused");
        (r, seen) = Runner(inst, server.Base, refuse: "RemoteSupport.Host.exe");
        r.Offer(Rel(server.Body));
        await (r.Running ?? Task.CompletedTask);
        Check("one file of the release refused: nothing installed, Refused said with why", seen.Last?.Step == UpdateSteps.Refused
            && seen.Last.Detail.Contains("Host") && seen.Applied.Count == 0, seen.Steps);
        Check("...and that version is never taken again, whatever the server says", r.GaveUp(V2));
        int before = seen.Reports.Count;
        r.Offer(Rel(server.Body));
        await Task.Delay(300);
        Check("...offered again: not even downloaded", seen.Reports.Count == before && r.Running?.IsCompleted != false);

        // The check could not be MADE (revocation not reachable): a failure to try again, never a refusal for ever.
        inst = Install("runner-offline");
        var seenOff = new Seen();
        var offline = new UpdateRunner(inst, server.Base, V1, m => seenOff.Reports.Add(m), _ => { })
        {
            Jitter = TimeSpan.Zero, RetryDelays = Array.Empty<TimeSpan>(),
            Accept = (f, i, e) => UpdateTrust.Verdict.No("whether its certificate was revoked could not be checked (offline)") with { TryLater = true },
            Signature = f => new(true, "ok"), Unpack = (p, into) => true, StartApply = (u, v) => true,
        };
        offline.Offer(Rel(server.Body));
        await (offline.Running ?? Task.CompletedTask);
        Check("a check that could not be made now (revocation offline) is a failure to try again — not refused for ever",
            seenOff.Last?.Step == UpdateSteps.Failed && !offline.GaveUp(V2), seenOff.Last?.Detail ?? "");

        // The packed app itself refused — before it is ever run.
        bool unpacked = false;
        inst = Install("runner-outer");
        var seenOuter = new Seen();
        var outer = new UpdateRunner(inst, server.Base, V1, m => seenOuter.Reports.Add(m), _ => { })
        {
            Jitter = TimeSpan.Zero, RetryDelays = Array.Empty<TimeSpan>(),
            Accept = (f, i, e) => UpdateTrust.Verdict.No("not ours"), Signature = f => new(true, "ok"),
            Unpack = (p, into) => { unpacked = true; return true; }, StartApply = (u, v) => true,
        };
        outer.Offer(Rel(server.Body));
        await (outer.Running ?? Task.CompletedTask);
        Check("the downloaded app is checked BEFORE it is run to unpack itself", seenOuter.Last?.Step == UpdateSteps.Refused && !unpacked);

        // Waits for nobody connected.
        bool idle = false;
        (r, seen) = Runner(Install("runner-busy"), server.Base, idle: () => idle, idleFor: TimeSpan.FromMilliseconds(500));
        r.Offer(Rel(server.Body));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (seen.Last?.Step != UpdateSteps.Ready && DateTime.UtcNow < until) await Task.Delay(50);
        await Task.Delay(1500);
        Check("while somebody is connected it waits, ready, and installs nothing", seen.Last?.Step == UpdateSteps.Ready && seen.Applied.Count == 0, seen.Steps);
        idle = true;
        await (r.Running ?? Task.CompletedTask);
        Check("...nobody connected for long enough: installed", seen.Applied.Count == 1);

        // Never another server; never an older or the same version.
        (r, seen) = Runner(Install("runner-elsewhere"), server.Base);
        server.Requests = 0;
        r.Offer(Rel(server.Body, path: "https://somewhere.else/client.exe"));
        await (r.Running ?? Task.CompletedTask);
        Check("an offer naming ANOTHER server is not fetched", seen.Last?.Step == UpdateSteps.Failed && seen.Last.Detail.Contains("another server") && server.Requests == 0,
            seen.Last?.Detail ?? "");
        (r, seen) = Runner(Install("runner-older"), server.Base);
        r.Offer(Rel(server.Body, version: "2026.10.15.100"));
        r.Offer(Rel(server.Body, version: V1));
        r.Offer(Rel(server.Body, version: "1.0.0.0"));
        await Task.Delay(300);
        Check("an older version, the same version, an unstamped one: not even looked at", seen.Reports.Count == 0 && r.Running == null);
    }

    // ------------------------------------------------------------------ APPLY

    private static void Applier()
    {
        Head("The apply step");
        var inst = Install("apply-good");
        var rel = Release("apply-good-release");
        var calls = new List<string>();
        var good = new UpdateApplier
        {
            WaitHealthy = TimeSpan.FromSeconds(5),
            StopAgent = () => { calls.Add("stop"); return true; },
            // The new agent connects and stays connected: it writes the marker.
            StartAgent = () => { calls.Add("start"); _ = Task.Run(async () => { await Task.Delay(300); File.WriteAllText(UpdateApplier.HealthyMarker(inst, V2), "ok"); }); return true; },
        };
        var r = good.Run(inst, rel, V2);
        Check("the new agent came back: kept", r.Step == UpdateSteps.Installed && Read(inst, "RemoteSupport.Agent.exe") == "new agent" && calls.SequenceEqual(new[] { "stop", "start" }),
            $"{r.Step} {r.Detail}; {string.Join(",", calls)}");
        Check("...the old files gone, the record gone", !File.Exists(Path.Combine(inst, "RemoteSupport.Agent.exe.old")) && UpdateSwap.Load(inst) == null);
        var taken = UpdateApplier.TakeResult(inst);
        Check("...and the next agent reports Installed — once", taken?.Step == UpdateSteps.Installed && UpdateApplier.TakeResult(inst) == null);

        inst = Install("apply-bad");
        var oldHost = Read(inst, "RemoteSupport.Host.exe");
        calls.Clear();
        var bad = new UpdateApplier
        {
            WaitHealthy = TimeSpan.FromSeconds(1.5),
            StopAgent = () => { calls.Add("stop"); return true; },
            StartAgent = () => { calls.Add("start"); return true; },   // starts, but never says it is healthy
        };
        r = bad.Run(inst, Release("apply-bad-release"), V2);
        Check("the new agent did not come back in time: rolled back", r.Step == UpdateSteps.RolledBack && r.Detail.Contains("did not connect"), r.Detail);
        Check("...every file the old one again, the release's new script gone", Read(inst, "RemoteSupport.Agent.exe") == "old agent" && Read(inst, "RemoteSupport.Host.exe") == oldHost
            && !File.Exists(Path.Combine(inst, "healthcheck", "New-In-This-Release.ps1")) && Read(inst, "device.json") == "the machine's identity");
        Check("...the new agent stopped and the OLD one started again", calls.SequenceEqual(new[] { "stop", "start", "stop", "start" }), string.Join(",", calls));
        var mem = new UpdateRunner(inst, "http://127.0.0.1:1", V1, _ => { }, _ => { });
        Check("...and that version is remembered: never taken again", mem.GaveUp(V2));
        Check("...the next agent reports Rolled back", UpdateApplier.TakeResult(inst)?.Step == UpdateSteps.RolledBack);

        // The downloads left behind: every release not newer than what runs goes; a newer one waiting stays; nothing goes
        // while a swap's record is there.
        inst = Install("apply-tidy");
        foreach (var v in new[] { "2026.10.16.1300", V1, V2, "notes" }) Directory.CreateDirectory(Path.Combine(inst, "update", v, "files"));
        UpdateRunner.ClearOldReleases(inst, V1);
        var kept = Directory.GetDirectories(Path.Combine(inst, "update")).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Check("old downloads are cleared at the agent's start — a newer one waiting, and anything else, stay",
            kept.SequenceEqual(new[] { V2, "notes" }), string.Join(", ", kept));
        UpdateSwap.Apply(inst, Release("apply-tidy-release"), new[] { "RemoteSupport.Host.exe" }, V2);
        UpdateRunner.ClearOldReleases(inst, "2099.1.1.0");
        Check("...but not while a swap is under way (a rollback may need them)", Directory.Exists(Path.Combine(inst, "update", V2)));
        UpdateSwap.RollBack(inst);

        inst = Install("apply-stuck");
        var stuck = new UpdateApplier { StopAgent = () => false, StartAgent = () => true };
        r = stuck.Run(inst, Release("apply-stuck-release"), V2);
        Check("an agent that cannot be stopped: nothing changed", r.Step == UpdateSteps.Failed && Read(inst, "RemoteSupport.Agent.exe") == "old agent" && UpdateSwap.Load(inst) == null, r.Detail);
    }

    // ------------------------------------------------------------------ REAL

    private static async Task RealChecks()
    {
        Head("With the real checks");
        using var server = new Server { Body = File.ReadAllBytes(Environment.ProcessPath!) };   // a real, unsigned program
        var inst = Install("real");
        var seen = new Seen();
        bool unpacked = false;
        var real = new UpdateRunner(inst, server.Base, V1, m => seen.Reports.Add(m), _ => { })
        {
            Jitter = TimeSpan.Zero, RetryDelays = Array.Empty<TimeSpan>(),
            Unpack = (p, into) => { unpacked = true; return true; }, StartApply = (u, v) => true,
        };
        real.Offer(Rel(server.Body));
        await (real.Running ?? Task.CompletedTask);
        Check("the runner's own checks (UpdateTrust) refuse an unsigned release before running it", seen.Last?.Step == UpdateSteps.Refused
            && seen.Last.Detail.Contains("not signed") && !unpacked, seen.Last?.Detail ?? seen.Steps);
    }

    // ------------------------------------------------------------------ plumbing

    private static void CopyDir(string from, string to)
    {
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(from, to), true);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }
}
