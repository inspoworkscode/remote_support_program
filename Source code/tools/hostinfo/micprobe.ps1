# Microphone measurement -- the day-one questions in docs/NEXT-SESSION-VOICE.md.
#
# Talking to the customer rests on four things nobody has measured, and each can quietly kill it:
#   1. can THIS token open the customer's microphone, and does Windows' microphone privacy refuse
#      it, or -- worse -- hand it silence without an error?
#   2. does Windows' own echo canceller (the Voice Capture DSP) run with this token?
#   3. can a loopback capture leave OUR OWN process out (process loopback, EXCLUDE mode)? Listen
#      would otherwise send the technician's voice straight back to them.
#   4. what does the microphone hear of the machine's own speakers -- the echo path itself?
#
# Run through tools/runscript it is a child of Host.exe, so its token IS the host's token: SYSTEM
# in the user's session. Run by hand it answers for the person instead. Both are worth having and
# only the first says what the product will see (HANDOVER 17: Edge as SYSTEM).
#
# It does not trust its own ears. With $PlayTone it plays a 1 kHz tone and runs a Goertzel filter
# over what came back, so "the microphone works" and "the canceller removed the tone" are numbers.
#
# Test locally before sending it anywhere:
#   powershell -NoProfile -ExecutionPolicy Bypass -File micprobe.ps1
#
# The harness rewrites the marked lines; the host runs scripts with -File and no arguments.
$PlayTone = $false   # MICPROBE_TONE_FLAG
$Seconds = 3   # MICPROBE_SECONDS
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
Write-Output ("windows       : " + [Environment]::OSVersion.Version)

