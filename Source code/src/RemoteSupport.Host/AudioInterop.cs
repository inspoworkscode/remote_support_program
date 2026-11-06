using System.Runtime.InteropServices;

namespace RemoteSupport.Host;

/// <summary>
/// The Windows Core Audio interfaces, in one place, shared by <see cref="AudioCapture"/> (which
/// listens to the machine) and <see cref="AudioDiagnostics"/> (which explains why it is silent).
///
/// THE ONE RULE THAT MATTERS HERE: the declared order of the methods IS the interface. COM
/// dispatches by vtable slot, not by name, so a single method out of sequence — or a missing one
/// — sends every later call to the wrong function with no error of any kind. Where these came
/// from: Mmdeviceapi.h, Audioclient.h, Audiopolicy.h and Endpointvolume.h. Do not reorder them,
/// and do not "tidy up" an unused method by deleting it.
/// </summary>
// Public, not internal, since 3.3: the process-loopback completion handler below is handed to COM,
// and a COM callable wrapper refuses a type COM cannot see — nested public in an internal class is not seen.
public static class AudioInterop
{
    public const int ERender = 0, ECapture = 1;
    public const int EConsole = 0, EMultimedia = 1, ECommunications = 2;
    public const int ClsCtxInproc = 1;
    public const int StgmRead = 0;
    /// <summary>Every device state: active, disabled, not present and unplugged. A device the
    /// customer says they do not have is usually present and DISABLED, and that is the answer.</summary>
    public const int StateMaskAll = 15;
    public const int StreamFlagsLoopback = 0x00020000;
    public const uint BufferFlagsSilent = 0x2;
    public const int AudclntDeviceInvalidated = unchecked((int)0x88890004);
    public const int ErrorNotFound = unchecked((int)0x80070490);

