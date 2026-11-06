using System.Runtime.InteropServices;

namespace RemoteSupport.Host;

/// <summary>
/// Raw DXGI and Direct3D 11 interop for Desktop Duplication. Nothing is bought in, so there is no
/// SharpDX or Vortice here: COM objects are IntPtrs and their methods are called through the
/// vtable by slot.
///
/// READ THIS BEFORE EDITING. A slot number is an index into a function-pointer table. Get one
/// wrong and the process does not throw -- it calls a different method with the wrong arguments
/// and dies, or worse, does not die. Every slot below is written as "interface method N", counted
/// from the start of the whole inheritance chain including IUnknown's three, and the chain is
/// spelled out so the count can be re-derived rather than trusted. `tools/codecbench --compare`
/// exists partly to catch exactly this class of mistake: it captures the same screen through this
/// file and through GDI and compares the pixels, so a vtable that is subtly wrong shows up as a
/// picture that is wrong rather than as a crash three weeks later on a customer's machine.
/// </summary>
internal static unsafe class Dxgi
{
    // ---- HRESULTs worth naming -----------------------------------------------------------
    public const int S_OK = 0;
    public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);   // nothing changed; not an error
    public const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);    // desktop switch, mode change, driver reset
    public const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);
    public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    public const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);

    public static string Explain(int hr) => hr switch
    {
        DXGI_ERROR_WAIT_TIMEOUT => "no new frame (the screen has not changed)",
        DXGI_ERROR_ACCESS_LOST => "access lost (desktop switch, resolution change or driver reset)",
        DXGI_ERROR_UNSUPPORTED => "Desktop Duplication is not supported on this adapter",
        DXGI_ERROR_NOT_FOUND => "not found",
        DXGI_ERROR_INVALID_CALL => "invalid call (already holding a frame, or duplication already open)",
        // The one that catches people out: a second duplication of the same output, ANYWHERE on
        // the machine, is refused. If a screen recorder is running, this is what we get.
        E_ACCESSDENIED => "access denied (another process already has this output duplicated, or this session may not)",
        E_INVALIDARG => "invalid argument",
        S_OK => "ok",
        _ => $"HRESULT 0x{hr:X8}"
    };

    // ---- IIDs -----------------------------------------------------------------------------
    public static Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    public static Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // ---- exports ---------------------------------------------------------------------------
    [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(
        IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr context);

    public const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    public const uint D3D11_SDK_VERSION = 7;
    // BGRA support is not needed for reading pixels back, but asking for it keeps us on the same
    // path every other desktop-capture consumer uses. VIDEO_SUPPORT is what the stage 2.2 H.264
    // encoder needs: Media Foundation reaches this device through a device manager and will query
    // it for ID3D11VideoDevice, which a device created without this flag does not have. Both are
    // asked for at creation because re-creating the device later would mean tearing down the
    // duplication with it.
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const uint D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 0x800;

    // ---- structures -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>DXGI_OUTPUT_DESC. 96 bytes; the fixed 32-WCHAR name is what makes it that size.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public RECT DesktopCoordinates;
        [MarshalAs(UnmanagedType.Bool)] public bool AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    /// <summary>DXGI_OUTDUPL_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_DESC
    {
        public int ModeWidth, ModeHeight;      // DXGI_MODE_DESC is bigger than this; see GetDesc below
        public int RefreshNumerator, RefreshDenominator;
        public int Format, ScanlineOrdering, Scaling;
        [MarshalAs(UnmanagedType.Bool)] public bool DesktopImageInSystemMemory;
    }

    /// <summary>DXGI_OUTDUPL_FRAME_INFO, 48 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        // Windows' BOOL is four bytes, and these are INTS on purpose. AcquireNextFrame is handed a raw
        // pointer to this struct, where MarshalAs does nothing and a C# bool takes ONE byte — so from
        // 2026-09-16 to 2026-10-15 every field from here on was read from the wrong place, and
        // TotalMetadataBufferSize was really PointerShapeBufferSize, nearly always 0. Nothing visibly
        // broke because the fields the capture used came before the first bool (HANDOVER §142).
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX, PointerY;
        public int PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    /// <summary>DXGI_OUTDUPL_MOVE_RECT: where a block of pixels moved FROM, and the rect it now occupies.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_MOVE_RECT { public int SourceX, SourceY; public RECT Destination; }

    /// <summary>D3D11_TEXTURE2D_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_MAPPED_SUBRESOURCE { public IntPtr pData; public uint RowPitch, DepthPitch; }

    /// <summary>D3D11_SUBRESOURCE_DATA, for handing a texture its contents at creation.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_SUBRESOURCE_DATA { public IntPtr pSysMem; public uint SysMemPitch, SysMemSlicePitch; }

    public const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const int D3D11_USAGE_DEFAULT = 0;
    public const int D3D11_USAGE_STAGING = 3;
    public const uint D3D11_CPU_ACCESS_READ = 0x20000;
    public const uint D3D11_MAP_READ = 1;
    public const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    public const uint D3D11_BIND_RENDER_TARGET = 0x20;

    // ---- vtable plumbing ---------------------------------------------------------------------
    // Every COM object here is an IntPtr whose first field points at its vtable.

    private static void** V(IntPtr obj) => *(void***)obj;

    public static int QueryInterface(IntPtr obj, ref Guid iid, out IntPtr result)
    {
        fixed (Guid* pi = &iid)
        fixed (IntPtr* pr = &result)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)V(obj)[0])(obj, pi, pr);
    }

    public static uint Release(IntPtr obj) =>
        obj == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(obj)[2])(obj);

    public static void ReleaseAndClear(ref IntPtr obj)
    {
        if (obj != IntPtr.Zero) { Release(obj); obj = IntPtr.Zero; }
    }

    // ---- IDXGIFactory1 : IDXGIFactory : IDXGIObject : IUnknown -------------------------------
    // IUnknown 0-2. IDXGIObject adds SetPrivateData(3), SetPrivateDataInterface(4),
    // GetPrivateData(5), GetParent(6). IDXGIFactory adds EnumAdapters(7),
    // MakeWindowAssociation(8), GetWindowAssociation(9), CreateSwapChain(10),
    // CreateSoftwareAdapter(11). IDXGIFactory1 adds EnumAdapters1(12), IsCurrent(13).
    public static int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter)
    {
        fixed (IntPtr* p = &adapter)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(factory)[12])(factory, index, p);
    }

    // ---- IDXGIAdapter : IDXGIObject : IUnknown ------------------------------------------------
    // EnumOutputs(7), GetDesc(8), CheckInterfaceSupport(9).
    public static int EnumOutputs(IntPtr adapter, uint index, out IntPtr output)
    {
        fixed (IntPtr* p = &output)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(adapter)[7])(adapter, index, p);
    }

    // ---- IDXGIOutput : IDXGIObject : IUnknown --------------------------------------------------
    // GetDesc(7), GetDisplayModeList(8), FindClosestMatchingMode(9), WaitForVBlank(10),
    // TakeOwnership(11), ReleaseOwnership(12), GetGammaControlCapabilities(13),
    // SetGammaControl(14), GetGammaControl(15), SetDisplaySurface(16), GetDisplaySurfaceData(17),
    // GetFrameStatistics(18).
    public static int GetOutputDesc(IntPtr output, out DXGI_OUTPUT_DESC desc)
    {
        desc = default;
        // Blittable read into a raw buffer, then marshal: DXGI_OUTPUT_DESC carries a fixed-length
        // string, so it cannot be passed by pointer as a managed struct.
        var buf = Marshal.AllocHGlobal(Marshal.SizeOf<DXGI_OUTPUT_DESC>());
        try
        {
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)V(output)[7])(output, buf);
            if (hr == S_OK) desc = Marshal.PtrToStructure<DXGI_OUTPUT_DESC>(buf);
            return hr;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---- IDXGIOutput1 : IDXGIOutput -------------------------------------------------------------
    // GetDisplayModeList1(19), FindClosestMatchingMode1(20), GetDisplaySurfaceData1(21),
    // DuplicateOutput(22).
    public static int DuplicateOutput(IntPtr output1, IntPtr device, out IntPtr duplication)
    {
        fixed (IntPtr* p = &duplication)
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)V(output1)[22])(output1, device, p);
    }

    // ---- IDXGIDevice : IDXGIObject ---------------------------------------------------------------
    // GetAdapter(7), CreateSurface(8), QueryResourceResidency(9), SetGPUThreadPriority(10),
    // GetGPUThreadPriority(11).
    public static int GetAdapter(IntPtr dxgiDevice, out IntPtr adapter)
    {
        fixed (IntPtr* p = &adapter)
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(dxgiDevice)[7])(dxgiDevice, p);
    }

    // ---- IDXGIOutputDuplication : IDXGIObject -----------------------------------------------------
    // GetDesc(7), AcquireNextFrame(8), GetFrameDirtyRects(9), GetFrameMoveRects(10),
    // GetFramePointerShape(11), MapDesktopSurface(12), UnMapDesktopSurface(13), ReleaseFrame(14).
    public static void GetDuplDesc(IntPtr dupl, out DXGI_OUTDUPL_DESC desc)
    {
        // DXGI_OUTDUPL_DESC begins with a DXGI_MODE_DESC (28 bytes) and ends with a BOOL. Read it
        // into a generous buffer rather than mirroring DXGI_MODE_DESC exactly: only the last
        // field is wanted and getting the middle wrong would be silent.
        var buf = Marshal.AllocHGlobal(64);
        try
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)V(dupl)[7])(dupl, buf);
            desc = new DXGI_OUTDUPL_DESC
            {
                ModeWidth = Marshal.ReadInt32(buf, 0),
                ModeHeight = Marshal.ReadInt32(buf, 4),
                RefreshNumerator = Marshal.ReadInt32(buf, 8),
                RefreshDenominator = Marshal.ReadInt32(buf, 12),
                Format = Marshal.ReadInt32(buf, 16),
                ScanlineOrdering = Marshal.ReadInt32(buf, 20),
                Scaling = Marshal.ReadInt32(buf, 24),
                DesktopImageInSystemMemory = Marshal.ReadInt32(buf, 28) != 0,
            };
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static int AcquireNextFrame(IntPtr dupl, uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource)
    {
        fixed (DXGI_OUTDUPL_FRAME_INFO* pi = &info)
        fixed (IntPtr* pr = &resource)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, DXGI_OUTDUPL_FRAME_INFO*, IntPtr*, int>)V(dupl)[8])(dupl, timeoutMs, pi, pr);
    }

    public static int GetFrameDirtyRects(IntPtr dupl, uint bufferSize, RECT* buffer, out uint required)
    {
        fixed (uint* p = &required)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, RECT*, uint*, int>)V(dupl)[9])(dupl, bufferSize, buffer, p);
    }

    public static int GetFrameMoveRects(IntPtr dupl, uint bufferSize, DXGI_OUTDUPL_MOVE_RECT* buffer, out uint required)
    {
        fixed (uint* p = &required)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, DXGI_OUTDUPL_MOVE_RECT*, uint*, int>)V(dupl)[10])(dupl, bufferSize, buffer, p);
    }

    public static int ReleaseFrame(IntPtr dupl) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(dupl)[14])(dupl);

    // ---- ID3D11Device : IUnknown -------------------------------------------------------------------
    // CreateBuffer(3), CreateTexture1D(4), CreateTexture2D(5). Nothing past slot 5 is called: the
    // immediate context comes back from D3D11CreateDevice directly, which is why
    // GetImmediateContext -- slot 40, and the one most likely to be miscounted -- is not needed.
    public static int CreateTexture2D(IntPtr device, ref D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture)
    {
        fixed (D3D11_TEXTURE2D_DESC* pd = &desc)
        fixed (IntPtr* pt = &texture)
            return ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)V(device)[5])(device, pd, initialData, pt);
    }

    // ---- ID3D11DeviceContext : ID3D11DeviceChild : IUnknown ------------------------------------------
    // IUnknown 0-2. ID3D11DeviceChild adds GetDevice(3), GetPrivateData(4), SetPrivateData(5),
    // SetPrivateDataInterface(6). So context method N sits at slot 6+N, and the order in d3d11.h is:
    //   1 VSSetConstantBuffers   2 PSSetShaderResources  3 PSSetShader      4 PSSetSamplers
    //   5 VSSetShader            6 DrawIndexed           7 Draw             8 Map
    //   9 Unmap                 10 PSSetConstantBuffers 11 IASetInputLayout 12 IASetVertexBuffers
    //  13 IASetIndexBuffer      14 DrawIndexedInstanced 15 DrawInstanced   16 GSSetConstantBuffers
    //  17 GSSetShader           18 IASetPrimitiveTopology 19 VSSetShaderResources 20 VSSetSamplers
    //  21 Begin                 22 End                  23 GetData         24 SetPredication
    //  25 GSSetShaderResources  26 GSSetSamplers        27 OMSetRenderTargets
    //  28 OMSetRenderTargetsAndUnorderedAccessViews     29 OMSetBlendState 30 OMSetDepthStencilState
    //  31 SOSetTargets          32 DrawAuto             33 DrawIndexedInstancedIndirect
    //  34 DrawInstancedIndirect 35 Dispatch             36 DispatchIndirect 37 RSSetState
    //  38 RSSetViewports        39 RSSetScissorRects    40 CopySubresourceRegion 41 CopyResource
    // Giving Map = 14, Unmap = 15, CopyResource = 47.
    public static int Map(IntPtr context, IntPtr resource, uint subresource, uint mapType, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        fixed (D3D11_MAPPED_SUBRESOURCE* pm = &mapped)
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)V(context)[14])
                   (context, resource, subresource, mapType, flags, pm);
    }

    public static void Unmap(IntPtr context, IntPtr resource, uint subresource) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)V(context)[15])(context, resource, subresource);

    public static void CopyResource(IntPtr context, IntPtr dst, IntPtr src) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)V(context)[47])(context, dst, src);
}
