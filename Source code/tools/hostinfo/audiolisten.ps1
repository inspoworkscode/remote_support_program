# Loopback CAPTURE measurement -- the day-one question in docs/NEXT-SESSION-AUDIO.md.
#
# WHY THIS EXISTS, SEPARATELY FROM audioprobe.ps1. audioprobe proved that SYSTEM in session 1 can
# SEE the endpoints and can RENDER a tone. Rendering is not capturing: WASAPI loopback is a
# different permission and a different code path, and the whole audio feature depends on it.
# Nothing downstream -- transport, jitter buffer, codec -- is worth designing until this has run.
#
# It answers all four day-one measurements in one pass:
#   1. can this token open IAudioClient::Initialize with AUDCLNT_STREAMFLAGS_LOOPBACK?
#   2. does a MUTED endpoint still deliver audio to loopback? (pass --unmute to measure it)
#   3. what arrives when nothing is playing -- silence, or no packets at all?
#   4. what is the device's mix format?
#
# It does not trust its own ears. It plays a known 1 kHz tone while capturing and runs a Goertzel
# filter over the captured samples: if the energy at 1 kHz is far above its neighbours, the bytes
# we captured really are the sound the machine made. That is the treecheck rule applied to audio --
# do not let a transfer mark its own homework.
#
# Test locally before sending it anywhere:
#   powershell -NoProfile -ExecutionPolicy Bypass -File audiolisten.ps1
#
# The harness rewrites the two marked lines below; the host runs scripts with -File and no
# arguments, so a parameter cannot be passed in (audioprobe.ps1 carries the same pattern).
$Unmute = $false   # AUDIOLISTEN_UNMUTE_FLAG
$Seconds = 3   # AUDIOLISTEN_SECONDS
$ErrorActionPreference = 'Continue'

function Section($t) { Write-Output ""; Write-Output ("=== " + $t + " ===") }

Section "identity"
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
Write-Output ("user          : " + $me.Name)
Write-Output ("is system     : " + $me.IsSystem)
$p = Get-Process -Id $PID
Write-Output ("process       : " + $p.ProcessName + " pid=" + $p.Id + " session=" + $p.SessionId)
try {
    $parent = (Get-CimInstance Win32_Process -Filter ("ProcessId=" + $PID) -ErrorAction SilentlyContinue).ParentProcessId
    $pp = Get-Process -Id $parent -ErrorAction SilentlyContinue
    if ($pp) { Write-Output ("parent        : " + $pp.ProcessName + " pid=" + $pp.Id + " session=" + $pp.SessionId) }
} catch { }