    public static readonly Guid SubFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubFormatIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static readonly PropertyKey PkeyFriendlyName =
        new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
    /// <summary>The adaptor behind the endpoint — what tells one "Speakers" from another.</summary>
    public static readonly PropertyKey PkeyInterfaceFriendlyName =
        new() { fmtid = new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), pid = 6 };

    /// <summary>Releasing a COM wrapper must never be the thing that fails. Marshal.ReleaseComObject
    /// threw from inside a finally while this was being written and swallowed the real answer for
    /// every endpoint; tidying up is not worth losing a measurement over.</summary>
    public static void Rel(object? o)
    {
        if (o == null) return;
        try { Marshal.ReleaseComObject(o); } catch { }
    }

    public static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    public static string StateName(int s) => s switch
    {
        1 => "ACTIVE",
        2 => "DISABLED",
        4 => "NOTPRESENT",
        8 => "UNPLUGGED",
        _ => "state " + s
    };

    public static string SessionStateName(int s) => s switch
    {
        0 => "idle",
        1 => "playing",
        2 => "expired",
        _ => "state " + s
    };

    /// <summary>A string property, or an empty string. Never throws: one unreadable device must
    /// not cost the rest of the list.</summary>
    public static string GetString(IMMDevice dev, PropertyKey key)
    {
        IPropertyStore? store = null;
        try
        {
            if (dev.OpenPropertyStore(StgmRead, out store) != 0 || store == null) return "";
            var k = key;
            if (store.GetValue(ref k, out var pv) != 0) return "";
            try
            {
                if (pv.vt != 31 /* VT_LPWSTR */ || pv.p == IntPtr.Zero) return "";
                return Marshal.PtrToStringUni(pv.p) ?? "";
            }
            finally { try { PropVariantClear(ref pv); } catch { } }
        }
        catch { return ""; }
        finally { Rel(store); }
    }

    /// <summary>Volume and mute for an endpoint. percent is -1 when it could not be read.</summary>
    public static void ReadVolume(IMMDevice dev, out bool muted, out int percent, out string error)
    {
        muted = false; percent = -1; error = "";
        IAudioEndpointVolume? vol = null;
        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            int hr = dev.Activate(ref iid, ClsCtxInproc, IntPtr.Zero, out var raw);
            if (hr != 0 || raw == IntPtr.Zero) { error = $"volume control unavailable (hr=0x{hr:X8})"; return; }
            vol = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(raw);
            Marshal.Release(raw);
            if (vol.GetMute(out int m) == 0) muted = m != 0;
            if (vol.GetMasterVolumeLevelScalar(out float f) == 0) percent = (int)Math.Round(f * 100);
        }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        finally { Rel(vol); }
    }

    // ------------------------------------------------------------------------------ interfaces

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore? store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);
        [PreserveSig] int GetState(out int state);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PropertyKey { public Guid fmtid; public int pid; }

    /// <summary>
    /// PROPVARIANT. <b>Size = 24 is not optional and is not padding.</b>
    ///
    /// On x64 a PROPVARIANT is 24 bytes: the tag and three reserved words, then a union whose
    /// largest members (CLIPDATA, the counted arrays) are twelve bytes. Declared without an
    /// explicit size this struct comes out at 16, and <c>IPropertyStore::GetValue</c> then writes
    /// EIGHT BYTES PAST THE END of the caller's stack slot.
    ///
    /// That is what the bug looked like from the outside: the device's friendly name read
    /// perfectly, the adaptor name came back empty, and ADDING A DIAGNOSTIC PRINT MADE IT WORK —
    /// because it changed what was sitting next to the struct on the stack. Two hours of "this is
    /// semantically identical, why does it behave differently" is the tax on a wrong struct size,
    /// and a heisenbug that moves when you look at it should point here every time.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr p;
    }

    /// <summary>Frees whatever a PROPVARIANT owns. The string a VT_LPWSTR points at is COM's, not
    /// ours, and every value read without this is a small permanent leak in a long session.</summary>
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

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

    // ------------------------------------------------------------------ process loopback (3.3)
    //
    // Loopback of every process on the machine EXCEPT one process tree (Windows 10 2004 and later).
    // Listen uses it while a voice call is on, so the technician's own voice — played by THIS
    // process — is not captured and sent straight back to them. micprobe measured it on
    // 2026-09-30: with our tone playing, INCLUDE heard it at 0.36 and EXCLUDE heard exactly nothing.
    //
    // Two things that were found by failing: activation completes on an MTA thread and IAudioClient
    // has no proxy between apartments, so the client must be USED on an MTA thread too
    // (E_NOINTERFACE otherwise); and process loopback has no mix format of its own — GetMixFormat
    // is E_NOTIMPL — so the caller says what it wants, with AUTOCONVERTPCM.

    public const int StreamFlagsEventCallback = 0x00040000;
    public const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);

    /// <summary>
    /// Opens an IAudioClient on the loopback of every process except <paramref name="pid"/>'s tree.
    /// Must be called on an MTA thread, and the client used there. Null with an HRESULT on failure,
    /// which on Windows before 2004 is the expected answer and not a fault.
    /// </summary>
    public static IAudioClient? ActivateProcessLoopbackExcluding(int pid, out int hr)
    {
        IntPtr blob = IntPtr.Zero, pv = IntPtr.Zero;
        try
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK (1), { TargetProcessId, EXCLUDE (1) } }
            blob = Marshal.AllocHGlobal(12);
            Marshal.WriteInt32(blob, 0, 1);
            Marshal.WriteInt32(blob, 4, pid);
            Marshal.WriteInt32(blob, 8, 1);
            // A PROPVARIANT of VT_BLOB: the size at 8, the pointer at 16. 24 bytes on x64.
            pv = Marshal.AllocHGlobal(24);
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 65);   // VT_BLOB
            Marshal.WriteInt32(pv, 8, 12);
            Marshal.WriteIntPtr(pv, 16, blob);

            var done = new ActivationCompletion();
            var iid = typeof(IAudioClient).GUID;
            hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, done, out _);
            if (hr != 0) return null;
            if (!done.Done.Wait(5000)) { hr = unchecked((int)0x800705B4); return null; }   // ERROR_TIMEOUT
            hr = done.Hr;
            return hr == 0 ? done.Client as IAudioClient : null;
        }
        catch (Exception ex) { hr = ex.HResult; return null; }
        finally
        {
            if (pv != IntPtr.Zero) Marshal.FreeHGlobal(pv);
            if (blob != IntPtr.Zero) Marshal.FreeHGlobal(blob);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid iid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activated);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    /// <summary>Activation refuses a completion handler that is not agile.</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }

    /// <summary>PUBLIC only because a COM callable wrapper refuses a type COM cannot see.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ActivationCompletion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public int Hr = -1;
        public object? Client;
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                int r = op.GetActivateResult(out int hr, out var o);
                Hr = r != 0 ? r : hr;
                Client = o;
            }
            catch (Exception ex) { Hr = ex.HResult; }
            Done.Set();
            return 0;
        }
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? e);
        [PreserveSig] int RegisterSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterSessionNotification(IntPtr n);
        [PreserveSig] int RegisterDuckNotification(IntPtr s, IntPtr n);
        [PreserveSig] int UnregisterDuckNotification(IntPtr n);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2? session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string? name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string? path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string? id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string? id);
        [PreserveSig] int GetProcessId(out int pid);
        /// <summary>Returns S_OK (0) when this is the Windows system sounds session.</summary>
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(int mute, ref Guid ctx);
        [PreserveSig] int GetMute(out int mute);
    }
}
