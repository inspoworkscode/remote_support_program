# Read-only audio diagnostic (plus an optional test tone), run on the remote machine through a
# live session the same way hostinfo.ps1 is (docs/HANDOVER.md 7).
#
# WHY THIS EXISTS. The host runs as SYSTEM in the interactive session (HANDOVER 4). Audio
# endpoints, the default-device choice and the per-application mixer are all things a support
# technician assumes are "the user's". This script answers, by measurement rather than by
# argument, whether a SYSTEM process can SEE the user's playback device, read its mute and
# volume, see which application has it open, and actually MAKE A SOUND come out of it.
#
# Edge refusing to render a PDF as SYSTEM cost two sessions (HANDOVER 17). Do not design the
# audio feature until this has been run and read.
#
# Test locally before sending it anywhere:
#   powershell -NoProfile -ExecutionPolicy Bypass -File audioprobe.ps1
# The tone is off unless the harness turns it on: audioprobe rewrites the marked line below.
$PlayTone = $false   # AUDIOPROBE_TONE_FLAG
$ErrorActionPreference = 'Continue'

function Section($t) { Write-Output ""; Write-Output ("=== " + $t + " ===") }

# ---------------------------------------------------------------------------------------------
# 1. Who are we, and in which session? An endpoint that SYSTEM in session 0 cannot see may be
#    perfectly visible to SYSTEM in session 1 -- so the session id is half the answer.
# ---------------------------------------------------------------------------------------------
Section "identity"
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
Write-Output ("user          : " + $me.Name)
Write-Output ("sid           : " + $me.User.Value)
Write-Output ("is system     : " + $me.IsSystem)
$p = Get-Process -Id $PID
Write-Output ("process       : " + $p.ProcessName + " pid=" + $p.Id + " session=" + $p.SessionId)
try {
    $parent = (Get-CimInstance Win32_Process -Filter ("ProcessId=" + $PID) -ErrorAction SilentlyContinue).ParentProcessId
    $pp = Get-Process -Id $parent -ErrorAction SilentlyContinue
    if ($pp) { Write-Output ("parent        : " + $pp.ProcessName + " pid=" + $pp.Id + " session=" + $pp.SessionId) }
} catch { }
Write-Output ("interactive   : " + [Environment]::UserInteractive)

# ---------------------------------------------------------------------------------------------
# 2. The audio services. If AudioEndpointBuilder is stopped there are no endpoints to find and
#    every other answer below is a consequence, not a cause.
# ---------------------------------------------------------------------------------------------
Section "audio services"
foreach ($n in 'Audiosrv', 'AudioEndpointBuilder') {
    $s = Get-Service -Name $n -ErrorAction SilentlyContinue
    if ($s) { Write-Output ("{0,-22} {1,-9} startup={2}" -f $s.Name, $s.Status, $s.StartType) }
    else { Write-Output ("{0,-22} NOT PRESENT" -f $n) }
}

