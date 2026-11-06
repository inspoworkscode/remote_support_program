using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteSupport.Shared;

namespace RemoteSupport.Agent;

/// <summary>Unattended agent: keeps a device connection open and launches a host process per session.</summary>
public sealed class AgentService : BackgroundService
{
    private readonly ILogger<AgentService> _log;
    private readonly Branding _brand;
    private readonly string _wsBase;
    private readonly DeviceIdentity _id;
    private readonly Dictionary<string, int> _sessionPids = new();
    private readonly object _lock = new();

    public AgentService(ILogger<AgentService> log)
    {
        _log = log;
        _brand = Branding.Load();
        _wsBase = ToWs(_brand.ServerUrl);
        _id = DeviceIdentity.LoadOrCreate();
    }

    private static string ToWs(string url)
    {
        var u = url.Trim().TrimEnd('/');
        if (u.StartsWith("http://")) return "ws://" + u["http://".Length..];
        if (u.StartsWith("https://")) return "wss://" + u["https://".Length..];
        if (u.StartsWith("ws://") || u.StartsWith("wss://")) return u;
        return "wss://" + u;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Agent starting. Device {Id}, server {Server}", _id.DeviceId, _wsBase);
        var link = new DeviceLink(_wsBase, _id, DeviceRole.Agent, _brand.TenantKey);
        // This build asks before an attended session (StartSessionMsg.AskConsent), so the hub may
        // hand it one. Said, not assumed: an agent that never said it is never sent one.
        link.Can.Add(DeviceCan.AskConsent);
        // The console's Processes and Performance windows. Offered here first: as SYSTEM the agent
        // can name the account behind every process, which the tray app cannot.
        link.OfferWatch();
        link.OfferWake();
        // The company's look as it is now (2026-10-07, roadmap 4.6): this service is the one thing on the machine
        // that can write beside the installed branding in Program Files, so it keeps it for everything else.
        link.Can.Add(DeviceCan.Branding);
        link.BrandHash = _brand.LiveHash;
        // Who it was installed for (2026-10-23), from the installer window; the tray app says it too, whichever connects first.
        link.InstalledFor = GivenPerson.Installed();
        link.OnBranding += m => TakeBranding(link, m);
        link.OnConnectedChanged += c => _log.LogInformation("Server connection: {State}", c ? "connected" : "disconnected");
        // The agent has no window, so its log is the only place a refusal can be seen from this
        // machine. Without it an unattended machine that the server is turning away is silent
        // everywhere: no UI, no log line, and nothing in the technician's device list.
        link.OnServerError += why => _log.LogWarning("The server REFUSED this machine: {Why}", why);
        link.OnStartSession += StartSession;
        link.OnSessionEnded += EndSession;
        link.OnSysAction += sa => DoSysAction(link, sa);
        // The monthly health check: the server says whether it belongs on this machine, the keeper
        // makes it so, and what the keeper then FINDS goes back — not what it was asked for.
        var keeper = new HealthCheckKeeper(_log, _brand);
        link.OnHealthCheckWanted += w =>
        {
            _log.LogInformation("Health check wanted: {On}", w.On ? "on" : "off");
            keeper.Want(w, status => link.Send(status));
        };
        // Removed from the company's list while connected (2026-10-06): take ourselves off this machine.
        link.OnRemoved += why => UninstallSelf(why, keeper);
        // Auto-update (2026-10-16): offers taken, checked, and installed when nobody is connected — as the service only.
        new AgentUpdate(_log, link, _brand.ServerUrl, NobodyConnected).Wire();
        await link.RunAsync(ct);
    }

