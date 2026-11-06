using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace RemoteSupport.Media;

/// <summary>
/// A PDF's pages as pictures, drawn by Windows' own PDF renderer (<c>Windows.Data.Pdf</c>) — for
/// remote printing (roadmap 1.8, 2026-10-06), where a print job travels as a PDF ("Microsoft Print to
/// PDF" is on every Windows 10/11; the XPS writer is not — it is absent on Windows 11 25H2) and has to
/// be printed at the other end with nothing bought in.
///
/// <para>Called through its vtables by hand rather than through the C#/WinRT projection: targeting
/// <c>net10.0-windows10.0.x</c> to get the projection would put the 20-odd MB Windows SDK assembly into
/// the console download and the customer's installer, for four interfaces. The interface IDs and the
/// method order below were read from <c>C:\Windows\System32\WinMetadata\*.winmd</c>, not from memory.</para>
///
/// <para>Every WinRT call here is synchronous from the caller's point of view: the async ones are
/// polled through <c>IAsyncInfo</c> until they finish. Use from any thread with COM initialised (every
/// .NET thread is).</para>
/// </summary>
public sealed unsafe class PdfPages : IDisposable
{
    private static readonly Guid IID_IPdfDocumentStatics = new("433A0B5F-C007-4788-90F2-08143D922599");
    private static readonly Guid IID_IPdfPageRenderOptions = new("3C98056F-B7CF-4C29-9A04-52D90267F425");
    private static readonly Guid IID_IRandomAccessStream = new("905A0FE1-BC53-11DF-8C49-001E4FC686DA");
    private static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    private static readonly Guid IID_IClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private static readonly Guid IID_IStream = new("0000000C-0000-0000-C000-000000000046");

    // IInspectable takes slots 0-5 (IUnknown 0-2, then GetIids, GetRuntimeClassName, GetTrustLevel).
    private const int Statics_LoadFromStreamAsync = 8;      // IPdfDocumentStatics
    private const int Doc_GetPage = 6, Doc_get_PageCount = 7; // IPdfDocument
    private const int Page_RenderWithOptions = 7, Page_get_Size = 10; // IPdfPage
    private const int Opt_put_DestinationWidth = 9, Opt_put_DestinationHeight = 11; // IPdfPageRenderOptions
    private const int Async_get_Status = 7, Async_get_ErrorCode = 8; // IAsyncInfo
    private const int Op_GetResults = 8;                    // IAsyncOperation<T> and IAsyncAction alike
    private const int Closable_Close = 6;

    private IntPtr _doc;       // IPdfDocument
    private IntPtr _file;      // IRandomAccessStream on the file

    /// <summary>How many pages the document has.</summary>
    public int Count { get; }

