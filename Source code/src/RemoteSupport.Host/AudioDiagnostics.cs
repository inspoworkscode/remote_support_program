using System.Runtime.InteropServices;
using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>
/// Answers the question streaming cannot: <b>why</b> is this machine silent?
///
/// Hearing a customer's machine tells a technician that nothing is coming out. It does not tell
/// them that the default playback device is an HDMI television nobody is watching, that the
/// endpoint is muted, that one application is muted in the mixer while everything else is fine,
/// or that the audio service is stopped. Neither TeamViewer nor AnyDesk answers any of that —
/// they stream the sound and leave you guessing. This is the differentiator.
///
/// It is not a guess either: this is the same MMDevice API the Windows sound settings page uses,
/// so "what Windows thinks" is literally what is reported. It was proven first as a PowerShell
/// probe (tools/hostinfo/audioprobe.ps1), which found a real fault on its first run — the test
/// machine was muted AND defaulting to a television, which is the two most common "my sound
/// stopped working" calls in one machine.
///
/// The host runs as SYSTEM. That matters and was measured before this was written: a SYSTEM
/// process in the interactive session CAN see the endpoints, their volumes and the per-application
/// mixer, including processes in the logged-on user's session. Audio endpoints are per-session and
/// the host is in session 1, which is why. Do not assume the same of any other user-level API —
/// see the Edge lesson in HANDOVER §17.
/// </summary>
public static class AudioDiagnostics
{
    public static AudioDevicesMsg Collect()
    {
        var msg = new AudioDevicesMsg();
        try
        {
            msg.Services = ReadServices();

            AudioInterop.IMMDeviceEnumerator? en = null;
            try
            {
                en = AudioInterop.Enumerator();
                string defConsole = DefaultId(en, AudioInterop.EConsole);
                string defMulti = DefaultId(en, AudioInterop.EMultimedia);
                string defComms = DefaultId(en, AudioInterop.ECommunications);

                AddEndpoints(en, AudioInterop.ERender, msg, defConsole, defMulti, defComms);
                AddEndpoints(en, AudioInterop.ECapture, msg, "", "", "");

                // The per-application mixer is read for the MULTIMEDIA default, because that is
                // where music and video go and it is the one people complain about.
                if (defMulti.Length > 0) msg.Apps = AppsOf(en, defMulti);
            }
            finally { AudioInterop.Rel(en); }

            if (msg.Devices.Count == 0 && msg.Error.Length == 0)
                msg.Error = "Windows reports no audio endpoints at all on that machine, " +
                            "not even disabled ones. Check the audio services above first.";
        }
        catch (Exception ex)
        {
            // The whole exception, never just .Message: a COM failure's type and stack are the
            // evidence, and `catch { }` has cost more than a day on this project already.
            msg.Error = ex.ToString();
            HostTrace.Write("audio diagnostics failed — " + ex);
        }
        return msg;
    }

    private static string DefaultId(AudioInterop.IMMDeviceEnumerator en, int role)
    {
        AudioInterop.IMMDevice? d = null;
        try
        {
            if (en.GetDefaultAudioEndpoint(AudioInterop.ERender, role, out d) != 0 || d == null) return "";
            return d.GetId(out var id) == 0 && id != null ? id : "";
        }
        catch { return ""; }
        finally { AudioInterop.Rel(d); }
    }

