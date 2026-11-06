using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteSupport.Shared;

/// <summary>
/// The machine's side of an update, up to the moment the files are swapped (2026-10-16, auto-update stage D). The agent
/// owns one; <see cref="Offer"/> is what it does with <see cref="UpdateOfferMsg"/>.
///
/// <list type="number">
/// <item>Never a version it already gave up on, nor one not newer than itself.</item>
/// <item>A random wait first (the server spreads its offers; this spreads the downloads within them).</item>
/// <item>Download the packed customer app from the machine's OWN server, carrying on from a broken download, with
///   retries at 1, 5 and 15 minutes (antivirus holds new files — BUGS 9, §53).</item>
/// <item>The download's size and hash against the offer — a broken download, nothing more.</item>
/// <item><see cref="UpdateTrust.Accept"/>: ours, newer, and the version it was offered as. Only THEN is it run — to
///   unpack itself (<c>--unpack</c>) — and every file it lays down is checked again: each program ours and of the
///   release's version, each script ours. One refusal and nothing is installed, and this version never will be.</item>
/// <item>Wait until nobody is connected (<see cref="IsIdle"/>) for <see cref="IdleFor"/>.</item>
/// <item>Hand over to the apply step (<see cref="StartApply"/>): a separate process, because the agent must stop for its
///   own files to be swapped (<see cref="UpdateApplier"/>).</item>
/// </list>
///
/// The checks are the real ones, passed in only so the agent and a harness can hand over the same functions; nothing at
/// run time — no setting, no file, no message — can change them.
/// </summary>
public sealed class UpdateRunner
{
    public const string StateFile = "state.json";
    public const string ResultFile = "result.json";

    private readonly string _installDir;
    private readonly string _serverHttp;
    private readonly string _installed;
    private readonly Action<UpdateReportMsg> _report;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private Task? _running;
    private string _runningVersion = "";

    /// <summary>Whether nobody is connected right now — no session host running.</summary>
    public Func<bool> IsIdle { get; init; } = () => true;
    /// <summary>How long the machine must have been idle before the files are swapped.</summary>
    public TimeSpan IdleFor { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>The longest random wait before downloading.</summary>
    public TimeSpan Jitter { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan[] RetryDelays { get; init; } = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15) };
    /// <summary>Starts the apply step for a checked, unpacked release. True when it started.</summary>
    public Func<string, string, bool> StartApply { get; init; } = (_, _) => false;
    /// <summary>The checks: <see cref="UpdateTrust.Accept"/> and <see cref="UpdateTrust.Signature"/>.</summary>
    public Func<string, string, string?, UpdateTrust.Verdict> Accept { get; init; } = UpdateTrust.Accept;
    public Func<string, UpdateTrust.Verdict> Signature { get; init; } = UpdateTrust.Signature;
    /// <summary>Runs the packed app to unpack itself into a folder. True on success.</summary>
    public Func<string, string, bool> Unpack { get; init; } = UnpackWithApp;
    public HttpMessageHandler? Http { get; init; }

    public UpdateRunner(string installDir, string serverHttp, string installedVersion, Action<UpdateReportMsg> report, Action<string> log)
    {
        _installDir = installDir; _serverHttp = serverHttp.TrimEnd('/'); _installed = installedVersion; _report = report; _log = log;
    }

    public string Folder => Path.Combine(_installDir, UpdateSwap.UpdateFolder);

    /// <summary>The task of the update in hand, for a harness to wait on. Null when none.</summary>
    public Task? Running { get { lock (_lock) return _running; } }

    public void Offer(UpdateRelease release)
    {
        if (!AppVersion.IsStamped(release.Version) || AppVersion.Compare(release.Version, _installed) <= 0) return;
        if (GaveUp(release.Version)) { _log($"update {release.Version}: given up on before — not taken"); return; }
        lock (_lock)
        {
            if (_running is { IsCompleted: false }) { if (_runningVersion != release.Version) _log($"update {release.Version}: busy with {_runningVersion}"); return; }
            _runningVersion = release.Version;
            _running = Task.Run(() => RunAsync(release));
        }
    }

    private void Say(string version, string step, string detail = "")
    {
        _log($"update {version}: {step}{(detail.Length > 0 ? " — " + detail : "")}");
        try { _report(new UpdateReportMsg { Version = version, Step = step, Detail = detail }); } catch { }
    }