# Add-Type in Windows PowerShell 5.1 compiles as C# 5: no string interpolation, no "out var",
# no expression-bodied members. Every COM walk stays inside C# -- PowerShell binds methods
# against the runtime type of a COM wrapper rather than the interface it was cast to, so handing
# an IMMDevice back to PowerShell does not work (measured; see audioprobe.ps1).
Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RSListen
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
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

    // The vtable order here is the whole interface: get one method out of sequence and the calls
    // silently land on the wrong slot. This is the documented order from Audioclient.h.
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration,
                                     long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long hnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, out IntPtr ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFrames, out uint flags,
                                    out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    public class Capture
    {
        public int Hr;                  // non-zero means Activate, Initialize or Start refused us
        public string Where = "";       // which call refused, in words
        public int Packets;
        public int EmptyPolls;          // polls that found no packet at all -- measurement 3
        public int SilentPackets;       // packets flagged AUDCLNT_BUFFERFLAGS_SILENT
        public long Bytes;
        public long Frames;
        public double Peak;
        public double Rms;
        public double Tone1k;           // Goertzel magnitude at 1 kHz
        public double Tone700;          // and at two neighbours, for contrast
        public double Tone1300;
        public string Format = "";
        public int SampleRate;
        public int Channels;
    }

    public static class Probe
    {
        static PropertyKey PKEY_FriendlyName = MakeKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14);

        static PropertyKey MakeKey(string guid, int pid)
        {
            PropertyKey k = new PropertyKey();
            k.fmtid = new Guid(guid);
            k.pid = pid;
            return k;
        }

        // Releasing a COM wrapper must never be the thing that fails: Marshal.ReleaseComObject
        // threw from inside a finally in audioprobe and swallowed the real answer.
        static void Rel(object o)
        {
            if (o == null) return;
            try { Marshal.ReleaseComObject(o); } catch { }
        }

        static IMMDeviceEnumerator Enumerator()
        {
            return (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
        }

        static IMMDevice DefaultRender(out string why)
        {
            why = "";
            IMMDeviceEnumerator en;
            try { en = Enumerator(); }
            catch (Exception ex) { why = "could not create the device enumerator: " + ex.Message; return null; }
            try
            {
                IMMDevice d;
                // eRender = 0, eMultimedia = 1: what music and video actually come out of.
                int hr = en.GetDefaultAudioEndpoint(0, 1, out d);
                if (hr != 0 || d == null)
                {
                    why = "no default render endpoint: hr=0x" + hr.ToString("X8");
                    return null;
                }
                return d;
            }
            finally { Rel(en); }
        }

        public static string DeviceName()
        {
            string why;
            IMMDevice d = DefaultRender(out why);
            if (d == null) return why;
            try
            {
                IPropertyStore store;
                if (d.OpenPropertyStore(0, out store) != 0 || store == null) return "(no property store)";
                try
                {
                    PropVariant pv;
                    if (store.GetValue(ref PKEY_FriendlyName, out pv) != 0) return "(no name)";
                    if (pv.vt != 31 || pv.p == IntPtr.Zero) return "(vt=" + pv.vt + ")";
                    return Marshal.PtrToStringUni(pv.p);
                }
                finally { Rel(store); }
            }
            finally { Rel(d); }
        }

        // ------------------------------------------------------------------------------------
        // Mute is read and written on the ENDPOINT, which is exactly the state a technician
        // needs shown next to the listen button if it turns out to silence loopback.
        // ------------------------------------------------------------------------------------
        static IAudioEndpointVolume Volume(IMMDevice dev)
        {
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            IntPtr raw;
            if (dev.Activate(ref iid, 1, IntPtr.Zero, out raw) != 0 || raw == IntPtr.Zero) return null;
            IAudioEndpointVolume v = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(raw);
            Marshal.Release(raw);
            return v;
        }

        public static int GetMute()
        {
            string why;
            IMMDevice d = DefaultRender(out why);
            if (d == null) return -1;
            IAudioEndpointVolume v = null;
            try
            {
                v = Volume(d);
                if (v == null) return -1;
                int m;
                if (v.GetMute(out m) != 0) return -1;
                return m;
            }
            finally { Rel(v); Rel(d); }
        }

        public static int GetVolumePercent()
        {
            string why;
            IMMDevice d = DefaultRender(out why);
            if (d == null) return -1;
            IAudioEndpointVolume v = null;
            try
            {
                v = Volume(d);
                if (v == null) return -1;
                float f;
                if (v.GetMasterVolumeLevelScalar(out f) != 0) return -1;
                return (int)Math.Round(f * 100);
            }
            finally { Rel(v); Rel(d); }
        }

        public static string SetMute(int mute)
        {
            string why;
            IMMDevice d = DefaultRender(out why);
            if (d == null) return why;
            IAudioEndpointVolume v = null;
            try
            {
                v = Volume(d);
                if (v == null) return "could not open the volume control";
                Guid ctx = Guid.Empty;
                int hr = v.SetMute(mute, ref ctx);
                if (hr != 0) return "SetMute hr=0x" + hr.ToString("X8");
                return "ok";
            }
            finally { Rel(v); Rel(d); }
        }

        // ------------------------------------------------------------------------------------
        // The measurement itself. Loopback capture is opened on the RENDER endpoint -- there is
        // no separate "what the speakers are playing" device; you ask the speakers to hand back
        // the mix they are being fed.
        // ------------------------------------------------------------------------------------
        public static Capture Listen(int milliseconds)
        {
            Capture c = new Capture();
            string why;
            IMMDevice dev = DefaultRender(out why);
            if (dev == null) { c.Hr = -1; c.Where = why; return c; }

            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            IntPtr fmt = IntPtr.Zero;
            bool started = false;
            try
            {
                Guid iid = typeof(IAudioClient).GUID;
                IntPtr raw;
                int hr = dev.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out raw);
                if (hr != 0 || raw == IntPtr.Zero)
                {
                    c.Hr = hr; c.Where = "IMMDevice::Activate(IAudioClient)";
                    return c;
                }
                client = (IAudioClient)Marshal.GetObjectForIUnknown(raw);
                Marshal.Release(raw);

                hr = client.GetMixFormat(out fmt);
                if (hr != 0 || fmt == IntPtr.Zero)
                {
                    c.Hr = hr; c.Where = "IAudioClient::GetMixFormat";
                    return c;
                }

                // WAVEFORMATEX is 18 bytes (cbSize at 16); WAVEFORMATEXTENSIBLE then carries
                // Samples at 18, dwChannelMask at 20 and the SubFormat GUID at 24. Offset 26 was
                // tried first and read a GUID that CHANGED BETWEEN RUNS -- which is what an
                // unaligned read of a struct looks like, and is worth recognising on sight.
                int tag = Marshal.ReadInt16(fmt, 0) & 0xFFFF;
                int channels = Marshal.ReadInt16(fmt, 2) & 0xFFFF;
                int rate = Marshal.ReadInt32(fmt, 4);
                int blockAlign = Marshal.ReadInt16(fmt, 12) & 0xFFFF;
                int bits = Marshal.ReadInt16(fmt, 14) & 0xFFFF;
                bool isFloat = (tag == 3);
                string tagName = "tag " + tag;
                if (tag == 1) tagName = "PCM";
                else if (tag == 3) tagName = "IEEE float";
                else if (tag == 0xFFFE)
                {
                    byte[] g = new byte[16];
                    Marshal.Copy(new IntPtr(fmt.ToInt64() + 24), g, 0, 16);
                    Guid sub = new Guid(g);
                    if (sub == new Guid("00000003-0000-0010-8000-00aa00389b71")) { isFloat = true; tagName = "EXTENSIBLE/IEEE float"; }
                    else if (sub == new Guid("00000001-0000-0010-8000-00aa00389b71")) { tagName = "EXTENSIBLE/PCM"; }
                    else tagName = "EXTENSIBLE/" + sub.ToString();
                }
                c.Format = tagName + "  " + rate + " Hz  " + channels + " ch  " + bits + "-bit  blockAlign=" + blockAlign;
                c.SampleRate = rate;
                c.Channels = channels;

                // Refuse a format we cannot decode, loudly and before capturing anything. The
                // first version of this quietly filled its sample array with zeroes instead and
                // reported "300 packets, peak 0" -- a capture that looks like a silent machine
                // when in fact the READER was broken. That is exactly the silent failure the
                // working agreements exist to prevent.
                if (!(isFloat && bits == 32) && bits != 16)
                {
                    c.Hr = -3;
                    c.Where = "mix format is " + tagName + " at " + bits +
                              " bits, which this probe cannot decode -- the numbers below would be lies";
                    return c;
                }

                // AUDCLNT_SHAREMODE_SHARED = 0, AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000.
                // One second of buffer; periodicity must be 0 in shared mode.
                hr = client.Initialize(0, 0x00020000, 10000000L, 0L, fmt, IntPtr.Zero);
                if (hr != 0)
                {
                    c.Hr = hr;
                    c.Where = "IAudioClient::Initialize with AUDCLNT_STREAMFLAGS_LOOPBACK"
                            + "  <-- THIS IS THE ANSWER THE FEATURE DEPENDS ON";
                    return c;
                }

                Guid capIid = typeof(IAudioCaptureClient).GUID;
                IntPtr capRaw;
                hr = client.GetService(ref capIid, out capRaw);
                if (hr != 0 || capRaw == IntPtr.Zero)
                {
                    c.Hr = hr; c.Where = "IAudioClient::GetService(IAudioCaptureClient)";
                    return c;
                }
                capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capRaw);
                Marshal.Release(capRaw);

                hr = client.Start();
                if (hr != 0) { c.Hr = hr; c.Where = "IAudioClient::Start"; return c; }
                started = true;

                // Keep a mono downmix for the Goertzel. Cap it so a long listen cannot eat memory.
                List<double> mono = new List<double>();
                int maxMono = rate * 8;

                DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
                while (DateTime.UtcNow < end)
                {
                    uint next;
                    if (capture.GetNextPacketSize(out next) != 0) break;
                    if (next == 0)
                    {
                        // Measurement 3: when nothing is playing this is what happens -- no
                        // packet at all, not a packet of zeroes. A pipeline that waits for a
                        // packet stalls here and looks broken.
                        c.EmptyPolls++;
                        System.Threading.Thread.Sleep(10);
                        continue;
                    }
                    IntPtr data;
                    uint frames, flags;
                    long devPos, qpc;
                    if (capture.GetBuffer(out data, out frames, out flags, out devPos, out qpc) != 0) break;
                    c.Packets++;
                    c.Frames += frames;
                    c.Bytes += (long)frames * blockAlign;
                    bool silentFlag = (flags & 0x2) != 0;   // AUDCLNT_BUFFERFLAGS_SILENT
                    if (silentFlag) c.SilentPackets++;
                    if (!silentFlag && data != IntPtr.Zero && frames > 0)
                    {
                        int n = (int)frames * channels;
                        double[] s = new double[n];
                        if (isFloat && bits == 32)
                        {
                            float[] f = new float[n];
                            Marshal.Copy(data, f, 0, n);
                            for (int i = 0; i < n; i++) s[i] = f[i];
                        }
                        else if (bits == 16)
                        {
                            short[] sh = new short[n];
                            Marshal.Copy(data, sh, 0, n);
                            for (int i = 0; i < n; i++) s[i] = sh[i] / 32768.0;
                        }
                        // No else: an undecodable width was refused above, before Start.
                        for (int f2 = 0; f2 < (int)frames; f2++)
                        {
                            double sum = 0;
                            for (int ch = 0; ch < channels; ch++) sum += s[f2 * channels + ch];
                            double m = sum / channels;
                            double a = Math.Abs(m);
                            if (a > c.Peak) c.Peak = a;
                            c.Rms += m * m;
                            if (mono.Count < maxMono) mono.Add(m);
                        }
                    }
                    capture.ReleaseBuffer(frames);
                }

                if (c.Frames > 0) c.Rms = Math.Sqrt(c.Rms / c.Frames);
                c.Tone1k = Goertzel(mono, rate, 1000);
                c.Tone700 = Goertzel(mono, rate, 700);
                c.Tone1300 = Goertzel(mono, rate, 1300);
                return c;
            }
            catch (Exception ex)
            {
                c.Hr = -2;
                c.Where = ex.ToString();
                return c;
            }
            finally
            {
                if (started && client != null) { try { client.Stop(); } catch { } }
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
                Rel(capture);
                Rel(client);
                Rel(dev);
            }
        }

        // A dozen lines, no FFT library, and it is the difference between "I think I heard
        // something" and a pass or a fail at two in the morning with the speakers muted.
        // Returns the normalised magnitude at one frequency.
        public static double Goertzel(List<double> x, int rate, double freq)
        {
            int n = x.Count;
            if (n < 256) return 0;
            double k = Math.Round(n * freq / rate);
            double w = 2.0 * Math.PI * k / n;
            double coeff = 2.0 * Math.Cos(w);
            double s1 = 0, s2 = 0;
            for (int i = 0; i < n; i++)
            {
                double s0 = x[i] + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            if (power < 0) power = 0;
            return Math.Sqrt(power) / (n / 2.0);
        }
    }
}
'@