    private static void AddEndpoints(AudioInterop.IMMDeviceEnumerator en, int flow, AudioDevicesMsg msg,
                                     string defConsole, string defMulti, string defComms)
    {
        AudioInterop.IMMDeviceCollection? coll = null;
        try
        {
            // Every state, including disabled, unplugged and not present. A device the customer
            // "does not have" is usually present and DISABLED, and that distinction is the answer.
            if (en.EnumAudioEndpoints(flow, AudioInterop.StateMaskAll, out coll) != 0 || coll == null) return;
            coll.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                AudioInterop.IMMDevice? d = null;
                try
                {
                    if (coll.Item(i, out d) != 0 || d == null) continue;
                    d.GetId(out var id);
                    d.GetState(out int state);
                    var info = new AudioDeviceInfo
                    {
                        Id = id ?? "",
                        Name = AudioInterop.GetString(d, AudioInterop.PkeyFriendlyName),
                        Adaptor = AudioInterop.GetString(d, AudioInterop.PkeyInterfaceFriendlyName),
                        State = AudioInterop.StateName(state),
                        IsCapture = flow == AudioInterop.ECapture,
                        IsDefaultConsole = id != null && id == defConsole,
                        IsDefaultMultimedia = id != null && id == defMulti,
                        IsDefaultCommunications = id != null && id == defComms,
                    };
                    // Only playback endpoints carry a volume worth showing; asking a disabled or
                    // absent device for one fails, and that failure is noise rather than a finding.
                    if (flow == AudioInterop.ERender && state == 1)
                    {
                        AudioInterop.ReadVolume(d, out bool muted, out int pct, out string err);
                        info.Muted = muted; info.VolumePercent = pct; info.Error = err;
                    }
                    msg.Devices.Add(info);
                }
                // One unreadable device must not cost the rest of the list.
                catch (Exception ex) { HostTrace.Write($"audio diagnostics: endpoint {i} failed — {ex.Message}"); }
                finally { AudioInterop.Rel(d); }
            }
        }
        finally { AudioInterop.Rel(coll); }
    }

    /// <summary>
    /// What is holding the playback device open, and what the per-application mixer is doing to
    /// each one. "Only this one program has no sound" is almost always answered here.
    /// </summary>
    private static List<AudioAppSession> AppsOf(AudioInterop.IMMDeviceEnumerator en, string deviceId)
    {
        var list = new List<AudioAppSession>();
        AudioInterop.IMMDevice? dev = null;
        AudioInterop.IAudioSessionManager2? mgr = null;
        AudioInterop.IAudioSessionEnumerator? sessions = null;
        try
        {
            if (en.GetDevice(deviceId, out dev) != 0 || dev == null) return list;
            var iid = typeof(AudioInterop.IAudioSessionManager2).GUID;
            if (dev.Activate(ref iid, AudioInterop.ClsCtxInproc, IntPtr.Zero, out var raw) != 0 || raw == IntPtr.Zero)
            {
                // A SYSTEM process being refused the user's mixer would be a FINDING, not a bug.
                // Measured 2026-09-13: it is not refused. If that ever changes, this line is why.
                HostTrace.Write("audio diagnostics: the session manager would not open");
                return list;
            }
            mgr = (AudioInterop.IAudioSessionManager2)Marshal.GetObjectForIUnknown(raw);
            Marshal.Release(raw);

            if (mgr.GetSessionEnumerator(out sessions) != 0 || sessions == null) return list;
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                AudioInterop.IAudioSessionControl2? sc = null;
                try
                {
                    if (sessions.GetSession(i, out sc) != 0 || sc == null) continue;
                    sc.GetState(out int state);
                    sc.GetProcessId(out int pid);
                    bool sysSounds = sc.IsSystemSoundsSession() == 0;
                    var app = new AudioAppSession
                    {
                        Pid = pid,
                        State = AudioInterop.SessionStateName(state),
                        IsSystemSounds = sysSounds,
                        Name = sysSounds ? "Windows system sounds" : "pid " + pid,
                    };
                    if (!sysSounds && pid > 0)
                    {
                        try
                        {
                            using var pr = System.Diagnostics.Process.GetProcessById(pid);
                            app.Name = pr.ProcessName;
                        }
                        catch { }   // it exited between enumerating and asking; "pid N" will do
                    }
                    if (sc is AudioInterop.ISimpleAudioVolume sv)
                    {
                        if (sv.GetMasterVolume(out float lvl) == 0) app.VolumePercent = (int)Math.Round(lvl * 100);
                        if (sv.GetMute(out int m) == 0) app.Muted = m != 0;
                    }
                    list.Add(app);
                }
                catch (Exception ex) { HostTrace.Write($"audio diagnostics: app session {i} failed — {ex.Message}"); }
                finally { AudioInterop.Rel(sc); }
            }
        }
        catch (Exception ex) { HostTrace.Write("audio diagnostics: mixer failed — " + ex.Message); }
        finally { AudioInterop.Rel(sessions); AudioInterop.Rel(mgr); AudioInterop.Rel(dev); }
        return list;
    }

    // ------------------------------------------------------------------------------- services

    /// <summary>
    /// Audiosrv and AudioEndpointBuilder. If the endpoint builder is stopped there are no
    /// endpoints to find and every other answer above is a consequence rather than a cause.
    ///
    /// Read through the service control manager directly rather than with
    /// System.ServiceProcess.ServiceController: that is a NuGet package, the host is published
    /// self-contained and single-file and goes onto every customer machine, and thirty lines of
    /// P/Invoke is cheaper than a dependency. Nothing is bought in.
    /// </summary>
    private static List<string> ReadServices()
    {
        var result = new List<string>();
        IntPtr scm = IntPtr.Zero;
        try
        {
            scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero) { result.Add("the service manager could not be opened"); return result; }
            foreach (var name in new[] { "Audiosrv", "AudioEndpointBuilder" })
            {
                IntPtr svc = IntPtr.Zero;
                try
                {
                    svc = OpenService(scm, name, ServiceQueryStatus);
                    if (svc == IntPtr.Zero) { result.Add($"{name}: NOT PRESENT"); continue; }
                    var st = new ServiceStatus();
                    if (!QueryServiceStatus(svc, ref st)) { result.Add($"{name}: status unreadable"); continue; }
                    result.Add($"{name}: {StateWord(st.dwCurrentState)}");
                }
                finally { if (svc != IntPtr.Zero) CloseServiceHandle(svc); }
            }
        }
        catch (Exception ex) { result.Add("services could not be read: " + ex.Message); }
        finally { if (scm != IntPtr.Zero) CloseServiceHandle(scm); }
        return result;
    }

    private static string StateWord(int s) => s switch
    {
        1 => "STOPPED",
        2 => "starting",
        3 => "stopping",
        4 => "running",
        5 => "continue pending",
        6 => "pausing",
        7 => "PAUSED",
        _ => "state " + s
    };

    private const int ScManagerConnect = 0x0001;
    private const int ServiceQueryStatus = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string serviceName, int access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, ref ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