    private async Task RunAsync(UpdateRelease r)
    {
        try
        {
            if (Jitter > TimeSpan.Zero) await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * Jitter.TotalMilliseconds));
            var dir = Path.Combine(Folder, r.Version);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "RemoteSupport.Client.exe");

            // ---- download, with retries
            Say(r.Version, UpdateSteps.Downloading);
            string? problem = null;
            for (int attempt = 0; ; attempt++)
            {
                problem = await DownloadAsync(r, file);
                if (problem == null) break;
                if (attempt >= RetryDelays.Length) { Say(r.Version, UpdateSteps.Failed, problem); return; }
                _log($"update {r.Version}: {problem}; trying again in {RetryDelays[attempt].TotalMinutes:0} min");
                await Task.Delay(RetryDelays[attempt]);
            }

            // ---- the packed app: ours, newer, as offered — before it is ever run
            var outer = Accept(file, _installed, r.Version);
            if (!outer.Ok) { Refuse(r.Version, outer); return; }
            var unpacked = Path.Combine(dir, "files");
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
            if (!Unpack(file, unpacked)) { Say(r.Version, UpdateSteps.Failed, "the release could not be unpacked"); return; }
            // The packed app IS the tray app: it goes in as RemoteSupport.Client.exe, as Launcher.StageInstall puts it.
            File.Copy(file, Path.Combine(unpacked, "RemoteSupport.Client.exe"), overwrite: true);

            // ---- every file it lays down, again
            var files = UpdateSwap.ReleaseFiles(unpacked);
            if (!files.Contains("RemoteSupport.Agent.exe") || !files.Contains("RemoteSupport.Host.exe"))
            { Refuse(r.Version, "the release does not carry the agent and the host"); return; }
            foreach (var rel in files)
            {
                var path = Path.Combine(unpacked, rel);
                var v = UpdateSwap.IsProgram(rel) ? Accept(path, _installed, r.Version) : Signature(path);
                if (!v.Ok) { Refuse(r.Version, v); return; }
            }

            // ---- wait until nobody is connected
            Say(r.Version, UpdateSteps.Ready, $"{files.Count} files checked");
            var idleSince = DateTime.UtcNow;
            while (true)
            {
                if (!IsIdle()) idleSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - idleSince >= IdleFor) break;
                await Task.Delay(IdleFor <= TimeSpan.FromSeconds(5) ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(15));
            }

            Say(r.Version, UpdateSteps.Installing);
            if (!StartApply(unpacked, r.Version)) Say(r.Version, UpdateSteps.Failed, "the apply step could not be started");
        }
        catch (Exception ex) { Say(r.Version, UpdateSteps.Failed, ex.Message); }
    }

    /// <summary>
    /// A file was not accepted. Refused for ever — unless the check itself could not be made now (the certificate's
    /// revocation could not be looked up): then it is a failure like a broken download, tried again at the next offer.
    /// </summary>
    private void Refuse(string version, UpdateTrust.Verdict v)
    {
        if (v.TryLater) { Say(version, UpdateSteps.Failed, v.Why + " — will try again"); return; }
        Refuse(version, v.Why);
    }

    private void Refuse(string version, string why)
    {
        Remember(version);
        Say(version, UpdateSteps.Refused, why);
    }

    /// <summary>Fetches the file, carrying on from what is already there. Null on success, or what went wrong.</summary>
    private async Task<string?> DownloadAsync(UpdateRelease r, string file)
    {
        if (!r.Path.StartsWith('/') || r.Path.StartsWith("//") || r.Path.Contains(':')) return "the offer named another server";
        var url = _serverHttp + r.Path;
        if (SecureTransport.Refusal(url) is { } insecure) return insecure;
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length == r.Size && Sha(file) == r.Sha256) return null;
            if (File.Exists(file) && new FileInfo(file).Length > r.Size) File.Delete(file);
            using var http = Http != null ? new HttpClient(Http, disposeHandler: false) : new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(30);
            long have = File.Exists(file) ? new FileInfo(file).Length : 0;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode) return $"the download was refused ({(int)res.StatusCode})";
            bool carryOn = have > 0 && res.StatusCode == System.Net.HttpStatusCode.PartialContent;
            await using (var to = new FileStream(file, carryOn ? FileMode.Append : FileMode.Create, FileAccess.Write))
            await using (var from = await res.Content.ReadAsStreamAsync())
                await from.CopyToAsync(to);
            var size = new FileInfo(file).Length;
            if (size != r.Size) { File.Delete(file); return $"the download was {size} bytes, not {r.Size}"; }
            if (Sha(file) != r.Sha256) { File.Delete(file); return "the download's hash is not the offer's"; }
            return null;
        }
        catch (Exception ex) { return "the download failed: " + ex.Message; }
    }

    private static string Sha(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>The packed customer app unpacks itself: <c>--unpack</c>, handled before it shows anything (Client/Program.cs).</summary>
    public static bool UnpackWithApp(string packed, string into)
    {
        using var p = Process.Start(new ProcessStartInfo(packed, $"--unpack \"{into}\"") { UseShellExecute = false, CreateNoWindow = true });
        if (p == null) return false;
        if (!p.WaitForExit(5 * 60_000)) { try { p.Kill(); } catch { } return false; }
        return p.ExitCode == 0 && File.Exists(Path.Combine(into, "RemoteSupport.Agent.exe"));
    }

    /// <summary>
    /// Releases downloaded before — 130 MB each — that are no longer needed: every <c>update\&lt;version&gt;</c> folder not
    /// newer than what is running, unless a swap is still under way (its record names it, and a rollback would need it).
    /// Best effort, at every start of the agent; a folder still in use is left for the next.
    /// </summary>
    public static void ClearOldReleases(string installDir, string running)
    {
        var folder = Path.Combine(installDir, UpdateSwap.UpdateFolder);
        if (!Directory.Exists(folder) || UpdateSwap.Load(installDir) != null) return;
        foreach (var dir in Directory.GetDirectories(folder))
        {
            var name = Path.GetFileName(dir);
            if (!AppVersion.IsStamped(name) || AppVersion.Compare(name, running) > 0) continue;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ------------------------------------------------------------------ what the machine remembers

    private sealed class State { public List<string> GaveUp { get; set; } = new(); }

    private string StatePath => Path.Combine(Folder, StateFile);

    public bool GaveUp(string version)
    {
        try { return File.Exists(StatePath) && (JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath))?.GaveUp.Contains(version) ?? false); }
        catch { return false; }
    }

    /// <summary>A version this machine refused or rolled back: never taken again, whatever the server says.</summary>
    public void Remember(string version) => RememberIn(Folder, version);

    public static void RememberIn(string folder, string version)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, StateFile);
            var s = File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State() : new State();
            if (!s.GaveUp.Contains(version)) s.GaveUp.Add(version);
            File.WriteAllText(path, JsonSerializer.Serialize(s));
        }
        catch { }
    }
}