# ---------------------------------------------------------------------------------------------
# The tone. Played from this same process, on a background thread, while the capture runs -- so
# a non-zero reading proves the loopback stream carries what the machine is actually playing.
# ---------------------------------------------------------------------------------------------
function New-ToneStream([int]$rate, [double]$secs, [int]$freq) {
    $samples = [int]($rate * $secs)
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
    return $ms
}

function Show-Capture($label, $c) {
    Write-Output ("  " + $label)
    if ($c.Hr -ne 0) {
        Write-Output ("    FAILED at " + $c.Where)
        Write-Output ("    hr=0x" + ('{0:X8}' -f $c.Hr))
        return
    }
    Write-Output ("    format      : " + $c.Format)
    Write-Output ("    packets     : " + $c.Packets + "   empty polls: " + $c.EmptyPolls + "   silent-flagged: " + $c.SilentPackets)
    Write-Output ("    captured    : " + $c.Bytes + " bytes, " + $c.Frames + " frames")
    if ($c.SampleRate -gt 0) {
        $secsCaptured = [Math]::Round($c.Frames / $c.SampleRate, 3)
        Write-Output ("    that is     : " + $secsCaptured + " s of audio")
    }
    Write-Output ("    peak        : " + [Math]::Round($c.Peak, 5) + "   rms: " + [Math]::Round($c.Rms, 5))
    Write-Output ("    goertzel    : 1000 Hz=" + [Math]::Round($c.Tone1k, 5) +
                  "   700 Hz=" + [Math]::Round($c.Tone700, 5) +
                  "   1300 Hz=" + [Math]::Round($c.Tone1300, 5))
    $neighbour = [Math]::Max($c.Tone700, $c.Tone1300)
    if ($c.Tone1k -gt 0.001 -and $c.Tone1k -gt ($neighbour * 8)) {
        Write-Output "    VERDICT     : the captured audio CONTAINS the 1 kHz tone. Loopback works."
    } elseif ($c.Frames -eq 0) {
        Write-Output "    VERDICT     : the stream opened but delivered nothing at all."
    } else {
        Write-Output "    VERDICT     : no 1 kHz peak above its neighbours -- captured, but not the tone."
    }
}

