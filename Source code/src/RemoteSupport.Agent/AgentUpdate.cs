using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using RemoteSupport.Shared;

namespace RemoteSupport.Agent;

/// <summary>
/// The agent's half of auto-update (2026-10-16, stage D, docs/NEXT-SESSION-AUTOUPDATE.md). <see cref="UpdateRunner"/> and
/// <see cref="UpdateApplier"/> (Shared) hold the steps; this wires them to the service: who may say it updates itself,
/// what "nobody connected" means, how the apply step is started and how it stops and starts THIS service, and how the
/// new agent says it is healthy.
///
/// <para><b>Only the real service updates.</b> An agent run by hand — a developer's console, a harness — never says
/// <see cref="DeviceCan.Update"/>, so it is never offered one: an update swaps files in Program Files and restarts a
/// Windows service, and nothing else should ever do that.</para>
/// </summary>
internal sealed class AgentUpdate
{
    public const string ServiceName = "RemoteSupportAgent";
    /// <summary>How long the new agent stays connected before it says it is healthy — long enough that a refusal (which closes
    /// the socket at once) would have been heard.</summary>
    public static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(20);

    private readonly ILogger _log;
    private readonly DeviceLink _link;
    private readonly string _installDir = AppContext.BaseDirectory;
    private readonly UpdateRunner _runner;
    private CancellationTokenSource? _healthy;

    public static bool IsService => WindowsServiceHelpers.IsWindowsService();

    public AgentUpdate(ILogger log, DeviceLink link, string serverUrl, Func<bool> idle)
    {
        _log = log; _link = link;
        _runner = new UpdateRunner(_installDir, ToHttp(serverUrl), AppVersion.Current, r => _link.Send(r), s => _log.LogInformation("{Update}", s))
        {
            IsIdle = idle,
            StartApply = StartApply,
        };
    }

    /// <summary>Says <see cref="DeviceCan.Update"/> and takes offers — only as the real service.</summary>
    public void Wire()
    {
        if (!IsService) { _log.LogInformation("Not running as the service: this agent does not update itself"); return; }
        _link.Can.Add(DeviceCan.Update);
        _link.OnUpdateOffer += r => _runner.Offer(r);
        _link.OnConnectedChanged += Connected;
        // Old files from an earlier update that were still running then — the tray app has restarted since.
        if (UpdateSwap.Load(_installDir) == null) UpdateSwap.ClearLeftovers(_installDir);
        // And the downloads of releases already installed (or older): 130 MB each, never needed again.
        UpdateRunner.ClearOldReleases(_installDir, AppVersion.Current);
    }

    private void Connected(bool up)
    {
        _healthy?.Cancel();
        if (!up) return;
        // Whatever the last apply step said — Installed, or Rolled back — reported by whichever agent runs next.
        Report(UpdateApplier.TakeResult(_installDir));
        // The NEW agent, in the middle of its own apply step: say it is healthy once it has stayed connected.
        if (UpdateSwap.Load(_installDir) is { } applied && applied.Version == AppVersion.Current)
        {
            var cts = _healthy = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(HealthyAfter, cts.Token); } catch { return; }
                if (!_link.Connected) return;
                try { File.WriteAllText(UpdateApplier.HealthyMarker(_installDir, AppVersion.Current), DateTime.UtcNow.ToString("o")); }
                catch (Exception ex) { _log.LogWarning(ex, "update: could not say this agent is healthy"); return; }
                _log.LogInformation("update {Version}: connected and staying connected — told the apply step", AppVersion.Current);
                // The apply step answers within moments; its result is this agent's to report.
                for (int i = 0; i < 60 && !cts.IsCancellationRequested; i++)
                {
                    await Task.Delay(1000);
                    if (UpdateApplier.TakeResult(_installDir) is { } r) { Report(r); break; }
                }
            });
        }
    }

    private void Report(UpdateApplier.Result? r)
    {
        if (r == null) return;
        _log.LogInformation("update {Version}: {Step} {Detail}", r.Version, r.Step, r.Detail);
        _link.Send(new UpdateReportMsg { Version = r.Version, Step = r.Step, Detail = r.Detail });
    }

    /// <summary>The apply step is the NEW agent, from the checked release, as SYSTEM, on its own: it outlives this service's stop.</summary>
    private bool StartApply(string unpacked, string version)
    {
        try
        {
            var exe = Path.Combine(unpacked, "RemoteSupport.Agent.exe");
            Process.Start(new ProcessStartInfo(exe, $"--apply-update \"{_installDir.TrimEnd('\\')}\" \"{unpacked}\" {version}")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = unpacked });
            return true;
        }
        catch (Exception ex) { _log.LogError(ex, "update {Version}: the apply step could not be started", version); return false; }
    }

    /// <summary>
    /// <c>--apply-update &lt;install folder&gt; &lt;unpacked release&gt; &lt;version&gt;</c>: the whole apply step, then exit. Stops
    /// and starts the agent through the Service Control Manager — the only thing that may.
    /// </summary>
    public static int RunApply(string[] args)
    {
        if (args.Length < 4) return 2;
        string installDir = args[1], unpacked = args[2], version = args[3];
        var logFile = Path.Combine(installDir, UpdateSwap.UpdateFolder, "apply.log");
        void Log(string s) { try { File.AppendAllText(logFile, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {s}{Environment.NewLine}"); } catch { } }
        var applier = new UpdateApplier
        {
            Log = Log,
            StopAgent = () => Control(sc => { if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)); } }, Log),
            StartAgent = () => Control(sc => { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)); }, Log),
        };
        // The stopped agent's process may linger a moment after the service says Stopped; its files are renamed, not
        // overwritten, so that is harmless.
        var result = applier.Run(installDir, unpacked, version);
        Log($"{result.Step}: {result.Detail}");
        return result.Step == UpdateSteps.Installed ? 0 : 1;
    }

    private static bool Control(Action<ServiceController> act, Action<string> log)
    {
        try { using var sc = new ServiceController(ServiceName); act(sc); return true; }
        catch (Exception ex) { log("service: " + ex.Message); return false; }
    }

    private static string ToHttp(string url)
    {
        var u = url.Trim().TrimEnd('/');
        if (u.StartsWith("wss://")) return "https://" + u["wss://".Length..];
        if (u.StartsWith("ws://")) return "http://" + u["ws://".Length..];
        if (u.StartsWith("http://") || u.StartsWith("https://")) return u;
        return "https://" + u;
    }
}
