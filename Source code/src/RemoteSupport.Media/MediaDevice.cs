namespace RemoteSupport.Media;

/// <summary>
/// The graphics device, as Media Foundation is willing to see it.
///
/// A transform is never handed a Direct3D device directly; it is handed a device MANAGER, and the
/// device must be multithread-protected before that happens because from then on the transform's
/// own worker threads use it alongside whatever created it. Both of those are one-line mistakes
/// that produce corruption rather than errors, so they live in one place that everything shares.
///
/// It is separate from <see cref="H264Encoder"/> so that the colour converter and the encoder can
/// share one manager for one device -- and so that each of them can be built and measured on its
/// own, which on a machine with no encoder is the difference between proving most of this code and
/// proving none of it.
/// </summary>
public sealed class MediaDevice : IDisposable
{
    /// <summary>The IMFDXGIDeviceManager to give a transform with MFT_MESSAGE_SET_D3D_MANAGER.</summary>
    public IntPtr Manager { get; private set; }

    public MediaDevice(IntPtr d3dDevice)
    {
        Mf.Startup();
        if (Mf.QueryInterface(d3dDevice, ref Mf.IID_ID3D10Multithread, out IntPtr mt) == Mf.S_OK && mt != IntPtr.Zero)
        {
            try { Mf.SetMultithreadProtected(mt, true); } finally { Mf.Release(mt); }
        }
        int hr = Mf.MFCreateDXGIDeviceManager(out uint token, out IntPtr manager);
        if (hr != Mf.S_OK || manager == IntPtr.Zero)
            throw new NotSupportedException($"a Direct3D device manager could not be created: {Mf.Explain(hr)}");
        hr = Mf.ResetDevice(manager, d3dDevice, token);
        if (hr != Mf.S_OK)
        {
            Mf.Release(manager);
            throw new NotSupportedException($"Media Foundation refused this Direct3D device: {Mf.Explain(hr)}");
        }
        Manager = manager;
    }

    public void Dispose()
    {
        var m = Manager; Manager = IntPtr.Zero;
        Mf.ReleaseAndClear(ref m);
    }
}