Section "the endpoint we will capture from"
Write-Output ("device        : " + [RSListen.Probe]::DeviceName())
$mute0 = [RSListen.Probe]::GetMute()
$vol0 = [RSListen.Probe]::GetVolumePercent()
Write-Output ("volume        : " + $vol0 + "%")
if ($mute0 -eq 1) { Write-Output "mute          : ON" }
elseif ($mute0 -eq 0) { Write-Output "mute          : off" }
else { Write-Output "mute          : unreadable" }

# ---------------------------------------------------------------------------------------------
# Measurement 3 first, because it is free: what arrives when nothing is playing?
# ---------------------------------------------------------------------------------------------
Section "capture with nothing playing"
$quiet = [RSListen.Probe]::Listen(1500)
Show-Capture "1.5 s of silence" $quiet
if ($quiet.Hr -eq 0) {
    if ($quiet.Packets -eq 0) {
        Write-Output ""
        Write-Output "  -> WASAPI delivered NO PACKETS AT ALL, not packets of zeroes."
        Write-Output "     A send loop that waits for a packet will stall here and look broken."
    } else {
        Write-Output ""
        Write-Output ("  -> packets did arrive while nothing was playing (" + $quiet.SilentPackets + " flagged silent).")
    }
}

# ---------------------------------------------------------------------------------------------
# Measurements 1 and 4, and half of 2: capture WHILE a known tone plays, at whatever the mute
# state currently is. On the test machine that is muted, which is the interesting case.
# ---------------------------------------------------------------------------------------------
Section "capture while a 1 kHz tone plays"
$ms = New-ToneStream 44100 ($Seconds + 1) 1000
$player = New-Object System.Media.SoundPlayer($ms)
try { $player.Play() } catch { Write-Output ("  the tone could not be started: " + $_.Exception.Message) }
Start-Sleep -Milliseconds 250
$withTone = [RSListen.Probe]::Listen($Seconds * 1000)
try { $player.Stop() } catch { }
if ($mute0 -eq 1) { Show-Capture "as found, mute=ON" $withTone } else { Show-Capture "as found, mute=off" $withTone }