# ---------------------------------------------------------------------------------------------
# 3. Core Audio. Everything below comes from the MMDevice API -- the same source the Windows
#    sound settings page uses -- so "what Windows thinks" is literally what is reported.
# ---------------------------------------------------------------------------------------------
# Add-Type in Windows PowerShell 5.1 compiles as C# 5: no interpolation, no "out var".
Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RSAudio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PropertyKey { public Guid fmtid; public int pid; }

    // Size = 24 is NOT optional. A PROPVARIANT is 24 bytes on x64; declared without it this
    // struct is 16 and GetValue writes eight bytes past the end of the caller stack slot. The
    // C# port of this code read the friendly name perfectly, returned the adaptor name empty,
    // and started working when a diagnostic print was added -- which is what stack corruption
    // looks like from the outside. This script was getting away with it; do not rely on that.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr p;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int ch, float level, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(int ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(int ch, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(int ch, out float level);
        [PreserveSig] int SetMute(int mute, ref Guid ctx);
        [PreserveSig] int GetMute(out int mute);
        [PreserveSig] int GetVolumeStepInfo(out int step, out int stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid ctx);
        [PreserveSig] int VolumeStepDown(ref Guid ctx);
        [PreserveSig] int QueryHardwareSupport(out int mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float inc);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
        [PreserveSig] int RegisterSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterSessionNotification(IntPtr n);
        [PreserveSig] int RegisterDuckNotification(IntPtr s, IntPtr n);
        [PreserveSig] int UnregisterDuckNotification(IntPtr n);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out int pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(int mute, ref Guid ctx);
        [PreserveSig] int GetMute(out int mute);
    }

    public static class Probe
    {
        // Friendly name, and the name of the physical adaptor behind it. The second one is what
        // tells "Speakers" apart from "Speakers" when there are three of them.
        static PropertyKey PKEY_FriendlyName = MakeKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        static PropertyKey PKEY_InterfaceFriendlyName = MakeKey("b3f8fa53-0004-438e-9003-51a46e139bfc", 6);

        // Releasing a COM wrapper must never be the thing that fails. Marshal.ReleaseComObject
        // threw a NullReferenceException from inside a finally here and swallowed the real
        // answer for every endpoint; tidying up is not worth losing the measurement over.
        static void Rel(object o)
        {
            if (o == null) return;
            try { Marshal.ReleaseComObject(o); } catch { }
        }

        static PropertyKey MakeKey(string guid, int pid)
        {
            PropertyKey k = new PropertyKey();
            k.fmtid = new Guid(guid);
            k.pid = pid;
            return k;
        }

        public static string StateName(int s)
        {
            switch (s)
            {
                case 1: return "ACTIVE";
                case 2: return "DISABLED";
                case 4: return "NOTPRESENT";
                case 8: return "UNPLUGGED";
                default: return "state " + s;
            }
        }

        public static string SessionStateName(int s)
        {
            switch (s)
            {
                case 0: return "inactive";
                case 1: return "ACTIVE (playing)";
                case 2: return "expired";
                default: return "state " + s;
            }
        }

        public static string GetString(IMMDevice dev, PropertyKey key)
        {
            IPropertyStore store = null;
            int hr = dev.OpenPropertyStore(0 /* STGM_READ */, out store);
            if (hr != 0 || store == null) return "(property store hr=0x" + hr.ToString("X8") + ")";
            try
            {
                PropVariant pv;
                hr = store.GetValue(ref key, out pv);
                if (hr != 0) return "(hr=0x" + hr.ToString("X8") + ")";
                if (pv.vt != 31 /* VT_LPWSTR */ || pv.p == IntPtr.Zero) return "(vt=" + pv.vt + ")";
                return Marshal.PtrToStringUni(pv.p);
            }
            finally { Rel(store); }
        }

        public static IMMDeviceEnumerator Enumerator()
        {
            return (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
        }

        public static string Describe(IMMDevice dev)
        {
            string id;
            dev.GetId(out id);
            int state;
            dev.GetState(out state);
            return string.Format("{0,-10}  {1}\n                adaptor: {2}\n                id     : {3}",
                StateName(state), GetString(dev, PKEY_FriendlyName),
                GetString(dev, PKEY_InterfaceFriendlyName), id);
        }

        public static string VolumeOf(IMMDevice dev)
        {
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            IntPtr raw;
            int hr = dev.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out raw);
            if (hr != 0 || raw == IntPtr.Zero)
                return "could not open the volume control: hr=0x" + hr.ToString("X8");
            IAudioEndpointVolume vol = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(raw);
            Marshal.Release(raw);
            try
            {
                float scalar; int mute;
                int h1 = vol.GetMasterVolumeLevelScalar(out scalar);
                int h2 = vol.GetMute(out mute);
                if (h1 != 0 || h2 != 0)
                    return "volume hr=0x" + h1.ToString("X8") + "  mute hr=0x" + h2.ToString("X8");
                return string.Format("volume {0}%   mute {1}", (int)Math.Round(scalar * 100),
                    mute != 0 ? "ON  <-- THIS ALONE WOULD EXPLAIN NO SOUND" : "off");
            }
            finally { Rel(vol); }
        }

        // Which applications hold this endpoint open, and are any muted in the per-app mixer?
        // "Only this one program has no sound" is almost always answered here.
        public static string SessionsOf(IMMDevice dev)
        {
            Guid iid = typeof(IAudioSessionManager2).GUID;
            IntPtr raw;
            int hr = dev.Activate(ref iid, 1, IntPtr.Zero, out raw);
            if (hr != 0 || raw == IntPtr.Zero)
                return "  could not open the session manager: hr=0x" + hr.ToString("X8") +
                       "\n  (a SYSTEM process being refused the user's mixer is a FINDING, not a bug in this script)";
            IAudioSessionManager2 mgr = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(raw);
            Marshal.Release(raw);
            StringBuilder sb = new StringBuilder();
            try
            {
                IAudioSessionEnumerator en;
                hr = mgr.GetSessionEnumerator(out en);
                if (hr != 0 || en == null) return "  session enumerator hr=0x" + hr.ToString("X8");
                int count;
                en.GetCount(out count);
                sb.AppendLine("  " + count + " application audio session(s)");
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 sc;
                    if (en.GetSession(i, out sc) != 0 || sc == null) continue;
                    try
                    {
                        int state, pid;
                        sc.GetState(out state);
                        sc.GetProcessId(out pid);
                        bool sysSounds = sc.IsSystemSoundsSession() == 0;
                        string name = sysSounds ? "(Windows system sounds)" : "pid " + pid;
                        if (!sysSounds && pid > 0)
                        {
                            try
                            {
                                System.Diagnostics.Process pr = System.Diagnostics.Process.GetProcessById(pid);
                                name = pr.ProcessName + " (pid " + pid + ", session " + pr.SessionId + ")";
                            }
                            catch { }
                        }
                        string mixer = "";
                        ISimpleAudioVolume sv = sc as ISimpleAudioVolume;
                        if (sv != null)
                        {
                            float lvl; int m;
                            if (sv.GetMasterVolume(out lvl) == 0 && sv.GetMute(out m) == 0)
                                mixer = string.Format("  mixer {0}%{1}", (int)Math.Round(lvl * 100),
                                    m != 0 ? " MUTED <--" : "");
                        }
                        sb.AppendLine(string.Format("    {0,-40} {1}{2}", name, SessionStateName(state), mixer));
                    }
                    finally { Rel(sc); }
                }
                Rel(en);
            }
            catch (Exception ex) { sb.AppendLine("  " + ex.ToString()); }
            finally { Rel(mgr); }
            return sb.ToString().TrimEnd();
        }

        // ---------------------------------------------------------------------------------
        // The whole walk stays inside C#. Handing an IMMDeviceEnumerator back to PowerShell
        // does not work: PowerShell binds methods against the runtime type of the COM
        // wrapper, not the interface it was cast to, so every call fails with
        // "does not contain a method named EnumAudioEndpoints". Measured, not guessed.
        // ---------------------------------------------------------------------------------
        public static string RenderEndpoints() { return Endpoints(0, "render"); }
        public static string CaptureEndpoints() { return Endpoints(1, "capture"); }

        static string Endpoints(int flow, string label)
        {
            StringBuilder sb = new StringBuilder();
            IMMDeviceEnumerator en;
            try { en = Enumerator(); }
            catch (Exception ex) { return "could not create the device enumerator: " + ex.Message; }
            try
            {
                // State mask 15 = every state, including disabled, unplugged and not-present.
                // A device the user "does not have" is usually present and DISABLED, and that
                // distinction is the whole answer.
                IMMDeviceCollection coll;
                int hr = en.EnumAudioEndpoints(flow, 15, out coll);
                if (hr != 0 || coll == null)
                    return "EnumAudioEndpoints failed hr=0x" + hr.ToString("X8");
                int n;
                coll.GetCount(out n);
                sb.AppendLine(n + " " + label + " endpoint(s), counting disabled and unplugged ones");
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d;
                    if (coll.Item(i, out d) != 0 || d == null) continue;
                    try
                    {
                        sb.AppendLine();
                        sb.AppendLine("  [" + i + "] " + Describe(d));
                        if (flow == 0) sb.AppendLine("                " + VolumeOf(d));
                    }
                    // One unreadable device must not cost us the rest of the list, and the whole
                    // exception is the evidence -- never reduce it to .Message.
                    catch (Exception ex) { sb.AppendLine("  [" + i + "] FAILED: " + ex.ToString()); }
                    finally { Rel(d); }
                }
                Rel(coll);
            }
            catch (Exception ex) { sb.AppendLine(ex.ToString()); }
            finally { Rel(en); }
            return sb.ToString().TrimEnd();
        }

        // THE question for the SYSTEM design. If these come back empty while the user plainly
        // has speakers, SYSTEM is not seeing the user's choice and the feature has to ask the
        // user's own session instead.
        public static string Defaults()
        {
            string[] roles = {
                "Console (system sounds, most apps)",
                "Multimedia (music, video)",
                "Communications (calls)"
            };
            StringBuilder sb = new StringBuilder();
            IMMDeviceEnumerator en;
            try { en = Enumerator(); }
            catch (Exception ex) { return "could not create the device enumerator: " + ex.Message; }
            try
            {
                for (int r = 0; r < 3; r++)
                {
                    sb.AppendLine();
                    sb.AppendLine("  " + roles[r]);
                    IMMDevice d;
                    int hr = en.GetDefaultAudioEndpoint(0, r, out d);
                    if (hr != 0 || d == null)
                    {
                        // 0x80070490 = ERROR_NOT_FOUND: no default, i.e. no usable playback device.
                        string why = hr == unchecked((int)0x80070490)
                            ? "   -> NO DEFAULT PLAYBACK DEVICE AT ALL (ERROR_NOT_FOUND)" : "";
                        sb.AppendLine("    none: hr=0x" + hr.ToString("X8") + why);
                        continue;
                    }
                    try
                    {
                        sb.AppendLine("    " + Describe(d));
                        sb.AppendLine("                " + VolumeOf(d));
                        if (r == 1)
                        {
                            sb.AppendLine();
                            sb.AppendLine("  applications holding this device open:");
                            sb.AppendLine(SessionsOf(d));
                        }
                    }
                    finally { Rel(d); }
                }
            }
            catch (Exception ex) { sb.AppendLine(ex.ToString()); }
            finally { Rel(en); }
            return sb.ToString().TrimEnd();
        }
    }
}
'@