    /// <summary>
    /// Ctrl+Alt+Del, and only Ctrl+Alt+Del. Everything else a technician can ask for is done by
    /// the host, which is in the interactive session where it belongs.
    ///
    /// This one has to happen HERE because `SendSAS` requires a **service running as
    /// LocalSystem**. The host is LocalSystem too, but it is a process launched into the
    /// interactive session, and Windows does not treat that as a service — the call returns
    /// without error and nothing happens, which is exactly what was measured on 2026-09-15 for
    /// both values of AsUser. This service runs in session 0, which is the thing being asked for.
    ///
    /// AsUser = false: the sequence goes to the active console session rather than to session 0,
    /// which is what a service wants and is how every remote-control product does this.
    /// </summary>
    private void DoSysAction(DeviceLink link, SysActionMsg sa)
    {
        if (!string.Equals(sa.Action, "ctrl_alt_del", StringComparison.OrdinalIgnoreCase))
        {
            // Anything else reaching the agent is a routing mistake on the server, not something
            // to silently swallow — the host is the only thing that should be doing it.
            _log.LogWarning("The agent was sent system action {Action}, which belongs to the host. Ignored.", sa.Action);
            return;
        }
        try
        {
            NativeSas.SendSAS(false);
            _log.LogInformation("Ctrl+Alt+Del sent to the console session");
            link.Send(new SysActionResultMsg { Action = sa.Action, Ok = true, Detail = "sent Ctrl+Alt+Del" });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendSAS failed");
            link.Send(new SysActionResultMsg { Action = sa.Action, Ok = false, Detail = ex.GetType().Name + ": " + ex.Message });
        }
    }