# ---------------------------------------------------------------------------------------------
# The rest of measurement 2. This CHANGES the customer's mute state, so it only happens when the
# harness asks, and the original state is put back in a finally -- leaving a customer's speakers
# unmuted at eleven at night is a real consequence.
# ---------------------------------------------------------------------------------------------
Section "the muted-endpoint question"
if ($mute0 -ne 1) {
    Write-Output "the endpoint is not muted, so there is nothing to compare against here."
    Write-Output "re-run this on a machine whose speakers are muted, or mute them and run again."
} elseif (-not $Unmute) {
    Write-Output "the endpoint IS muted and the reading above was taken through that mute."
    Write-Output "pass --unmute to briefly unmute, capture again, and put the mute back."
} else {
    Write-Output "unmuting, capturing again, then restoring the mute..."
    try {
        $r = [RSListen.Probe]::SetMute(0)
        if ($r -ne 'ok') { Write-Output ("  could not unmute: " + $r) }
        else {
            Start-Sleep -Milliseconds 300
            $ms2 = New-ToneStream 44100 ($Seconds + 1) 1000
            $player2 = New-Object System.Media.SoundPlayer($ms2)
            try { $player2.Play() } catch { }
            Start-Sleep -Milliseconds 250
            $unmuted = [RSListen.Probe]::Listen($Seconds * 1000)
            try { $player2.Stop() } catch { }
            Show-Capture "unmuted" $unmuted
            Write-Output ""
            Write-Output ("  muted rms   : " + [Math]::Round($withTone.Rms, 5))
            Write-Output ("  unmuted rms : " + [Math]::Round($unmuted.Rms, 5))
            if ($withTone.Rms -lt ($unmuted.Rms / 10) -and $unmuted.Rms -gt 0.0001) {
                Write-Output "  -> MUTING SILENCES LOOPBACK. The endpoint's mute MUST be shown next to the"
                Write-Output "     listen button, or technicians will chase ghosts."
            } elseif ($withTone.Rms -gt 0.0001) {
                Write-Output "  -> loopback still carries audio through a mute. Silence means silence."
            } else {
                Write-Output "  -> inconclusive: neither capture had signal. Look at the two blocks above."
            }
        }
    } finally {
        $back = [RSListen.Probe]::SetMute($mute0)
        if ($back -eq 'ok') { Write-Output "  mute restored to its original state: yes" }
        else { Write-Output ("  mute restored to its original state: NO -- " + $back) }
    }
}

Write-Output ""
Write-Output "=== end of loopback capture probe ==="
