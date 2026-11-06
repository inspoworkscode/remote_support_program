using System.Runtime.InteropServices;

namespace RemoteSupport.Media;

/// <summary>
/// Raw Media Foundation interop. Nothing is bought in, so this is the same shape as
/// <c>Dxgi.cs</c> in the host: COM objects are IntPtrs and their methods are called through the
/// vtable by slot.
///
/// READ THIS BEFORE EDITING. A slot number is an index into a function-pointer table. Get one
/// wrong and the process does not throw -- it calls a different method with the wrong arguments
/// and dies, or worse, does not die. Every interface below is written out in full, in header
/// order, counting from the start of the whole inheritance chain including IUnknown's three, so
/// the count can be re-derived rather than trusted.
///
/// Two of these chains are long enough to be worth naming: IMFMediaType and IMFSample both
/// inherit IMFAttributes, which is thirty methods on its own. Everything they add therefore
/// starts at slot 33, and the commonest possible mistake here is to count an IMFSample method
/// from IUnknown.
///
/// `tools/codeccheck` exists partly to catch this class of mistake the way `codecbench --compare`
/// does for DXGI: it encodes a known picture, decodes it again and compares the pixels, so a
/// vtable that is subtly wrong shows up as a wrong picture rather than as a crash three weeks
/// later on a customer's machine.
/// </summary>
internal static unsafe class Mf
{
    // ---- HRESULTs worth naming ---------------------------------------------------------------
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);

    /// <summary>The transform has taken what it was given and wants another frame. Not an error.</summary>
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    /// <summary>The output format changed underneath us and must be re-negotiated before reading.</summary>
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_TRANSFORM_TYPE_NOT_SET = unchecked((int)0xC00D6D60);
    public const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
    public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    public const int MF_E_ATTRIBUTENOTFOUND = unchecked((int)0xC00D36E6);
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    /// <summary>What GetEvent with NO_WAIT says when there is nothing queued. Not an error.</summary>
    public const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
    public const int MF_E_UNSUPPORTED_D3D_TYPE = unchecked((int)0xC00D3EA2);

    public static string Explain(int hr) => hr switch
    {
        S_OK => "ok",
        MF_E_TRANSFORM_NEED_MORE_INPUT => "the transform wants more input before it will produce anything",
        MF_E_TRANSFORM_STREAM_CHANGE => "the transform's output format changed and must be set again",
        MF_E_TRANSFORM_TYPE_NOT_SET => "the transform's media types have not been set",
        MF_E_INVALIDMEDIATYPE => "that media type was refused",
        MF_E_NO_MORE_TYPES => "no more media types",
        MF_E_ATTRIBUTENOTFOUND => "attribute not found",
        MF_E_NOTACCEPTING => "the transform is not accepting input right now",
        MF_E_NO_EVENTS_AVAILABLE => "no event queued",
        MF_E_UNSUPPORTED_D3D_TYPE => "this Direct3D device or surface format is not supported by the transform",
        E_ACCESSDENIED => "access denied",
        E_INVALIDARG => "invalid argument",
        E_NOTIMPL => "not implemented",
        E_FAIL => "unspecified failure",
        _ => $"HRESULT 0x{hr:X8}"
    };

    // ---- exports -------------------------------------------------------------------------------
    public const uint MF_VERSION = 0x00020070;      // MF_SDK_VERSION (2) << 16 | MF_API_VERSION (0x70)
    public const uint MFSTARTUP_FULL = 0;
    public const uint MFSTARTUP_LITE = 1;

    [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] public static extern int MFShutdown();
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IntPtr mediaType);
    [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);
    [DllImport("mfplat.dll")] public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr manager);
    [DllImport("mfplat.dll")] public static extern int MFCreateDXGISurfaceBuffer(ref Guid riid, IntPtr surface, uint subresourceIndex, [MarshalAs(UnmanagedType.Bool)] bool bottomUpWhenLinear, out IntPtr buffer);
    [DllImport("mfplat.dll")] public static extern int MFTEnumEx(Guid category, uint flags, MFT_REGISTER_TYPE_INFO* input, MFT_REGISTER_TYPE_INFO* output, out IntPtr* activates, out uint count);
    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);

    // ---- categories, formats and attribute keys ------------------------------------------------
    // THESE THREE ARE CHECKABLE AND WERE CHECKED. Every registered category is a subkey of
    // HKLM\SOFTWARE\Classes\MediaFoundation\Transforms\Categories, named by its GUID without
    // braces, so a wrong one can be caught in ten seconds instead of being believed.
    //
    // It is worth the trouble because of how a wrong one fails. MFTEnumEx given a category that
    // does not exist does not complain: it returns S_OK and zero transforms, which is exactly what
    // a machine with no encoder returns. The first version of this file had the video encoder
    // GUID wrong, and the result was a confident, thoroughly documented conclusion that this
    // laptop had no H.264 encoder -- on a laptop with Intel Quick Sync.
    public static Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    public static Guid MFT_CATEGORY_VIDEO_PROCESSOR = new("302ea3fc-aa5f-47f9-9f7a-c2188bb16302");

    public static Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    // The four-character codes, as GUIDs: H264 and NV12 spelled in little-endian ASCII. ARGB32 is
    // D3D format number 21 (D3DFMT_A8R8G8B8), which is what DXGI_FORMAT_B8G8R8A8_UNORM is called
    // on this side of the fence -- the name says ARGB and the bytes are BGRA, and that is not a
    // mistake, it is two subsystems naming the same layout from opposite ends.
    public static Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static Guid MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00aa00389b71");

    public static Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    public static Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    public static Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static Guid MF_SA_D3D11_AWARE = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
    public static Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    public static Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    public static Guid IID_IMFMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");
    public static Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    public static Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    public static Guid IID_IMF2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");

    public const int MFVideoInterlace_Progressive = 2;
    public const int eAVEncH264VProfile_Base = 66;
    public const int eAVEncH264VProfile_Main = 77;
    public const int eAVEncH264VProfile_High = 100;

    public const uint MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
    public const uint MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
    public const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004;
    public const uint MFT_ENUM_FLAG_FIELDOFUSE = 0x00000008;
    public const uint MFT_ENUM_FLAG_LOCALMFT = 0x00000010;
    public const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;

    public const int MFT_MESSAGE_COMMAND_FLUSH = 0;
    public const int MFT_MESSAGE_COMMAND_DRAIN = 1;
    public const int MFT_MESSAGE_SET_D3D_MANAGER = 2;
    // These four are consecutive and two of them were the wrong way round, which cost stage 2.2
    // two machines and a written conclusion that hardware was missing. END_OF_STREAM is 0x...02
    // and START_OF_STREAM is 0x...03 -- the order reads backwards, and swapping them means
    // BeginStreaming told the encoder the stream had ENDED. Every encoder answered S_OK and then
    // correctly never asked for another frame, which looked exactly like silicon that was not
    // there. Same shape as the category GUID in MFTEnumEx: an API saying "success" to a
    // well-formed request that means something else.
    //
    // Checkable, and checked: they are the MFT_MESSAGE_TYPE enum in mftransform.h.
    public const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = unchecked((int)0x10000000);
    public const int MFT_MESSAGE_NOTIFY_END_STREAMING = unchecked((int)0x10000001);
    public const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = unchecked((int)0x10000002);
    public const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = unchecked((int)0x10000003);

    public const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;
    public const uint MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x00000200;
    public const uint MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE = 0x00000100;

    public const uint MF_EVENT_FLAG_NO_WAIT = 0x00000001;
    public const uint METransformNeedInput = 601;
    public const uint METransformHaveOutput = 602;
    public const uint METransformDrainComplete = 603;

    // ---- structures ------------------------------------------------------------------------------

    /// <summary>MFT_REGISTER_TYPE_INFO: the (major, subtype) pair MFTEnumEx filters on.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_REGISTER_TYPE_INFO { public Guid MajorType, Subtype; }

    /// <summary>
    /// MFT_OUTPUT_DATA_BUFFER. 32 bytes on x64: each DWORD is followed by four bytes of padding
    /// because the pointer after it wants eight-byte alignment. Writing this struct without the
    /// padding is a silent corruption, not a compile error.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_DATA_BUFFER
    {
        public uint StreamID; private uint _pad0;
        public IntPtr Sample;
        public uint Status; private uint _pad1;
        public IntPtr Events;
    }

    /// <summary>MFT_OUTPUT_STREAM_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_STREAM_INFO { public uint Flags, Size, Alignment; }

    // ---- vtable plumbing ---------------------------------------------------------------------------
    private static void** V(IntPtr obj) => *(void***)obj;

    public static int QueryInterface(IntPtr obj, ref Guid iid, out IntPtr result)
    {
        fixed (Guid* pi = &iid)
        fixed (IntPtr* pr = &result)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)V(obj)[0])(obj, pi, pr);
    }

    public static uint AddRef(IntPtr obj) =>
        obj == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(obj)[1])(obj);

    public static uint Release(IntPtr obj) =>
        obj == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(obj)[2])(obj);

    public static void ReleaseAndClear(ref IntPtr obj)
    {
        if (obj != IntPtr.Zero) { Release(obj); obj = IntPtr.Zero; }
    }

    // ---- IMFAttributes : IUnknown --------------------------------------------------------------------
    // IUnknown 0-2, then in header order: GetItem(3), GetItemType(4), CompareItem(5), Compare(6),
    // GetUINT32(7), GetUINT64(8), GetDouble(9), GetGUID(10), GetStringLength(11), GetString(12),
    // GetAllocatedString(13), GetBlobSize(14), GetBlob(15), GetAllocatedBlob(16), GetUnknown(17),
    // SetItem(18), DeleteItem(19), DeleteAllItems(20), SetUINT32(21), SetUINT64(22), SetDouble(23),
    // SetGUID(24), SetString(25), SetBlob(26), SetUnknown(27), LockStore(28), UnlockStore(29),
    // GetCount(30), GetItemByIndex(31), CopyAllItems(32).
    //
    // IMFMediaType, IMFSample, IMFActivate and IMFMediaEvent ALL inherit this, so everything they
    // add of their own begins at slot 33. Counting one of their methods from IUnknown instead is
    // the easiest mistake to make in this file.

    public static int GetUINT32(IntPtr attrs, ref Guid key, out uint value)
    {
        fixed (Guid* pk = &key)
        fixed (uint* pv = &value)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)V(attrs)[7])(attrs, pk, pv);
    }

    public static int GetUINT64(IntPtr attrs, ref Guid key, out ulong value)
    {
        fixed (Guid* pk = &key)
        fixed (ulong* pv = &value)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)V(attrs)[8])(attrs, pk, pv);
    }

    public static int GetGUID(IntPtr attrs, ref Guid key, out Guid value)
    {
        fixed (Guid* pk = &key)
        fixed (Guid* pv = &value)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)V(attrs)[10])(attrs, pk, pv);
    }

    public static int GetAllocatedString(IntPtr attrs, ref Guid key, out IntPtr str, out uint length)
    {
        fixed (Guid* pk = &key)
        fixed (IntPtr* ps = &str)
        fixed (uint* pl = &length)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, uint*, int>)V(attrs)[13])(attrs, pk, ps, pl);
    }

    public static int GetBlobSize(IntPtr attrs, ref Guid key, out uint size)
    {
        fixed (Guid* pk = &key)
        fixed (uint* ps = &size)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)V(attrs)[14])(attrs, pk, ps);
    }

    public static int GetBlob(IntPtr attrs, ref Guid key, byte* buf, uint bufSize, out uint written)
    {
        fixed (Guid* pk = &key)
        fixed (uint* pw = &written)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, uint, uint*, int>)V(attrs)[15])(attrs, pk, buf, bufSize, pw);
    }

    public static int SetUINT32(IntPtr attrs, ref Guid key, uint value)
    {
        fixed (Guid* pk = &key)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)V(attrs)[21])(attrs, pk, value);
    }

    public static int SetUINT64(IntPtr attrs, ref Guid key, ulong value)
    {
        fixed (Guid* pk = &key)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)V(attrs)[22])(attrs, pk, value);
    }

    public static int SetGUID(IntPtr attrs, ref Guid key, ref Guid value)
    {
        fixed (Guid* pk = &key)
        fixed (Guid* pv = &value)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)V(attrs)[24])(attrs, pk, pv);
    }

    public static int SetUnknown(IntPtr attrs, ref Guid key, IntPtr unknown)
    {
        fixed (Guid* pk = &key)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, int>)V(attrs)[27])(attrs, pk, unknown);
    }

    /// <summary>A UINT64 attribute holding two UINT32s: width or numerator high, height or denominator low.</summary>
    public static int SetPair(IntPtr attrs, ref Guid key, uint high, uint low) =>
        SetUINT64(attrs, ref key, ((ulong)high << 32) | low);

    public static (uint high, uint low) GetPair(IntPtr attrs, ref Guid key)
    {
        if (GetUINT64(attrs, ref key, out ulong v) != S_OK) return (0, 0);
        return ((uint)(v >> 32), (uint)(v & 0xFFFFFFFF));
    }

    /// <summary>The MFT's own name, for the trace line that says which encoder actually ran.</summary>
    public static string FriendlyName(IntPtr activate)
    {
        if (GetAllocatedString(activate, ref MFT_FRIENDLY_NAME_Attribute, out IntPtr s, out _) != S_OK || s == IntPtr.Zero)
            return "unnamed";
        try { return Marshal.PtrToStringUni(s) ?? "unnamed"; }
        finally { CoTaskMemFree(s); }
    }

    // ---- IMFActivate : IMFAttributes ---------------------------------------------------------------
    // ActivateObject(33), ShutdownObject(34), DetachObject(35).
    public static int ActivateObject(IntPtr activate, ref Guid iid, out IntPtr obj)
    {
        fixed (Guid* pi = &iid)
        fixed (IntPtr* po = &obj)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)V(activate)[33])(activate, pi, po);
    }

    // ---- IMFMediaType : IMFAttributes --------------------------------------------------------------
    // GetMajorType(33), IsCompressedFormat(34), IsEqual(35), GetRepresentation(36),
    // FreeRepresentation(37). None of them are needed: a media type IS its attributes, and every
    // field this code sets or reads is an attribute on the inherited half.

    // ---- IMFSample : IMFAttributes -----------------------------------------------------------------
    // GetSampleFlags(33), SetSampleFlags(34), GetSampleTime(35), SetSampleTime(36),
    // GetSampleDuration(37), SetSampleDuration(38), GetBufferCount(39), GetBufferByIndex(40),
    // ConvertToContiguousBuffer(41), AddBuffer(42), RemoveBufferByIndex(43), RemoveAllBuffers(44),
    // GetTotalLength(45), CopyToBuffer(46).
    public static int SetSampleTime(IntPtr sample, long hns) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)V(sample)[36])(sample, hns);

    public static int SetSampleDuration(IntPtr sample, long hns) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)V(sample)[38])(sample, hns);

    public static int GetBufferByIndex(IntPtr sample, uint index, out IntPtr buffer)
    {
        fixed (IntPtr* p = &buffer)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(sample)[40])(sample, index, p);
    }

    public static int ConvertToContiguousBuffer(IntPtr sample, out IntPtr buffer)
    {
        fixed (IntPtr* p = &buffer)
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(sample)[41])(sample, p);
    }

    public static int AddBuffer(IntPtr sample, IntPtr buffer) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)V(sample)[42])(sample, buffer);

    // ---- IMFMediaBuffer : IUnknown -------------------------------------------------------------------
    // Lock(3), Unlock(4), GetCurrentLength(5), SetCurrentLength(6), GetMaxLength(7).
    public static int Lock(IntPtr buffer, out IntPtr data, out uint maxLength, out uint currentLength)
    {
        fixed (IntPtr* pd = &data)
        fixed (uint* pm = &maxLength)
        fixed (uint* pc = &currentLength)
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, int>)V(buffer)[3])(buffer, pd, pm, pc);
    }

    public static int Unlock(IntPtr buffer) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(buffer)[4])(buffer);

    public static int GetCurrentLength(IntPtr buffer, out uint length)
    {
        fixed (uint* p = &length)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)V(buffer)[5])(buffer, p);
    }

    public static int SetCurrentLength(IntPtr buffer, uint length) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)V(buffer)[6])(buffer, length);

    // ---- IMF2DBuffer : IUnknown ------------------------------------------------------------------------
    // Lock2D(3), Unlock2D(4), GetScanline0AndPitch(5), IsContiguousFormat(6),
    // GetContiguousLength(7), ContiguousCopyTo(8), ContiguousCopyFrom(9).
    //
    // Worth having because a decoded NV12 buffer is usually NOT contiguous: the chroma plane sits
    // at a stride-aligned offset rather than immediately after the luma. Lock() on such a buffer
    // succeeds anyway by handing back a linearised copy, which costs a whole frame of memcpy per
    // frame and hides the real layout. Lock2D gives the true pointer and pitch.
    public static int Lock2D(IntPtr buf2d, out IntPtr scanline0, out int pitch)
    {
        fixed (IntPtr* ps = &scanline0)
        fixed (int* pp = &pitch)
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int*, int>)V(buf2d)[3])(buf2d, ps, pp);
    }

    public static int Unlock2D(IntPtr buf2d) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(buf2d)[4])(buf2d);

    public static int GetContiguousLength(IntPtr buf2d, out uint length)
    {
        fixed (uint* p = &length)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)V(buf2d)[7])(buf2d, p);
    }

    // ---- IMFTransform : IUnknown -------------------------------------------------------------------------
    // GetStreamLimits(3), GetStreamCount(4), GetStreamIDs(5), GetInputStreamInfo(6),
    // GetOutputStreamInfo(7), GetAttributes(8), GetInputStreamAttributes(9),
    // GetOutputStreamAttributes(10), DeleteInputStream(11), AddInputStreams(12),
    // GetInputAvailableType(13), GetOutputAvailableType(14), SetInputType(15), SetOutputType(16),
    // GetInputCurrentType(17), GetOutputCurrentType(18), GetInputStatus(19), GetOutputStatus(20),
    // SetOutputBounds(21), ProcessEvent(22), ProcessMessage(23), ProcessInput(24), ProcessOutput(25).
    public static int GetOutputStreamInfo(IntPtr mft, uint id, out MFT_OUTPUT_STREAM_INFO info)
    {
        fixed (MFT_OUTPUT_STREAM_INFO* p = &info)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, MFT_OUTPUT_STREAM_INFO*, int>)V(mft)[7])(mft, id, p);
    }

    public static int GetAttributes(IntPtr mft, out IntPtr attrs)
    {
        fixed (IntPtr* p = &attrs)
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(mft)[8])(mft, p);
    }

    public static int GetInputAvailableType(IntPtr mft, uint id, uint index, out IntPtr type)
    {
        fixed (IntPtr* p = &type)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)V(mft)[13])(mft, id, index, p);
    }

    public static int GetOutputAvailableType(IntPtr mft, uint id, uint index, out IntPtr type)
    {
        fixed (IntPtr* p = &type)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)V(mft)[14])(mft, id, index, p);
    }

    public static int SetInputType(IntPtr mft, uint id, IntPtr type, uint flags) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)V(mft)[15])(mft, id, type, flags);

    public static int SetOutputType(IntPtr mft, uint id, IntPtr type, uint flags) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)V(mft)[16])(mft, id, type, flags);

    public static int GetOutputCurrentType(IntPtr mft, uint id, out IntPtr type)
    {
        fixed (IntPtr* p = &type)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(mft)[18])(mft, id, p);
    }

    public static int ProcessMessage(IntPtr mft, int message, IntPtr param) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)V(mft)[23])(mft, message, param);

    public static int ProcessInput(IntPtr mft, uint id, IntPtr sample, uint flags) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)V(mft)[24])(mft, id, sample, flags);

    public static int ProcessOutput(IntPtr mft, uint flags, uint count, MFT_OUTPUT_DATA_BUFFER* buffers, out uint status)
    {
        fixed (uint* p = &status)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, MFT_OUTPUT_DATA_BUFFER*, uint*, int>)V(mft)[25])(mft, flags, count, buffers, p);
    }

    // ---- IMFMediaEventGenerator : IUnknown ---------------------------------------------------------------
    // GetEvent(3), BeginGetEvent(4), EndGetEvent(5), QueueEvent(6).
    public static int GetEvent(IntPtr gen, uint flags, out IntPtr ev)
    {
        fixed (IntPtr* p = &ev)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(gen)[3])(gen, flags, p);
    }

    // ---- IMFMediaEvent : IMFAttributes -------------------------------------------------------------------
    // GetType(33), GetExtendedType(34), GetStatus(35), GetValue(36).
    public static int GetEventType(IntPtr ev, out uint type)
    {
        fixed (uint* p = &type)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)V(ev)[33])(ev, p);
    }

    // ---- IMFDXGIDeviceManager : IUnknown -----------------------------------------------------------------
    // CloseDeviceHandle(3), GetVideoService(4), LockDevice(5), OpenDeviceHandle(6), ResetDevice(7),
    // TestDevice(8), UnlockDevice(9). That is the header's order; it looks alphabetical and is not
    // a guess.
    public static int ResetDevice(IntPtr manager, IntPtr device, uint resetToken) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)V(manager)[7])(manager, device, resetToken);

    // ---- ID3D10Multithread : IUnknown --------------------------------------------------------------------
    // Enter(3), Leave(4), SetMultithreadProtected(5), GetMultithreadProtected(6).
    //
    // Needed because the moment a Media Foundation transform is handed our D3D11 device it will
    // use it from its own worker threads. An unprotected device shared that way corrupts quietly.
    public static int SetMultithreadProtected(IntPtr mt, bool on) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)V(mt)[5])(mt, on ? 1 : 0);

    // ---- ICodecAPI : IUnknown -----------------------------------------------------------------------------
    // IsSupported(3), IsModifiable(4), GetParameterRange(5), GetParameterValues(6),
    // GetDefaultValue(7), GetValue(8), SetValue(9), and eight more that are not used.
    //
    // Only SetValue is called, and only for the knobs below. It takes a VARIANT: 24 bytes on x64,
    // the type at offset 0 and the payload at offset 8.
    public static Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static Guid CODECAPI_AVEncCommonMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static Guid CODECAPI_AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    public const uint eAVEncCommonRateControlMode_CBR = 0;
    public const uint eAVEncCommonRateControlMode_PeakConstrainedVBR = 1;
    public const uint eAVEncCommonRateControlMode_UnconstrainedVBR = 2;
    public const uint eAVEncCommonRateControlMode_Quality = 3;
    public const uint eAVEncCommonRateControlMode_LowDelayVBR = 4;

    private const ushort VT_UI4 = 19;
    private const ushort VT_BOOL = 11;

    public static int CodecSetUInt32(IntPtr codecApi, ref Guid api, uint value)
    {
        byte* v = stackalloc byte[24];
        for (int i = 0; i < 24; i++) v[i] = 0;
        *(ushort*)v = VT_UI4;
        *(uint*)(v + 8) = value;
        fixed (Guid* pa = &api)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, int>)V(codecApi)[9])(codecApi, pa, v);
    }

    public static int CodecSetBool(IntPtr codecApi, ref Guid api, bool value)
    {
        byte* v = stackalloc byte[24];
        for (int i = 0; i < 24; i++) v[i] = 0;
        *(ushort*)v = VT_BOOL;
        *(short*)(v + 8) = value ? (short)-1 : (short)0;   // VARIANT_TRUE is -1, not 1
        fixed (Guid* pa = &api)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, int>)V(codecApi)[9])(codecApi, pa, v);
    }

    // ---- start-up ---------------------------------------------------------------------------------------

    private static int _started;
    private static readonly object StartLock = new();

    /// <summary>
    /// MFStartup, once per process and never shut down. Reference-counting it across an encoder in
    /// the host and a decoder in the console would buy nothing and risks the classic fault: the
    /// last object disposing, MFShutdown running, and the next session of the same process finding
    /// a dead platform.
    /// </summary>
    public static void Startup()
    {
        lock (StartLock)
        {
            if (_started > 0) return;
            if (_started < 0) throw new NotSupportedException("Media Foundation could not be started on this machine");
            // FULL, not LITE. An asynchronous hardware transform delivers METransformNeedInput and
            // METransformHaveOutput through Media Foundation's work queues, and a platform started
            // without them accepts every call, reports success to every message, and then simply
            // never raises an event -- which reads as a machine with a broken encoder rather than
            // as a start-up flag.
            int hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
            _started = hr == S_OK ? 1 : -1;
            if (hr != S_OK) throw new NotSupportedException($"Media Foundation could not be started: {Explain(hr)}");
        }
    }

    /// <summary>
    /// Every video transform of a category that will take the input and produce the output, best
    /// first. Returns the activation objects; the caller owns them and must Release every one.
    /// </summary>
    public static List<IntPtr> Enumerate(Guid category, uint flags, Guid inMajor, Guid inSub, Guid outMajor, Guid outSub)
    {
        var inInfo = new MFT_REGISTER_TYPE_INFO { MajorType = inMajor, Subtype = inSub };
        var outInfo = new MFT_REGISTER_TYPE_INFO { MajorType = outMajor, Subtype = outSub };
        var list = new List<IntPtr>();
        LastEnumResult = MFTEnumEx(category, flags, &inInfo, &outInfo, out IntPtr* activates, out uint count);
        if (LastEnumResult != S_OK || activates == null) return list;
        try { for (uint i = 0; i < count; i++) list.Add(activates[i]); }
        finally { CoTaskMemFree((IntPtr)activates); }
        return list;
    }

    /// <summary>
    /// What the last <see cref="Enumerate"/> returned. An empty list and a failed call look the
    /// same to the caller and mean very different things — "this machine has no encoder" against
    /// "we asked the question wrongly" — and the second one is a bug in this file.
    /// </summary>
    public static int LastEnumResult { get; private set; }

    /// <summary>
    /// Every transform of a category, unfiltered, with its name. For the diagnostic in
    /// `codecbench --mfts`: when an enumeration comes back empty, the only useful next question is
    /// what the machine has at all.
    /// </summary>
    public static List<string> Describe(Guid category, uint flags)
    {
        var names = new List<string>();
        int hr = MFTEnumEx(category, flags, null, null, out IntPtr* activates, out uint count);
        if (hr != S_OK) { names.Add($"(the enumeration failed: {Explain(hr)})"); return names; }
        if (activates == null || count == 0) return names;
        try { for (uint i = 0; i < count; i++) { names.Add(FriendlyName(activates[i])); Release(activates[i]); } }
        finally { CoTaskMemFree((IntPtr)activates); }
        return names;
    }
}