    /// <summary>
    /// The machine was removed from the company's list (Clint, 2026-10-06: "for when machines get upgraded,
    /// replaced or a person has been using their own private device"; his answer: a machine that is online
    /// takes itself off). Our health check first, while this service can still run it; then the SAME
    /// Uninstall-Client.ps1 a person would run, copied out with its branding — it deletes the folder it would
    /// otherwise be running from — and started on its own, because it stops this service on its way.
    /// </summary>
    private void UninstallSelf(string why, HealthCheckKeeper keeper)
    {
        lock (_lock) { if (_uninstalling) return; _uninstalling = true; }
        _log.LogWarning("Removed from the company's list ({Why}) — uninstalling", why);
        try { var hc = keeper.RemoveForUninstall(); _log.LogInformation("Health check: {Note}", hc.Installed ? "could not be removed: " + hc.Note : "removed or not ours"); }
        catch (Exception ex) { _log.LogWarning(ex, "Health check removal failed; uninstalling anyway"); }
        try
        {
            var (script, args) = ClientUninstall.Command(AppContext.BaseDirectory, Path.GetTempPath());
            if (script == null)
            {
                _log.LogWarning("No Uninstall-Client.ps1 beside the agent (an older install): the machine stays installed, refused by the server");
                return;
            }
            Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = false, CreateNoWindow = true });
            _log.LogInformation("Started the uninstaller from {Script}", script);
        }
        catch (Exception ex) { _log.LogError(ex, "Could not start the uninstaller"); }
    }
    private bool _uninstalling;

    /// <summary>
    /// The company changed its logo, colours, wording or consent settings since this machine was installed
    /// (2026-10-07, roadmap 4.6). Kept in branding\live beside the installed brand.json — which is never rewritten,
    /// because the uninstaller finds the shortcuts by the names in it — and laid over it by every Branding.Load:
    /// the tray app at its next start, the consent prompt at the next session. This service takes the consent
    /// settings now. The desktop shortcut's icon is drawn again from the new logo.
    /// </summary>
    private void TakeBranding(DeviceLink link, BrandingMsg m)
    {
        if (m.Hash == link.BrandHash) return;
        if (!BrandRefresh.Save(_brand.BaseDirectory, m, out var why))
        {
            _log.LogWarning("The company's new branding could not be kept: {Why}", why);
            return;
        }
        BrandRefresh.Reapply(_brand);
        link.BrandHash = m.Hash;
        _log.LogInformation("The company's branding changed: now {Company}, kept in {Dir}", m.Look.CompanyName, BrandRefresh.LiveDir(_brand.BaseDirectory));
        _ = Task.Run(RedrawShortcutIcon);
    }

    /// <summary>
    /// The shortcut icon the installer drew from the logo (Install-Client.ps1, <c>--write-icon</c>), drawn again by the
    /// same code from the new one. Only where there is one: the free version has no shortcuts.
    /// </summary>
    private void RedrawShortcutIcon()
    {
        try
        {
            var ico = Path.Combine(_brand.BaseDirectory, "branding", "app-shortcut.ico");
            var exe = Path.Combine(_brand.BaseDirectory, "RemoteSupport.Client.exe");
            if (!File.Exists(ico) || !File.Exists(exe)) return;
            using var p = Process.Start(new ProcessStartInfo(exe, $"--write-icon \"{ico}\"") { UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return;
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } _log.LogWarning("Drawing the shortcut icon took too long; left as it was"); return; }
            _log.LogInformation(p.ExitCode == 0 ? "Shortcut icon drawn from the new logo" : "Shortcut icon not redrawn (no usable logo, exit {Code})", p.ExitCode);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not redraw the shortcut icon"); }
    }

    /// <summary>
    /// The host's command line for a session. Public and static so tools/noticeprobe can check what a
    /// session asks for reaches the host — the free version's notice most of all.
    /// </summary>
    public static string HostArguments(StartSessionMsg s, string wsBase, bool unattendedRequireConsent)
    {
        var consent = s.AskConsent || unattendedRequireConsent ? "ask" : "auto";
        var tech = (s.TechnicianName ?? "").Replace("\"", "");
        return $"--server {wsBase} --session {s.SessionId} --token {s.HostToken} --consent {consent} --tech \"{tech}\""
            + (string.IsNullOrEmpty(s.VisitorCompany) ? "" : $" --visitor \"{s.VisitorCompany.Replace("\"", "")}\"")
            // "Being controlled remotely" for the whole session — the free version, whose computers have no tray app (2026-10-06).
            + (s.ShowNotice ? " --notice" : "");
    }

    private void StartSession(StartSessionMsg s)
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "RemoteSupport.Host.exe");
            // Asked when the company wants every unattended session asked, and ALWAYS when the
            // person at the machine has not granted unattended access — the hub only hands this
            // service such a session so that it runs as SYSTEM once they say yes.
            var args = HostArguments(s, _wsBase, _brand.UnattendedRequireConsent);

            // Prefer SYSTEM. Only SYSTEM can reach the Winlogon desktop (UAC prompts, the lock
            // screen, Ctrl+Alt+Del) and it outranks every elevated window, so remote control keeps
            // working where a user-token host would silently go dead. Fall back to the user token
            // if that ever fails, so a session still happens rather than none at all.
            int pid; string how;
            try { pid = SessionLauncher.LaunchAsSystemInActiveSession(exe, args); how = "SYSTEM"; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SYSTEM launch failed for session {Session}; falling back to the user token", s.SessionId);
                pid = SessionLauncher.LaunchInActiveSession(exe, args); how = "user token";
            }
            lock (_lock) _sessionPids[s.SessionId] = pid;
            _log.LogInformation("Launched host pid {Pid} as {How} for session {Session} (tech {Tech})", pid, how, s.SessionId, s.TechnicianName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to launch host for session {Session}", s.SessionId);
        }
    }

    /// <summary>
    /// No session host running (auto-update waits for this). A host that died without its session ending is forgotten here,
    /// or one crash would hold every update back for ever.
    /// </summary>
    private bool NobodyConnected()
    {
        lock (_lock)
        {
            foreach (var (session, pid) in _sessionPids.ToList())
            {
                try { using var p = Process.GetProcessById(pid); if (!p.HasExited) continue; } catch (ArgumentException) { }
                _sessionPids.Remove(session);
            }
            return _sessionPids.Count == 0;
        }
    }

    private void EndSession(SessionEndedMsg e)
    {
        int pid; lock (_lock) { _sessionPids.TryGetValue(e.SessionId, out pid); _sessionPids.Remove(e.SessionId); }
        if (pid != 0) { SessionLauncher.Kill(pid); _log.LogInformation("Stopped host pid {Pid} for session {Session}", pid, e.SessionId); }
    }
}

/// <summary>
/// The secure attention sequence. Lives in the agent rather than the host because Windows only
/// accepts it from a service running as LocalSystem — see AgentService.DoSysAction.
/// </summary>
internal static class NativeSas
{
    /// <summary>
    /// sas.dll, shipped with Windows. asUser=false delivers the sequence to the active console
    /// session, which is what a session-0 service wants.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("sas.dll", SetLastError = true)]
    internal static extern void SendSAS([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool asUser);
}