Section "playback endpoints Windows can see from here"
Write-Output ([RSAudio.Probe]::RenderEndpoints())

Section "the default playback device, per role"
Write-Output ([RSAudio.Probe]::Defaults())

Section "recording endpoints (for completeness)"
Write-Output ([RSAudio.Probe]::CaptureEndpoints())

# ---------------------------------------------------------------------------------------------
# 4. The driver's own view. An endpoint can be perfectly visible and still be attached to a
#    device Windows has flagged with a problem code -- the "yellow triangle" case.
# ---------------------------------------------------------------------------------------------
Section "sound devices in device manager terms"
$media = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
    Where-Object { $_.PNPClass -eq 'MEDIA' -or $_.PNPClass -eq 'AudioEndpoint' }
if (-not $media) { Write-Output "none found" }
foreach ($m in $media) {
    $flag = ''
    if ($m.ConfigManagerErrorCode -ne 0) { $flag = "   <-- PROBLEM CODE " + $m.ConfigManagerErrorCode }
    Write-Output ("  {0,-14} {1}{2}" -f $m.PNPClass, $m.Name, $flag)
    if ($m.ConfigManagerErrorCode -ne 0) { Write-Output ("                 status: " + $m.Status) }
}

# ---------------------------------------------------------------------------------------------
# 5. Can a process with THIS token actually make a noise? Reading the mixer and being allowed to
#    render into it are different permissions. A 1 kHz tone through the default endpoint is the
#    only honest test, and it is also the feature Clint asked for: play something and ask the
#    customer whether they heard it.
# ---------------------------------------------------------------------------------------------
Section "test tone"
if (-not $PlayTone) {
    Write-Output "skipped (the harness did not ask for one; pass --tone to play it)"
} else {
    try {
        $rate = 44100
        $secs = 2
        $freq = 1000
        $samples = $rate * $secs
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        $dataBytes = $samples * 2
        $bw.Write([char[]]'RIFF'); $bw.Write([int](36 + $dataBytes))
        $bw.Write([char[]]'WAVE'); $bw.Write([char[]]'fmt ')
        $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
        $bw.Write([int]$rate); $bw.Write([int]($rate * 2))
        $bw.Write([int16]2); $bw.Write([int16]16)
        $bw.Write([char[]]'data'); $bw.Write([int]$dataBytes)
        $fade = $rate * 0.05
        for ($i = 0; $i -lt $samples; $i++) {
            # Fade the first and last 50 ms, so it is a tone and not a click at each end.
            $envl = 1.0
            if ($i -lt $fade) { $envl = $i / $fade }
            elseif ($i -gt ($samples - $fade)) { $envl = ($samples - $i) / $fade }
            $v = [int](12000 * $envl * [Math]::Sin(2 * [Math]::PI * $freq * $i / $rate))
            $bw.Write([int16]$v)
        }
        $bw.Flush()
        [void]$ms.Seek(0, 'Begin')
        Write-Output "playing a 2-second 1 kHz tone on the default playback device..."
        $player = New-Object System.Media.SoundPlayer($ms)
        $player.PlaySync()
        Write-Output "the call returned without error -- a process with this token IS allowed to render audio."
        Write-Output "whether a human heard it is the other half of the answer; ask the customer."
    } catch {
        Write-Output ("the tone FAILED: " + $_.Exception.GetType().Name + ": " + $_.Exception.Message)
        Write-Output "a process with this token cannot render audio -- the test tone would need the user's session."
    }
}

Write-Output ""
Write-Output "=== end of audio probe ==="