# ---------------------------------------------------------------------------------------------
# Microphone privacy, as Windows stores it. "Allow" / "Deny", or absent (which means the default,
# allowed). Device-wide in HKLM; per person in their own hive, with NonPackaged being "Let desktop
# apps access your microphone" -- the one that would apply to us, if anything does.
# ---------------------------------------------------------------------------------------------
Section "microphone privacy"
$cs = 'SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone'
function Read-Consent($root, $path) {
    try {
        $k = Get-ItemProperty -Path ($root + '\' + $path) -Name Value -ErrorAction Stop
        return $k.Value
    } catch { return '(not set)' }
}
Write-Output ("device-wide (HKLM)            : " + (Read-Consent 'HKLM:' $cs))
try {
    $pol = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy' -Name LetAppsAccessMicrophone -ErrorAction Stop
    Write-Output ("policy LetAppsAccessMicrophone: " + $pol.LetAppsAccessMicrophone + "  (0 user decides, 1 force allow, 2 force deny)")
} catch { Write-Output "policy LetAppsAccessMicrophone: (not set)" }
Write-Output ("this token's own hive, apps   : " + (Read-Consent 'HKCU:' $cs))
Write-Output ("this token's own, desktop apps: " + (Read-Consent 'HKCU:' ($cs + '\NonPackaged')))
# The person at the machine: whoever owns explorer.exe in THIS session.
try {
    if (-not (Test-Path 'HKU:\')) { New-PSDrive -Name HKU -PSProvider Registry -Root HKEY_USERS -ErrorAction Stop | Out-Null }
    $ex = Get-CimInstance Win32_Process -Filter ("Name='explorer.exe' AND SessionId=" + $p.SessionId) -ErrorAction Stop | Select-Object -First 1
    if ($ex) {
        $sid = (Invoke-CimMethod -InputObject $ex -MethodName GetOwnerSid).Sid
        $own = Invoke-CimMethod -InputObject $ex -MethodName GetOwner
        Write-Output ("the person                    : " + $own.Domain + '\' + $own.User + "  " + $sid)
        Write-Output ("  their apps                  : " + (Read-Consent ('HKU:\' + $sid) $cs))
        Write-Output ("  their desktop apps          : " + (Read-Consent ('HKU:\' + $sid) ($cs + '\NonPackaged')))
    } else { Write-Output "the person                    : nobody signed in to this session (no explorer.exe)" }
} catch { Write-Output ("the person                    : could not read -- " + $_.Exception.Message) }

Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace RSMic
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PropertyKey { public Guid fmtid; public int pid; }

    // Size = 24 is NOT optional (audiolisten.ps1 says why: a 16-byte PROPVARIANT corrupts the stack).
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(8)] public int i4;
        [FieldOffset(8)] public int blobSize;
        [FieldOffset(16)] public IntPtr blobData;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFrames, out uint flags,
                                    out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    // ---- DirectX Media Object: the Voice Capture DSP's interface. Vtable order from mediaobj.h.
    [StructLayout(LayoutKind.Sequential)]
    public struct DmoMediaType
    {
        public Guid majortype;
        public Guid subtype;
        public int bFixedSizeSamples;
        public int bTemporalCompression;
        public uint lSampleSize;
        public Guid formattype;
        public IntPtr pUnk;
        public uint cbFormat;
        public IntPtr pbFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DmoOutputDataBuffer
    {
        public IntPtr pBuffer;
        public int dwStatus;
        public long rtTimestamp;
        public long rtTimelength;
    }

    [ComImport, Guid("D8AD0F58-5494-4102-97C5-EC798E59BCF4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMediaObject
    {
        [PreserveSig] int GetStreamCount(out int inputs, out int outputs);
        [PreserveSig] int GetInputStreamInfo(int index, out int flags);
        [PreserveSig] int GetOutputStreamInfo(int index, out int flags);
        [PreserveSig] int GetInputType(int index, int typeIndex, IntPtr mt);
        [PreserveSig] int GetOutputType(int index, int typeIndex, IntPtr mt);
        [PreserveSig] int SetInputType(int index, ref DmoMediaType mt, int flags);
        [PreserveSig] int SetOutputType(int index, ref DmoMediaType mt, int flags);
        [PreserveSig] int GetInputCurrentType(int index, IntPtr mt);
        [PreserveSig] int GetOutputCurrentType(int index, IntPtr mt);
        [PreserveSig] int GetInputSizeInfo(int index, out int size, out int lookahead, out int align);
        [PreserveSig] int GetOutputSizeInfo(int index, out int size, out int align);
        [PreserveSig] int GetInputMaxLatency(int index, out long latency);
        [PreserveSig] int SetInputMaxLatency(int index, long latency);
        [PreserveSig] int Flush();
        [PreserveSig] int Discontinuity(int index);
        [PreserveSig] int AllocateStreamingResources();
        [PreserveSig] int FreeStreamingResources();
        [PreserveSig] int GetInputStatus(int index, out int flags);
        [PreserveSig] int ProcessInput(int index, IntPtr buffer, int flags, long timestamp, long timelength);
        // LPArray is NOT optional. On a COM interface an array marshals as a SAFEARRAY by default;
        // the DSP then reads a SAFEARRAY as its C array and Windows PowerShell dies of an access
        // violation with no other word. .NET 8 at least said "SafeArrayMarshaler" -- that is how
        // it was found.
        [PreserveSig] int ProcessOutput(int flags, int count,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] DmoOutputDataBuffer[] buffers, out int status);
        [PreserveSig] int Lock(int bLock);
    }

    [ComImport, Guid("59EFF8B9-938C-4A26-82F2-95CB84CDC837"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMediaBuffer
    {
        [PreserveSig] int SetLength(int length);
        [PreserveSig] int GetMaxLength(out int maxLength);
        // Pointers, not "out": the DMO may pass NULL for either (mediaobj.h allows it), and an
        // out parameter would write through that NULL.
        [PreserveSig] int GetBufferAndLength(IntPtr ppBuffer, IntPtr pcbLength);
    }

    // Our side of the DMO's output: a fixed block of unmanaged memory it writes into.
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public class MediaBuffer : IMediaBuffer, IDisposable
    {
        public IntPtr Data;
        public int Max;
        public int Length;
        public MediaBuffer(int max) { Max = max; Data = Marshal.AllocHGlobal(max); }
        public int SetLength(int length) { if (length > Max) return unchecked((int)0x80070057); Length = length; return 0; }
        public int GetMaxLength(out int maxLength) { maxLength = Max; return 0; }
        public int GetBufferAndLength(IntPtr ppBuffer, IntPtr pcbLength)
        {
            if (ppBuffer != IntPtr.Zero) Marshal.WriteIntPtr(ppBuffer, Data);
            if (pcbLength != IntPtr.Zero) Marshal.WriteInt32(pcbLength, Length);
            return 0;
        }
        public void Dispose() { if (Data != IntPtr.Zero) { Marshal.FreeHGlobal(Data); Data = IntPtr.Zero; } }
    }

    // ---- Process loopback activation (Windows 10 2004+).
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    // The completion handler must be agile or activation refuses it (E_ILLEGAL_METHOD_CALL).
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEvent Done = new ManualResetEvent(false);
        public int Hr = -1;
        public object Client;
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                int hr; object o;
                int r = op.GetActivateResult(out hr, out o);
                Hr = r != 0 ? r : hr;
                Client = o;
            }
            catch (Exception ex) { Hr = Marshal.GetHRForException(ex); }
            Done.Set();
            return 0;
        }
    }

    public class Result
    {
        public int Hr;
        public string Where = "";
        public string Format = "";
        public int Packets, EmptyPolls, SilentPackets, ZeroPackets;
        public long Frames;
        public int SampleRate;
        public double Peak, Rms;
        public double Tone1k, Tone700, Tone1300;
        public double LateTone1k, LateTone700, LateTone1300;   // the last half only: a canceller needs time to converge
    }

    public static class Probe
    {
        static PropertyKey PKEY_FriendlyName = MakeKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14);

        static PropertyKey MakeKey(string guid, int pid) { PropertyKey k = new PropertyKey(); k.fmtid = new Guid(guid); k.pid = pid; return k; }
        static void Rel(object o) { if (o == null) return; try { Marshal.ReleaseComObject(o); } catch { } }
        static IMMDeviceEnumerator Enumerator() { return (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject()); }

        static string Name(IMMDevice d)
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

        static string MuteAndVolume(IMMDevice d)
        {
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            IntPtr raw;
            if (d.Activate(ref iid, 1, IntPtr.Zero, out raw) != 0 || raw == IntPtr.Zero) return "volume unreadable";
            IAudioEndpointVolume v = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(raw);
            Marshal.Release(raw);
            try
            {
                int m; float f;
                string s = v.GetMute(out m) == 0 ? (m != 0 ? "MUTED" : "not muted") : "mute unreadable";
                if (v.GetMasterVolumeLevelScalar(out f) == 0) s += ", level " + (int)Math.Round(f * 100) + "%";
                return s;
            }
            finally { Rel(v); }
        }

        // Every active microphone, and which one is the default for each role. dataFlow 1 = eCapture.
        public static List<string> Microphones()
        {
            List<string> lines = new List<string>();
            IMMDeviceEnumerator en = null;
            try
            {
                en = Enumerator();
                IMMDeviceCollection col;
                int hr = en.EnumAudioEndpoints(1, 1 /* DEVICE_STATE_ACTIVE */, out col);
                if (hr != 0 || col == null) { lines.Add("EnumAudioEndpoints(capture) hr=0x" + hr.ToString("X8")); return lines; }
                int n; col.GetCount(out n);
                lines.Add(n + " active microphone(s)");
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d;
                    if (col.Item(i, out d) != 0 || d == null) continue;
                    try { lines.Add("  - " + Name(d) + "  [" + MuteAndVolume(d) + "]"); } finally { Rel(d); }
                }
                Rel(col);
                string[] roles = { "console", "multimedia", "communications" };
                for (int r = 0; r < 3; r++)
                {
                    IMMDevice d;
                    hr = en.GetDefaultAudioEndpoint(1, r, out d);
                    if (hr != 0 || d == null) { lines.Add("default for " + roles[r] + ": none (hr=0x" + hr.ToString("X8") + ")"); continue; }
                    try { lines.Add("default for " + roles[r] + ": " + Name(d)); } finally { Rel(d); }
                }
            }
            catch (Exception ex) { lines.Add("enumeration threw: " + ex.Message); }
            finally { Rel(en); }
            return lines;
        }

        // ------------------------------------------------------------------------------------
        // Question 1: open the default COMMUNICATIONS microphone, shared mode, and read it.
        // ZeroPackets matters as much as Hr: a privacy refusal that hands back silence without an
        // error would look exactly like a quiet room. A real microphone is never all zeroes.
        // ------------------------------------------------------------------------------------
        public static Result Microphone(int ms)
        {
            Result c = new Result();
            IMMDeviceEnumerator en = null;
            IMMDevice dev = null;
            IAudioClient client = null;
            IAudioCaptureClient cap = null;
            IntPtr fmt = IntPtr.Zero;
            bool started = false;
            try
            {
                en = Enumerator();
                int hr = en.GetDefaultAudioEndpoint(1, 2, out dev);
                if (hr != 0 || dev == null) { c.Hr = hr == 0 ? -1 : hr; c.Where = "no default microphone"; return c; }
                Guid iid = typeof(IAudioClient).GUID;
                IntPtr raw;
                hr = dev.Activate(ref iid, 1, IntPtr.Zero, out raw);
                if (hr != 0 || raw == IntPtr.Zero) { c.Hr = hr; c.Where = "IMMDevice::Activate(IAudioClient)"; return c; }
                client = (IAudioClient)Marshal.GetObjectForIUnknown(raw);
                Marshal.Release(raw);
                hr = client.GetMixFormat(out fmt);
                if (hr != 0 || fmt == IntPtr.Zero) { c.Hr = hr; c.Where = "IAudioClient::GetMixFormat"; return c; }
                bool isFloat; int channels, bits, blockAlign;
                c.Format = Describe(fmt, out isFloat, out channels, out bits, out blockAlign, out c.SampleRate);
                if (!(isFloat && bits == 32) && bits != 16) { c.Hr = -3; c.Where = "mix format " + c.Format + " -- this probe cannot decode it"; return c; }
                hr = client.Initialize(0, 0, 10000000L, 0L, fmt, IntPtr.Zero);
                if (hr != 0) { c.Hr = hr; c.Where = "IAudioClient::Initialize on the MICROPHONE  <-- question 1"; return c; }
                Guid capIid = typeof(IAudioCaptureClient).GUID;
                IntPtr capRaw;
                hr = client.GetService(ref capIid, out capRaw);
                if (hr != 0 || capRaw == IntPtr.Zero) { c.Hr = hr; c.Where = "GetService(IAudioCaptureClient)"; return c; }
                cap = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capRaw);
                Marshal.Release(capRaw);
                hr = client.Start();
                if (hr != 0) { c.Hr = hr; c.Where = "IAudioClient::Start"; return c; }
                started = true;
                Drain(cap, ms, isFloat, bits, channels, c);
                return c;
            }
            catch (Exception ex) { c.Hr = -2; c.Where = ex.ToString(); return c; }
            finally
            {
                if (started) { try { client.Stop(); } catch { } }
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
                Rel(cap); Rel(client); Rel(dev); Rel(en);
            }
        }

        static string Describe(IntPtr fmt, out bool isFloat, out int channels, out int bits, out int blockAlign, out int rate)
        {
            int tag = Marshal.ReadInt16(fmt, 0) & 0xFFFF;
            channels = Marshal.ReadInt16(fmt, 2) & 0xFFFF;
            rate = Marshal.ReadInt32(fmt, 4);
            blockAlign = Marshal.ReadInt16(fmt, 12) & 0xFFFF;
            bits = Marshal.ReadInt16(fmt, 14) & 0xFFFF;
            isFloat = tag == 3;
            string name = tag == 1 ? "PCM" : tag == 3 ? "IEEE float" : "tag " + tag;
            if (tag == 0xFFFE)
            {
                // SubFormat is at offset 24, not 26 (HANDOVER 18: the misaligned read).
                byte[] g = new byte[16];
                Marshal.Copy(new IntPtr(fmt.ToInt64() + 24), g, 0, 16);
                Guid sub = new Guid(g);
                if (sub == new Guid("00000003-0000-0010-8000-00aa00389b71")) { isFloat = true; name = "EXTENSIBLE/float"; }
                else if (sub == new Guid("00000001-0000-0010-8000-00aa00389b71")) name = "EXTENSIBLE/PCM";
                else name = "EXTENSIBLE/" + sub;
            }
            return name + " " + rate + " Hz " + channels + " ch " + bits + "-bit";
        }

        static void Drain(IAudioCaptureClient cap, int ms, bool isFloat, int bits, int channels, Result c)
        {
            List<double> mono = new List<double>();
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                uint next;
                if (cap.GetNextPacketSize(out next) != 0) break;
                if (next == 0) { c.EmptyPolls++; Thread.Sleep(10); continue; }
                IntPtr data; uint frames, flags; long dp, qpc;
                if (cap.GetBuffer(out data, out frames, out flags, out dp, out qpc) != 0) break;
                c.Packets++;
                c.Frames += frames;
                bool silent = (flags & 0x2) != 0;
                if (silent) c.SilentPackets++;
                else if (data != IntPtr.Zero && frames > 0)
                {
                    int n = (int)frames * channels;
                    double[] s = new double[n];
                    if (isFloat && bits == 32) { float[] f = new float[n]; Marshal.Copy(data, f, 0, n); for (int i = 0; i < n; i++) s[i] = f[i]; }
                    else { short[] sh = new short[n]; Marshal.Copy(data, sh, 0, n); for (int i = 0; i < n; i++) s[i] = sh[i] / 32768.0; }
                    bool allZero = true;
                    for (int fr = 0; fr < (int)frames; fr++)
                    {
                        double sum = 0;
                        for (int ch = 0; ch < channels; ch++) sum += s[fr * channels + ch];
                        double m = sum / channels;
                        if (m != 0) allZero = false;
                        Accumulate(c, m, mono);
                    }
                    if (allZero) c.ZeroPackets++;
                }
                cap.ReleaseBuffer(frames);
            }
            Finish(c, mono);
        }

        static void Accumulate(Result c, double m, List<double> mono)
        {
            double a = Math.Abs(m);
            if (a > c.Peak) c.Peak = a;
            c.Rms += m * m;
            if (mono.Count < 48000 * 20) mono.Add(m);
        }

        static void Finish(Result c, List<double> mono)
        {
            if (mono.Count > 0) c.Rms = Math.Sqrt(c.Rms / mono.Count);
            c.Tone1k = Goertzel(mono, 0, mono.Count, c.SampleRate, 1000);
            c.Tone700 = Goertzel(mono, 0, mono.Count, c.SampleRate, 700);
            c.Tone1300 = Goertzel(mono, 0, mono.Count, c.SampleRate, 1300);
            int half = mono.Count / 2;
            c.LateTone1k = Goertzel(mono, half, mono.Count - half, c.SampleRate, 1000);
            c.LateTone700 = Goertzel(mono, half, mono.Count - half, c.SampleRate, 700);
            c.LateTone1300 = Goertzel(mono, half, mono.Count - half, c.SampleRate, 1300);
        }

        // ------------------------------------------------------------------------------------
        // Question 2: Windows' Voice Capture DSP in its system mode -- it opens the default
        // communications microphone AND watches the default speakers itself, and hands back the
        // microphone with the speakers' sound taken out. 16 kHz mono 16-bit, asked for here.
        // ------------------------------------------------------------------------------------
        public static Result EchoCanceller(int ms)
        {
            Result c = new Result();
            c.SampleRate = 16000;
            c.Format = "PCM 16000 Hz 1 ch 16-bit (asked for)";
            object dsp = null;
            IntPtr wfx = IntPtr.Zero;
            MediaBuffer buf = null;
            IntPtr bufPtr = IntPtr.Zero;
            try
            {
                Type t = Type.GetTypeFromCLSID(new Guid("745057C7-F353-4F2D-A7EE-58434477730E"));
                try { dsp = Activator.CreateInstance(t); }
                catch (Exception ex) { c.Hr = Marshal.GetHRForException(ex); c.Where = "CoCreateInstance(CLSID_CWMAudioAEC)"; return c; }

                IPropertyStore props = (IPropertyStore)dsp;
                // MFPKEY_WMAAECMA_SYSTEM_MODE = SINGLE_CHANNEL_AEC (0).
                PropertyKey mode = MakeKey("6f52c567-0360-4bd2-9617-ccbf1421c939", 2);
                PropVariant v = new PropVariant(); v.vt = 3; v.i4 = 0;
                int hr = props.SetValue(ref mode, ref v);
                if (hr != 0) { c.Hr = hr; c.Where = "SetValue(MFPKEY_WMAAECMA_SYSTEM_MODE)"; return c; }

                IMediaObject mo = (IMediaObject)dsp;
                wfx = Marshal.AllocCoTaskMem(18);
                Marshal.WriteInt16(wfx, 0, 1);          // WAVE_FORMAT_PCM
                Marshal.WriteInt16(wfx, 2, 1);          // mono
                Marshal.WriteInt32(wfx, 4, 16000);
                Marshal.WriteInt32(wfx, 8, 32000);      // bytes per second
                Marshal.WriteInt16(wfx, 12, 2);         // block align
                Marshal.WriteInt16(wfx, 14, 16);
                Marshal.WriteInt16(wfx, 16, 0);
                DmoMediaType mt = new DmoMediaType();
                mt.majortype = new Guid("73647561-0000-0010-8000-00AA00389B71");     // MEDIATYPE_Audio
                mt.subtype = new Guid("00000001-0000-0010-8000-00AA00389B71");       // MEDIASUBTYPE_PCM
                mt.bFixedSizeSamples = 1;
                mt.lSampleSize = 2;
                mt.formattype = new Guid("05589f81-c356-11ce-bf01-00aa0055595a");    // FORMAT_WaveFormatEx
                mt.cbFormat = 18;
                mt.pbFormat = wfx;
                hr = mo.SetOutputType(0, ref mt, 0);
                if (hr != 0) { c.Hr = hr; c.Where = "IMediaObject::SetOutputType"; return c; }
                hr = mo.AllocateStreamingResources();
                if (hr != 0) { c.Hr = hr; c.Where = "IMediaObject::AllocateStreamingResources  <-- it opens the devices here"; return c; }

                buf = new MediaBuffer(32000);   // a second, far more than one call returns
                bufPtr = Marshal.GetComInterfaceForObject(buf, typeof(IMediaBuffer));
                List<double> mono = new List<double>();
                DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
                while (DateTime.UtcNow < end)
                {
                    DmoOutputDataBuffer[] outs = new DmoOutputDataBuffer[1];
                    outs[0].pBuffer = bufPtr;
                    int status;
                    buf.Length = 0;
                    hr = mo.ProcessOutput(0, 1, outs, out status);
                    if (hr == 1 /* S_FALSE: nothing yet */ || (hr == 0 && buf.Length == 0)) { c.EmptyPolls++; Thread.Sleep(10); continue; }
                    if (hr != 0) { c.Hr = hr; c.Where = "IMediaObject::ProcessOutput"; break; }
                    c.Packets++;
                    int n = buf.Length / 2;
                    short[] sh = new short[n];
                    Marshal.Copy(buf.Data, sh, 0, n);
                    bool allZero = true;
                    for (int i = 0; i < n; i++) { double m = sh[i] / 32768.0; if (m != 0) allZero = false; Accumulate(c, m, mono); }
                    if (allZero) c.ZeroPackets++;
                    c.Frames += n;
                    // Incomplete means more is waiting: take it without sleeping.
                    if ((outs[0].dwStatus & 0x01000000) == 0) Thread.Sleep(10);
                }
                mo.FreeStreamingResources();
                Finish(c, mono);
                return c;
            }
            catch (Exception ex) { c.Hr = -2; c.Where = ex.ToString(); return c; }
            finally
            {
                if (bufPtr != IntPtr.Zero) Marshal.Release(bufPtr);
                if (buf != null) buf.Dispose();
                if (wfx != IntPtr.Zero) Marshal.FreeCoTaskMem(wfx);
                Rel(dsp);
            }
        }

        // ------------------------------------------------------------------------------------
        // Question 3: loopback of every process EXCEPT (mode 1) -- or ONLY (mode 0) -- a process
        // tree. The tone is played by THIS process, so INCLUDE must hear it and EXCLUDE must not.
        // ------------------------------------------------------------------------------------
        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
        static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid iid,
            IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

        // On its own MTA thread. The activation completes on an MTA thread and IAudioClient has no
        // proxy to cross apartments, so used from Windows PowerShell's STA thread it is
        // E_NOINTERFACE (measured). The host's own capture thread will be MTA too.
        public static Result ProcessLoopback(int pid, int loopbackMode, int ms)
        {
            Result r = null;
            Thread t = new Thread(delegate() { r = ProcessLoopbackMta(pid, loopbackMode, ms); });
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
            return r;
        }

        static Result ProcessLoopbackMta(int pid, int loopbackMode, int ms)
        {
            Result c = new Result();
            IntPtr pvMem = IntPtr.Zero, blob = IntPtr.Zero, wfx = IntPtr.Zero;
            IAudioClient client = null;
            IAudioCaptureClient cap = null;
            bool started = false;
            try
            {
                // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = PROCESS_LOOPBACK (1), { TargetProcessId, ProcessLoopbackMode } }
                blob = Marshal.AllocHGlobal(12);
                Marshal.WriteInt32(blob, 0, 1);
                Marshal.WriteInt32(blob, 4, pid);
                Marshal.WriteInt32(blob, 8, loopbackMode);
                PropVariant pv = new PropVariant();
                pv.vt = 65;   // VT_BLOB
                pv.blobSize = 12;
                pv.blobData = blob;
                pvMem = Marshal.AllocHGlobal(24);
                Marshal.StructureToPtr(pv, pvMem, false);

                Completion done = new Completion();
                Guid iid = typeof(IAudioClient).GUID;
                IActivateAudioInterfaceAsyncOperation op;
                int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pvMem, done, out op);
                if (hr != 0) { c.Hr = hr; c.Where = "ActivateAudioInterfaceAsync(VAD\\Process_Loopback)"; return c; }
                if (!done.Done.WaitOne(5000)) { c.Hr = -4; c.Where = "activation never completed"; return c; }
                if (done.Hr != 0 || done.Client == null) { c.Hr = done.Hr; c.Where = "GetActivateResult  <-- question 3"; return c; }
                client = (IAudioClient)done.Client;

                // Process loopback has no mix format of its own (GetMixFormat is E_NOTIMPL): we say.
                c.SampleRate = 48000;
                wfx = Marshal.AllocCoTaskMem(18);
                Marshal.WriteInt16(wfx, 0, 1); Marshal.WriteInt16(wfx, 2, 2);
                Marshal.WriteInt32(wfx, 4, 48000); Marshal.WriteInt32(wfx, 8, 48000 * 4);
                Marshal.WriteInt16(wfx, 12, 4); Marshal.WriteInt16(wfx, 14, 16); Marshal.WriteInt16(wfx, 16, 0);
                c.Format = "PCM 48000 Hz 2 ch 16-bit (asked for)";
                // LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM -- the combination Microsoft's sample uses.
                hr = client.Initialize(0, 0x00020000 | 0x00040000 | unchecked((int)0x80000000), 2000000L, 0L, wfx, IntPtr.Zero);
                if (hr != 0) { c.Hr = hr; c.Where = "IAudioClient::Initialize (process loopback)"; return c; }
                EventWaitHandle ev = new EventWaitHandle(false, EventResetMode.AutoReset);
                client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                Guid capIid = typeof(IAudioCaptureClient).GUID;
                IntPtr capRaw;
                hr = client.GetService(ref capIid, out capRaw);
                if (hr != 0 || capRaw == IntPtr.Zero) { c.Hr = hr; c.Where = "GetService(IAudioCaptureClient)"; return c; }
                cap = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capRaw);
                Marshal.Release(capRaw);
                hr = client.Start();
                if (hr != 0) { c.Hr = hr; c.Where = "IAudioClient::Start"; return c; }
                started = true;
                Drain(cap, ms, false, 16, 2, c);
                GC.KeepAlive(ev);
                return c;
            }
            catch (Exception ex) { c.Hr = -2; c.Where = ex.ToString(); return c; }
            finally
            {
                if (started) { try { client.Stop(); } catch { } }
                Rel(cap); Rel(client);
                if (pvMem != IntPtr.Zero) Marshal.FreeHGlobal(pvMem);
                if (blob != IntPtr.Zero) Marshal.FreeHGlobal(blob);
                if (wfx != IntPtr.Zero) Marshal.FreeCoTaskMem(wfx);
            }
        }

        public static double Goertzel(List<double> x, int start, int n, int rate, double freq)
        {
            if (n < 256 || rate <= 0) return 0;
            double k = Math.Round(n * freq / rate);
            double w = 2.0 * Math.PI * k / n;
            double coeff = 2.0 * Math.Cos(w);
            double s1 = 0, s2 = 0;
            for (int i = 0; i < n; i++) { double s0 = x[start + i] + coeff * s1 - s2; s2 = s1; s1 = s0; }
            double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            if (power < 0) power = 0;
            return Math.Sqrt(power) / (n / 2.0);
        }
    }
}
'@

function New-ToneStream([int]$rate, [double]$secs, [int]$freq, [switch]$Pulsed) {
    # One period tiled, not a per-sample PowerShell loop: audiotone.ps1 took SECONDS to build its
    # wave that way, and the silence before it started read as missing audio (HANDOVER 18).
    #
    # -Pulsed: 250 ms on, 250 ms off. A STEADY tone is what a noise suppressor learns and removes
    # within a second or so -- this laptop's microphone heard the tone at 0.0186 over three
    # seconds and at 0.0001 over the last half of them. Against a steady tone, "the echo canceller
    # removed it" cannot be told from "the noise suppressor did". Speech is not steady; pulses
    # are closer to it.
    $samples = [int]($rate * $secs)
    $period = [int]($rate / $freq)
    $one = New-Object byte[] ($period * 2)
    for ($i = 0; $i -lt $period; $i++) {
        $v = [int16](12000 * [Math]::Sin(2 * [Math]::PI * $i / $period))
        $b = [BitConverter]::GetBytes($v); $one[2 * $i] = $b[0]; $one[2 * $i + 1] = $b[1]
    }
    if ($Pulsed) {
        $burst = [int]($rate / 4 / $period)            # periods in 250 ms
        $on = New-Object byte[] ($burst * $period * 2)
        for ($j = 0; $j -lt $burst; $j++) { [Array]::Copy($one, 0, $on, $j * $period * 2, $period * 2) }
        $one = New-Object byte[] ($on.Length * 2)      # 250 ms of tone, then 250 ms of nothing
        [Array]::Copy($on, 0, $one, 0, $on.Length)
        $period = $burst * $period * 2
    }
    $periods = [int]($samples / $period)
    $dataBytes = $periods * $period * 2
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([char[]]'RIFF'); $bw.Write([int](36 + $dataBytes))
    $bw.Write([char[]]'WAVE'); $bw.Write([char[]]'fmt ')
    $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
    $bw.Write([int]$rate); $bw.Write([int]($rate * 2))
    $bw.Write([int16]2); $bw.Write([int16]16)
    $bw.Write([char[]]'data'); $bw.Write([int]$dataBytes)
    for ($j = 0; $j -lt $periods; $j++) { $bw.Write($one) }
    $bw.Flush()
    [void]$ms.Seek(0, 'Begin')
    return $ms
}

# A render stream that makes no sound: all the echo canceller needs to start delivering.
function New-SilenceStream([int]$rate, [double]$secs) {
    $dataBytes = [int]($rate * $secs) * 2
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([char[]]'RIFF'); $bw.Write([int](36 + $dataBytes))
    $bw.Write([char[]]'WAVE'); $bw.Write([char[]]'fmt ')
    $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
    $bw.Write([int]$rate); $bw.Write([int]($rate * 2))
    $bw.Write([int16]2); $bw.Write([int16]16)
    $bw.Write([char[]]'data'); $bw.Write([int]$dataBytes)
    $bw.Write((New-Object byte[] $dataBytes))
    $bw.Flush()
    [void]$ms.Seek(0, 'Begin')
    return $ms
}

function Show-Result($label, $c, [bool]$toneExpected) {
    Write-Output ("  " + $label)
    if ($c.Hr -ne 0) {
        Write-Output ("    FAILED at " + $c.Where)
        Write-Output ("    hr=0x" + ('{0:X8}' -f $c.Hr))
        if ($c.Hr -eq -2147024891) { Write-Output "    (0x80070005 is E_ACCESSDENIED -- refused, which is what a privacy block looks like)" }
        return
    }
    Write-Output ("    format      : " + $c.Format)
    Write-Output ("    packets     : " + $c.Packets + "   empty polls: " + $c.EmptyPolls + "   silent-flagged: " + $c.SilentPackets + "   ALL-ZERO: " + $c.ZeroPackets)
    if ($c.SampleRate -gt 0) { Write-Output ("    audio       : " + [Math]::Round($c.Frames / $c.SampleRate, 2) + " s") }
    Write-Output ("    peak        : " + [Math]::Round($c.Peak, 5) + "   rms: " + [Math]::Round($c.Rms, 6))
    if ($toneExpected) {
        Write-Output ("    goertzel    : 1000 Hz=" + [Math]::Round($c.Tone1k, 6) + "   700=" + [Math]::Round($c.Tone700, 6) + "   1300=" + [Math]::Round($c.Tone1300, 6))
        Write-Output ("    second half : 1000 Hz=" + [Math]::Round($c.LateTone1k, 6) + "   700=" + [Math]::Round($c.LateTone700, 6) + "   1300=" + [Math]::Round($c.LateTone1300, 6))
    }
    if ($c.Packets -gt 0 -and ($c.ZeroPackets + $c.SilentPackets) -eq $c.Packets) {
        Write-Output "    VERDICT     : EVERY packet was silence. A real microphone is never exactly zero --"
        Write-Output "                  this is a refusal dressed as a quiet room (or a hardware mute)."
    } elseif ($c.Frames -eq 0) {
        Write-Output "    VERDICT     : opened, but delivered nothing at all."
    } else {
        Write-Output "    VERDICT     : real audio arrived."
    }
}

function Tone-Verdict($c, $what) {
    if ($c.Hr -ne 0) { return }
    $n = [Math]::Max($c.LateTone700, $c.LateTone1300)
    if ($c.LateTone1k -gt 0.0005 -and $c.LateTone1k -gt ($n * 8)) { Write-Output ("    TONE        : " + $what + " HEARD the 1 kHz tone.") }
    else { Write-Output ("    TONE        : " + $what + " did NOT hear the 1 kHz tone.") }
}

Section "microphones"
[RSMic.Probe]::Microphones() | ForEach-Object { Write-Output $_ }

Section "1. the microphone, as this token"
$mic = [RSMic.Probe]::Microphone($Seconds * 1000)
Show-Result ("" + $Seconds + " s of the default communications microphone") $mic $false

Section "2. Windows' echo canceller, as this token"
# Measured on this laptop 2026-09-30: with nothing playing the DSP returns S_FALSE for ever and
# delivers NOTHING -- in system mode it waits for a render stream to cancel against. So it is run
# here with a tone playing when allowed, and a silent render stream otherwise: the product will
# keep its own voice playback open for the whole call, which is that stream.
if ($PlayTone) { $msA = New-ToneStream 48000 ($Seconds + 1) 1000 } else { $msA = New-SilenceStream 48000 ($Seconds + 1) }
$plA = New-Object System.Media.SoundPlayer($msA)
$plA.Play(); Start-Sleep -Milliseconds 300
$aec = [RSMic.Probe]::EchoCanceller($Seconds * 1000)
$plA.Stop()
if ($PlayTone) { $what = "while a tone plays" } else { $what = "while SILENCE plays (a render stream, no sound)" }
Show-Result ("" + $Seconds + " s through the Voice Capture DSP, " + $what) $aec $false
if ($aec.Hr -eq 0 -and $aec.Frames -eq 0) {
    Write-Output "    NOTE        : nothing at all even with a render stream open -- see the NOTE in the script."
}

Section "3. process loopback, as this token"
if (-not $PlayTone) {
    Write-Output "  needs a sound this process plays to tell INCLUDE from EXCLUDE; activation only, no tone:"
    $inc = [RSMic.Probe]::ProcessLoopback($PID, 0, 500)
    Show-Result "INCLUDE this process, 0.5 s" $inc $false
    Write-Output "  (set MICPROBE_TONE_FLAG -- audioprobe/runscript --tone -- for the real answer)"
} else {
    $secs = $Seconds + 1
    $ms1 = New-ToneStream 48000 $secs 1000
    $pl1 = New-Object System.Media.SoundPlayer($ms1)
    $pl1.Play(); Start-Sleep -Milliseconds 300
    $inc = [RSMic.Probe]::ProcessLoopback($PID, 0, $Seconds * 1000)
    $pl1.Stop()
    Show-Result "INCLUDE this process (it is playing the tone)" $inc $true
    Tone-Verdict $inc "INCLUDE"
    $ms2 = New-ToneStream 48000 $secs 1000
    $pl2 = New-Object System.Media.SoundPlayer($ms2)
    $pl2.Play(); Start-Sleep -Milliseconds 300
    $exc = [RSMic.Probe]::ProcessLoopback($PID, 1, $Seconds * 1000)
    $pl2.Stop()
    Show-Result "EXCLUDE this process (it is playing the tone)" $exc $true
    Tone-Verdict $exc "EXCLUDE"
    if ($inc.Hr -eq 0 -and $exc.Hr -eq 0) {
        $n1 = [Math]::Max($inc.LateTone700, $inc.LateTone1300); $n2 = [Math]::Max($exc.LateTone700, $exc.LateTone1300)
        if ($inc.LateTone1k -gt ($n1 * 8) -and -not ($exc.LateTone1k -gt ($n2 * 8) -and $exc.LateTone1k -gt 0.0005)) {
            Write-Output "  -> PROCESS LOOPBACK WORKS: our own sound can be left out of Listen."
        } else { Write-Output "  -> inconclusive: look at the two blocks above." }
    }
}

Section "4. what the microphone hears of the speakers"
if (-not $PlayTone) {
    Write-Output "  skipped: it plays a 1 kHz tone on this machine's speakers (MICPROBE_TONE_FLAG)."
} else {
    # Pulsed, for the reason at New-ToneStream: a steady tone is removed by noise suppression
    # (the microphone driver's, and the DSP's own) and would flatter the canceller.
    $secs = $Seconds + 1
    $ms3 = New-ToneStream 48000 $secs 1000 -Pulsed
    $pl3 = New-Object System.Media.SoundPlayer($ms3)
    $pl3.Play(); Start-Sleep -Milliseconds 300
    $raw = [RSMic.Probe]::Microphone($Seconds * 1000)
    $pl3.Stop()
    Show-Result "the raw microphone, while PULSES of tone play" $raw $true
    Tone-Verdict $raw "the raw microphone"
    $ms4 = New-ToneStream 48000 ($secs + 2) 1000 -Pulsed
    $pl4 = New-Object System.Media.SoundPlayer($ms4)
    $pl4.Play(); Start-Sleep -Milliseconds 300
    $can = [RSMic.Probe]::EchoCanceller(($Seconds + 2) * 1000)
    $pl4.Stop()
    Show-Result "through the echo canceller, while PULSES of tone play" $can $true
    Tone-Verdict $can "the echo canceller's output"
    $rawN = [Math]::Max($raw.LateTone700, $raw.LateTone1300)
    $rawHeard = $raw.Hr -eq 0 -and $raw.LateTone1k -gt 0.0005 -and $raw.LateTone1k -gt ($rawN * 8)
    if ($rawHeard -and $can.Hr -eq 0) {
        # The last half of each: the canceller has had a second or two to learn the room.
        $db = 20 * [Math]::Log10([Math]::Max($can.LateTone1k, 1e-9) / $raw.LateTone1k)
        Write-Output ("  -> the canceller changed the tone by " + [Math]::Round($db, 1) + " dB (negative is removed; -20 or better is working)")
    } elseif ($raw.Hr -eq 0) {
        # Seen on this laptop (Realtek, 2026-09-30): a steady tone heard at first and then lost,
        # pulses never heard at all. Its driver cancels its own speakers ahead of us. A desktop with
        # a webcam microphone and separate speakers will not -- which is why the canceller stays.
        Write-Output "  -> the raw microphone did not hear the speakers, so there is nothing to measure the"
        Write-Output "     canceller against. Either the speakers are muted, quiet or far away, or the driver"
        Write-Output "     already removes its own speakers from the microphone (this laptop's Realtek does)."
    }
}

Write-Output ""
Write-Output "=== end of microphone probe ==="