/// <summary>
/// The apply step (stage D): a process of its own — the new agent, started from the unpacked release with
/// <c>--apply-update</c> — because the agent must stop for its own files to be swapped. It stops the agent, swaps the
/// files (<see cref="UpdateSwap"/>), starts it again, and waits for the NEW agent to say it is connected
/// (<see cref="HealthyMarker"/>). If it does not within <see cref="WaitHealthy"/>, everything goes back as it was and the
/// old agent is started again: a bad update must never leave a machine nobody can reach.
/// </summary>
public sealed class UpdateApplier
{
    public TimeSpan WaitHealthy { get; init; } = TimeSpan.FromMinutes(5);
    public Func<bool> StopAgent { get; init; } = () => false;
    public Func<bool> StartAgent { get; init; } = () => false;
    public Action<string> Log { get; init; } = _ => { };

    /// <summary>The file the new agent writes once it has connected and stayed connected.</summary>
    public static string HealthyMarker(string installDir, string version) => Path.Combine(installDir, UpdateSwap.UpdateFolder, $"healthy-{version}");

    public sealed record Result(string Version, string Step, string Detail);

    public static string ResultPath(string installDir) => Path.Combine(installDir, UpdateSwap.UpdateFolder, UpdateRunner.ResultFile);

    /// <summary>The whole step. What happened is also written to <see cref="ResultPath"/>, for whichever agent runs next to report.</summary>
    public Result Run(string installDir, string unpacked, string version)
    {
        var marker = HealthyMarker(installDir, version);
        try { File.Delete(marker); } catch { }
        var files = UpdateSwap.ReleaseFiles(unpacked);
        Log($"apply {version}: stopping the agent");
        if (!StopAgent()) return Write(installDir, new Result(version, UpdateSteps.Failed, "the agent could not be stopped; nothing was changed"));

        string why;
        try
        {
            UpdateSwap.Apply(installDir, unpacked, files, version);
            Log($"apply {version}: {files.Count} files in place; starting the new agent");
            if (StartAgent())
            {
                var until = DateTime.UtcNow + WaitHealthy;
                while (DateTime.UtcNow < until)
                {
                    if (File.Exists(marker))
                    {
                        UpdateSwap.Commit(installDir);
                        Log($"apply {version}: the new agent is connected — done");
                        return Write(installDir, new Result(version, UpdateSteps.Installed, $"{files.Count} files"));
                    }
                    Thread.Sleep(WaitHealthy <= TimeSpan.FromSeconds(30) ? 100 : 2000);
                }
                why = WaitHealthy >= TimeSpan.FromMinutes(1)
                    ? $"the new agent did not connect within {WaitHealthy.TotalMinutes:0.#} minutes"
                    : $"the new agent did not connect within {WaitHealthy.TotalSeconds:0.#} seconds";
            }
            else why = "the new agent could not be started";
        }
        catch (Exception ex) { why = "the files could not be put in place: " + ex.Message; }

        Log($"apply {version}: {why} — putting the old files back");
        StopAgent();
        var left = UpdateSwap.RollBack(installDir);
        UpdateRunner.RememberIn(Path.Combine(installDir, UpdateSwap.UpdateFolder), version);
        var started = StartAgent();
        var detail = why + (left.Count > 0 ? "; could not put back: " + string.Join(", ", left) : "") + (started ? "" : "; the old agent did not start again");
        return Write(installDir, new Result(version, UpdateSteps.RolledBack, detail));
    }

    private static Result Write(string installDir, Result r)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(installDir, UpdateSwap.UpdateFolder));
            File.WriteAllText(ResultPath(installDir), JsonSerializer.Serialize(r));
        }
        catch { }
        return r;
    }

    /// <summary>What the last apply step said, taken once: the agent that runs next reports it, then it is gone.</summary>
    public static Result? TakeResult(string installDir)
    {
        try
        {
            var path = ResultPath(installDir);
            if (!File.Exists(path)) return null;
            var r = JsonSerializer.Deserialize<Result>(File.ReadAllText(path));
            File.Delete(path);
            return r;
        }
        catch { return null; }
    }
}