    /// <summary>Opens a PDF. Throws with a reason when Windows cannot read it (damaged, or password-protected).</summary>
    public PdfPages(string path)
    {
        Check(CreateRandomAccessStreamOnFile(Path.GetFullPath(path), 0 /* Read */, IID_IRandomAccessStream, out _file), "open the file");
        IntPtr statics = Factory("Windows.Data.Pdf.PdfDocument", IID_IPdfDocumentStatics);
        try
        {
            IntPtr op;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, Statics_LoadFromStreamAsync))(statics, _file, &op), "read the PDF");
            try { _doc = Result(op, "read the PDF"); }
            finally { Marshal.Release(op); }
        }
        finally { Marshal.Release(statics); }
        uint n;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(_doc, Doc_get_PageCount))(_doc, &n), "count the pages");
        Count = (int)n;
    }

    /// <summary>A page's size in inches — the PDF's own, so a printer can be told portrait or landscape.</summary>
    public SizeF PageInches(int index)
    {
        var page = Page(index);
        try { return Size(page); }
        finally { Marshal.Release(page); }
    }

    /// <summary>
    /// What Windows multiplies the asked-for size by. It renders in device-independent pixels and then
    /// applies the display's zoom (measured: 1.4 on this laptop), so the first page tells us and the
    /// rest are asked for divided by it — the picture comes out the size wanted, not 40% bigger.
    /// </summary>
    private static double _zoom = 1.0;

    /// <summary>One page drawn at <paramref name="dpi"/>, on white. The caller disposes the bitmap.</summary>
    public Bitmap Render(int index, int dpi)
    {
        var page = Page(index);
        IntPtr options = IntPtr.Zero, optionsI = IntPtr.Zero, outStream = IntPtr.Zero, outI = IntPtr.Zero, action = IntPtr.Zero, istream = IntPtr.Zero;
        try
        {
            var inches = Size(page);
            double zoom = Volatile.Read(ref _zoom);
            uint w = (uint)Math.Clamp(inches.Width * dpi / zoom, 1, 12000), h = (uint)Math.Clamp(inches.Height * dpi / zoom, 1, 12000);

            options = Activate("Windows.Data.Pdf.PdfPageRenderOptions");
            optionsI = Query(options, IID_IPdfPageRenderOptions);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(optionsI, Opt_put_DestinationWidth))(optionsI, w), "size the page");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(optionsI, Opt_put_DestinationHeight))(optionsI, h), "size the page");

            outStream = Activate("Windows.Storage.Streams.InMemoryRandomAccessStream");
            outI = Query(outStream, IID_IRandomAccessStream);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Slot(page, Page_RenderWithOptions))(page, outI, optionsI, &action), "draw the page");
            Wait(action, "draw the page");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(action, Op_GetResults))(action), "draw the page");

            // The page is a PNG in the in-memory stream; read it back as a classic COM IStream.
            Check(CreateStreamOverRandomAccessStream(outI, IID_IStream, out istream), "read the drawn page");
            var s = (IStream)Marshal.GetObjectForIUnknown(istream);
            s.Stat(out var stat, 1 /* STATFLAG_NONAME */);
            s.Seek(0, 0, IntPtr.Zero);
            var bytes = new byte[stat.cbSize];
            int got = 0;
            var gotPtr = Marshal.AllocHGlobal(4);
            try
            {
                while (got < bytes.Length)
                {
                    var chunk = new byte[Math.Min(1 << 20, bytes.Length - got)];
                    s.Read(chunk, chunk.Length, gotPtr);
                    int n = Marshal.ReadInt32(gotPtr);
                    if (n <= 0) break;
                    Buffer.BlockCopy(chunk, 0, bytes, got, n);
                    got += n;
                }
            }
            finally { Marshal.FreeHGlobal(gotPtr); Marshal.ReleaseComObject(s); }
            using var ms = new MemoryStream(bytes, 0, got);
            using var img = Image.FromStream(ms);
            var bmp = new Bitmap(img);
            Volatile.Write(ref _zoom, Math.Clamp((double)img.Width / w, 0.25, 4));
            // Its resolution is whatever makes it the page's real size, whatever Windows drew it at.
            bmp.SetResolution(img.Width / inches.Width, img.Height / inches.Height);
            return bmp;
        }
        finally
        {
            foreach (var p in new[] { istream, action, outI, outStream, optionsI, options, page })
                if (p != IntPtr.Zero) Marshal.Release(p);
        }
    }

    public void Dispose()
    {
        if (_doc != IntPtr.Zero) { Marshal.Release(_doc); _doc = IntPtr.Zero; }
        if (_file != IntPtr.Zero)
        {
            // Close the file so it can be deleted straight away, rather than when the GC gets round to it.
            try
            {
                var c = Query(_file, IID_IClosable);
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(c, Closable_Close))(c);
                Marshal.Release(c);
            }
            catch { }
            Marshal.Release(_file); _file = IntPtr.Zero;
        }
    }

    // ---- the plumbing ----------------------------------------------------------------------

    private IntPtr Page(int index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        IntPtr page;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(_doc, Doc_GetPage))(_doc, (uint)index, &page), "open the page");
        return page;
    }

    private static SizeF Size(IntPtr page)
    {
        // Windows.Foundation.Size: two floats, in device-independent pixels (1/96 inch).
        float* sz = stackalloc float[2];
        Check(((delegate* unmanaged[Stdcall]<IntPtr, float*, int>)Slot(page, Page_get_Size))(page, sz), "measure the page");
        return new SizeF(sz[0] / 96f, sz[1] / 96f);
    }

    private static IntPtr Slot(IntPtr obj, int i) => (*(IntPtr**)obj)[i];

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"Windows could not {what} (0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
    }

    private static IntPtr Query(IntPtr obj, Guid iid)
    {
        Check(Marshal.QueryInterface(obj, ref iid, out var p), "use the PDF reader");
        return p;
    }

    /// <summary>Polls an async operation until it is done; throws with its error if it failed.</summary>
    private static void Wait(IntPtr op, string what)
    {
        var info = Query(op, IID_IAsyncInfo);
        try
        {
            var until = Environment.TickCount64 + 60000;
            while (true)
            {
                int status;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, Async_get_Status))(info, &status), what);
                if (status == 1) return;                       // Completed
                if (status == 2) throw new OperationCanceledException($"Windows stopped trying to {what}.");
                if (status == 3)
                {
                    int err;
                    ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, Async_get_ErrorCode))(info, &err);
                    Check(err < 0 ? err : unchecked((int)0x80004005), what);
                }
                if (Environment.TickCount64 > until) throw new TimeoutException($"Windows took over a minute to {what}.");
                Thread.Sleep(5);
            }
        }
        finally { Marshal.Release(info); }
    }

    /// <summary>Waits for an IAsyncOperation&lt;T&gt; and returns its result, an interface pointer the caller releases.</summary>
    private static IntPtr Result(IntPtr op, string what)
    {
        Wait(op, what);
        IntPtr r;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(op, Op_GetResults))(op, &r), what);
        return r;
    }

    private static IntPtr Factory(string runtimeClass, Guid iid)
    {
        Check(WindowsCreateString(runtimeClass, (uint)runtimeClass.Length, out var h), "name the PDF reader");
        try { Check(RoGetActivationFactory(h, ref iid, out var f), "start the PDF reader"); return f; }
        finally { WindowsDeleteString(h); }
    }

    private static IntPtr Activate(string runtimeClass)
    {
        Check(WindowsCreateString(runtimeClass, (uint)runtimeClass.Length, out var h), "name " + runtimeClass);
        try { Check(RoActivateInstance(h, out var inst), "create " + runtimeClass); return inst; }
        finally { WindowsDeleteString(h); }
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string s, uint len, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")] private static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);
    [DllImport("shcore.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateRandomAccessStreamOnFile(string path, uint mode, in Guid riid, out IntPtr stream);
    [DllImport("shcore.dll")] private static extern int CreateStreamOverRandomAccessStream(IntPtr randomAccessStream, in Guid riid, out IntPtr stream);
}
