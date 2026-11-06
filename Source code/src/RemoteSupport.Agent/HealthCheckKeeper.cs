using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteSupport.Shared;

namespace RemoteSupport.Agent;

/// <summary>
/// Makes this machine's monthly health check match what the server wants, and says what is true.
///
/// Clint, 2026-09-28: not every customer gets the health check — SLA clients and the ones chosen.
/// So the installer no longer sets it up; it only copies the scripts beside the app, where they do
/// nothing. The server decides per machine (the customer's tick, or the machine's own override)
/// and tells THIS service, because only a SYSTEM service can register or remove a SYSTEM
/// scheduled task — the tray app runs as the person, and so does an attended session's host.
///
/// It is told a STATE, not a command. Being told "on" twice does nothing the second time; a
/// machine that was off while somebody changed it catches up when it connects. And whatever it
/// does, it reports what it then FINDS, never what it was asked for: installed is not running
/// (§41), and the console draws this answer rather than the wish.
///
/// It removes only what this platform installed. DESKTOP-OPHQIDH also carries the standalone
/// toolkit that predates this repository, in the same folder, and that is somebody else's.
/// </summary>
internal sealed class HealthCheckKeeper
{
    /// <summary>The name Install-HealthCheck.ps1 registers under. Changing one means changing both.</summary>
    public const string TaskName = "Monthly Maintenance and Health Check";

    /// <summary>The files the installer copies into the install folder — compared to spot an install
    /// left behind by an older version of the app, which a reinstall used to refresh.</summary>
    private static readonly string[] Scripts =
    {
        "Get-ClientHealthCheck.ps1", "Invoke-ClientMaintenance.ps1", "New-ClientReport.ps1",
        "Invoke-MonthlyRun.ps1", "Uninstall-HealthCheck.ps1"
    };

    private readonly ILogger _log;
    private readonly Branding _brand;
    private readonly SemaphoreSlim _one = new(1, 1);
    private HealthCheckWantedMsg? _pending;
    private readonly object _pendingLock = new();
    /// <summary>Released on every new answer, so a pass waiting to retry wakes and yields to it.</summary>
    private readonly SemaphoreSlim _arrived = new(0);

    /// <summary>
    /// How long to wait before each further attempt when one fails (BUGS.md, bug 9). ESET holds an
    /// unknown file while it asks its cloud, then releases it about a minute later having decided it
    /// is safe; by then the one attempt there used to be had failed, and nothing ever tried again.
    /// So: at once, then after 1, 5 and 15 minutes - four attempts over about 21 minutes, which
    /// costs nothing when nothing is wrong. Settable for the harness only.
    /// </summary>
    internal TimeSpan[] RetryDelays { get; init; } =
        { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15) };

    /// <summary>One attempt at making the machine match. The real one unless a harness swaps it.</summary>
    internal Func<HealthCheckWantedMsg, Outcome>? Attempt { get; init; }

    /// <summary>What one attempt came to, and whether trying the same thing again could change it.</summary>
    internal sealed record Outcome(HealthCheckStatusMsg Status, bool WorthRetrying);

    /// <summary>Where the scripts ship with the app: Program Files\…\Remote Support\healthcheck.</summary>
    public string SourceDir { get; init; } = Path.Combine(AppContext.BaseDirectory, "healthcheck");
    /// <summary>Where Install-HealthCheck.ps1 puts it on the machine.</summary>
    public string InstallPath { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HealthCheck");
    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteSupport", "healthcheck-agent.log");

    public HealthCheckKeeper(ILogger log, Branding brand) { _log = log; _brand = brand; }

    /// <summary>
    /// Queue the wanted state and act on it. Only the LATEST matters: flicked on and off three
    /// times while an install is running comes to one more pass with the last answer, not three.
    /// </summary>
    public void Want(HealthCheckWantedMsg m, Action<HealthCheckStatusMsg> report)
    {
        lock (_pendingLock) _pending = m;
        // A pass waiting to retry wakes on this and stands aside: the newer answer goes first. The
        // same answer counts as newer - that is how a technician's "On" again means "try now".
        _arrived.Release();
        _ = Task.Run(async () =>
        {
            await _one.WaitAsync();
            try
            {
                HealthCheckWantedMsg? next;
                lock (_pendingLock) { next = _pending; _pending = null; }
                if (next == null) return;   // a pass already took it
                await ApplyWithRetriesAsync(next, report);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Health check: could not act on the server's answer");
                report(new HealthCheckStatusMsg { Installed = Find() == State.Ours, Note = "the support app failed while setting it up: " + ex.Message });
            }
            finally { _one.Release(); }
        });
    }

    internal enum State { None, Ours, Foreign }

    /// <summary>
    /// Attempt, and on a failure worth repeating wait and attempt again, saying so each time.
    ///
    /// Each failure is REPORTED before the wait, with when the next try is, so the console never
    /// shows a failure as final while one is coming - nor "waiting to install" while the machine has
    /// in fact refused twice (§41: the machine's own words, not the wish).
    ///
    /// The wait gives way to any newer answer. That is the detail bug 9 named: a machine switched
    /// OFF during a five-minute wait must not be installed at the end of it. The newer answer has a
    /// pass of its own queued behind this one, so this one simply stops, reporting nothing more.
    /// </summary>
    internal async Task ApplyWithRetriesAsync(HealthCheckWantedMsg m, Action<HealthCheckStatusMsg> report)
    {
        var attempt = Attempt ?? Apply;
        for (int n = 1; ; n++)
        {
            var o = attempt(m);
            if (!o.WorthRetrying)
            {
                report(o.Status);
                return;
            }
            if (n > RetryDelays.Length)
            {
                report(new HealthCheckStatusMsg
                {
                    Installed = o.Status.Installed,
                    Note = $"{o.Status.Note} - tried {n} times over {Words(RetryDelays.Aggregate(TimeSpan.Zero, (a, b) => a + b))}; " +
                           $"see {LogPath} on the machine, or choose it again in the console to try now"
                });
                return;
            }
            var wait = RetryDelays[n - 1];
            _log.LogInformation("Health check: attempt {N} failed; trying again in {Wait}", n, wait);
            report(new HealthCheckStatusMsg
            {
                Installed = o.Status.Installed,
                Note = $"{o.Status.Note} - trying again in {Words(wait)} (antivirus often holds new files for a minute)"
            });
            if (await NewerArrivedWithinAsync(wait))
            {
                _log.LogInformation("Health check: a newer answer arrived; stopping the retries for the old one");
                return;
            }
        }
    }

    /// <summary>True as soon as a newer answer is waiting; false when the time runs out first.</summary>
    private async Task<bool> NewerArrivedWithinAsync(TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            lock (_pendingLock) if (_pending != null) return true;
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return false;
            // A release can be left over from an answer an earlier pass already took; waking on it
            // costs one trip round this loop, which then finds nothing pending and waits on.
            await _arrived.WaitAsync(left);
        }
    }

    private static string Words(TimeSpan t) =>
        t.TotalMinutes >= 1
            ? (Math.Round(t.TotalMinutes) == 1 ? "1 minute" : $"{Math.Round(t.TotalMinutes)} minutes")
            : (Math.Round(t.TotalSeconds) == 1 ? "1 second" : $"{Math.Max(0, Math.Round(t.TotalSeconds))} seconds");

    /// <summary>
    /// The machine was removed from the company (2026-10-06): our monthly health check goes with it, as
    /// "off" would take it — and a health check this app did not install is left alone, as ever.
    /// </summary>
    internal HealthCheckStatusMsg RemoveForUninstall() => Apply(new HealthCheckWantedMsg { On = false }).Status;

    private Outcome Apply(HealthCheckWantedMsg m)
    {
        var now = Find();
        if (m.On)
        {
            if (now == State.Ours && !Stale())
                return new(new HealthCheckStatusMsg { Installed = true, Note = "installed" }, false);

            // Not worth retrying: waiting does not put the scripts there. A reinstall of the app does.
            if (!File.Exists(Path.Combine(SourceDir, "Install-HealthCheck.ps1")))
                return new(new HealthCheckStatusMsg
                {
                    Installed = now == State.Ours,
                    Note = "the health check scripts are not beside the support app - reinstall the support app on this machine"
                }, false);

            _log.LogInformation("Health check: {What}", now == State.Ours ? "refreshing an older install" : "installing");
            var (code, _) = RunPowerShell(InstallCommand(m.DayOfMonth), "install");
            var after = Find();
            if (after == State.Ours && !Stale()) return new(new HealthCheckStatusMsg { Installed = true, Note = "installed" }, false);
            // Retried, whatever the exit code says. An antivirus hold looks like anything - a file
            // that cannot be read, a script that stops half way, a timeout - and a real fault costs
            // three more tries in the log, which is where it gets diagnosed anyway.
            return new(new HealthCheckStatusMsg
            {
                Installed = after == State.Ours,
                Note = after == State.Ours
                    ? $"could not bring it up to date (exit {code})"
                    : $"could not install it (exit {code})"
            }, true);
        }

        switch (now)
        {
            case State.None:
                return new(new HealthCheckStatusMsg { Installed = false, Note = "" }, false);
            case State.Foreign:
                // Somebody else's. Reported, so the console can say why a machine that is "off" still
                // runs a monthly check — and left exactly as it is.
                return new(new HealthCheckStatusMsg
                {
                    Installed = false,
                    Note = "a health check not set up by this support app is on the machine, and was left alone"
                }, false);
        }

        _log.LogInformation("Health check: removing the monthly schedule");
        var uninstall = Path.Combine(SourceDir, "Uninstall-HealthCheck.ps1");
        if (!File.Exists(uninstall)) uninstall = Path.Combine(InstallPath, "Uninstall-HealthCheck.ps1");
        int rc;
        if (File.Exists(uninstall))
            (rc, _) = RunPowerShell($"& {Q(uninstall)} -InstallPath {Q(InstallPath)} -TaskName {Q(TaskName)}", "remove");
        else
            rc = RunSchtasks($"/Delete /TN \"{TaskName}\" /F").code;
        var gone = Find();
        // Removal runs a script too, and an antivirus can hold that just the same.
        return gone == State.Ours
            ? new(new HealthCheckStatusMsg { Installed = true, Note = $"could not remove it (exit {rc})" }, true)
            : new(new HealthCheckStatusMsg { Installed = false, Note = "" }, false);
    }

    /// <summary>
    /// What is on the machine. Ours is our task AND a config that names a platform server — the
    /// standalone toolkit writes the same folder and the same kind of task, and never that section.
    /// </summary>
    internal State Find()
    {
        bool task = RunSchtasks($"/Query /TN \"{TaskName}\"").code == 0;
        if (!task) return State.None;
        return PlatformManaged() ? State.Ours : State.Foreign;
    }

    private bool PlatformManaged()
    {
        try
        {
            var p = Path.Combine(InstallPath, "config.json");
            if (!File.Exists(p)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.TryGetProperty("Platform", out var plat)
                && plat.ValueKind == JsonValueKind.Object
                && plat.TryGetProperty("BaseUrl", out var url)
                && url.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(url.GetString());
        }
        catch { return false; }
    }

    /// <summary>
    /// The installed scripts differ from the ones that shipped with this app. A reinstall of the
    /// client used to re-run the health check installer, which is how a fixed script reached a
    /// machine; now the client installer does not touch it, so this is how.
    /// </summary>
    private bool Stale()
    {
        foreach (var s in Scripts)
        {
            var src = Path.Combine(SourceDir, s);
            if (!File.Exists(src)) continue;                  // nothing newer to offer
            var dst = Path.Combine(InstallPath, s);
            if (!File.Exists(dst)) return true;
            if (!Hash(src).SequenceEqual(Hash(dst))) return true;
        }
        return false;
    }

    private static byte[] Hash(string path) { using var f = File.OpenRead(path); return SHA256.HashData(f); }

    private string InstallCommand(int dayOfMonth)
    {
        // Every value named, including the empty ones: an empty string is what gets lost crossing a
        // powershell.exe -File boundary (see Install-Client.ps1), so this goes as a script instead,
        // where '' stays ''.
        var http = (_brand.ServerUrl ?? "").Trim();
        if (http.StartsWith("wss://")) http = "https://" + http["wss://".Length..];
        else if (http.StartsWith("ws://")) http = "http://" + http["ws://".Length..];
        var machine = Environment.MachineName;
        var sb = new StringBuilder();
        sb.Append("& ").Append(Q(Path.Combine(SourceDir, "Install-HealthCheck.ps1")));
        // Organisation is the END CUSTOMER and this machine cannot know it; the monthly run asks the
        // server each month and replaces this placeholder (§17).
        sb.Append(" -Organisation ").Append(Q(machine));
        sb.Append(" -UserOrRole ").Append(Q(machine));
        sb.Append(" -CompanyName ").Append(Q(_brand.CompanyName));
        sb.Append(" -CompanyEmail ").Append(Q(_brand.SupportEmail));
        sb.Append(" -CompanyPhone ").Append(Q(_brand.SupportPhone));
        sb.Append(" -CompanyTag ''");
        sb.Append(" -BrandLogoPath ").Append(Q(_brand.HasLogo ? _brand.LogoPath : ""));
        sb.Append(" -AccentColor ").Append(Q(_brand.AccentColor));
        sb.Append(" -PrimaryColor ").Append(Q(_brand.PrimaryColor));
        sb.Append(" -PlatformUrl ").Append(Q(http.TrimEnd('/')));
        sb.Append(" -InstallPath ").Append(Q(InstallPath));
        sb.Append(" -TaskName ").Append(Q(TaskName));
        // Keep: a schedule or anything else configured on the machine survives a refresh.
        sb.Append(" -ExistingSettings Keep -PlatformManaged -NoTestRun");
        if (dayOfMonth is > 0 and <= 28) sb.Append(" -DayOfMonth ").Append(dayOfMonth);
        return sb.ToString();
    }

    /// <summary>A PowerShell single-quoted literal. Nothing inside it is interpreted.</summary>
    internal static string Q(string? s) => "'" + (s ?? "").Replace("'", "''") + "'";

    private (int code, string output) RunPowerShell(string script, string what)
    {
        // -EncodedCommand, so no quoting survives or fails to survive a command line.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ProgressPreference='SilentlyContinue'; " + script + "; exit $LASTEXITCODE"));
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Directory.Exists(SourceDir) ? SourceDir : AppContext.BaseDirectory
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            try { p.Kill(true); } catch { }
            Log(what, -1, "timed out after 5 minutes\n" + outTask.Result + errTask.Result);
            return (-1, "timed out");
        }
        var output = outTask.Result + (errTask.Result.Length > 0 ? "\n--- errors ---\n" + errTask.Result : "");
        Log(what, p.ExitCode, output);
        return (p.ExitCode, output);
    }

    private static (int code, string output) RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    /// <summary>
    /// The installer's own output, kept on the machine. The agent has no window and its event log
    /// entries are Information-level (§41), so without this a failed install is a one-line note in
    /// the console and nothing anywhere saying why.
    /// </summary>
    private void Log(string what, int code, string output)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what}  exit {code}\n{output}\n");
        }
        catch { }
        if (code == 0) _log.LogInformation("Health check {What}: exit 0", what);
        else _log.LogWarning("Health check {What} failed with exit {Code}; see {Log}", what, code, LogPath);
    }
}
