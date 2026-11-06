using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteSupport.Host;

/// <summary>
/// The customer's real mouse pointer — the actual bitmap Windows is drawing, not a ring drawn over
/// the picture by the console.
///
/// ---- Why this is not GetFramePointerShape ---------------------------------------------------
///
/// The roadmap says to take the shape from Desktop Duplication, which does hand it over
/// (`IDXGIOutputDuplication::GetFramePointerShape`). This uses `GetCursorInfo` and `GetIconInfo`
/// instead, for one reason that decided it: **duplication is not always the capture path.** It is
/// refused in some session and driver states and the GDI fallback exists precisely for those, so a
/// cursor that only works on the fast path would leave exactly the machines that are already having
/// a bad day looking at a yellow ring. This works on both paths, needs no new DXGI interop, and the
/// host is already on the right desktop for it because the cursor loop follows the user.
///
/// ---- Why a duplicated frame has no cursor in it ---------------------------------------------
///
/// Windows composes the pointer separately from the desktop, so neither BitBlt nor Desktop
/// Duplication brings it back with the pixels. That is not a bug to work around; it is what makes
/// a remote cursor possible to draw at the right moment rather than baked into a stale frame.
///
/// ---- The formats, and the one that matters --------------------------------------------------
///
/// A cursor is a mask bitmap and, usually, a colour bitmap. **The ordinary arrow has no colour
/// bitmap at all** — it is monochrome, and its mask is twice the height: the top half is the AND
/// mask and the bottom half the XOR mask. Handling only the colour case would work perfectly on
/// every I-beam and hand and fail on the one pointer every session spends its life looking at.
/// </summary>
public static class CursorShape
{
    /// <summary>One pointer, converted to something a console can draw.</summary>
    public sealed record Image(long Id, int Width, int Height, int HotX, int HotY, byte[] Png);

    /// <summary>
    /// What the pointer is doing right now. <paramref name="id"/> is the cursor HANDLE, which
    /// Windows keeps stable for the life of a shape — so it doubles as the cache key that stops us
    /// re-sending the arrow a thousand times a session.
    /// </summary>
    /// <summary>Why the last <see cref="TryPoll"/> failed, for the trace. Empty when it worked.</summary>
    public static string LastPollError { get; private set; } = "";

    public static bool TryPoll(out long id, out bool visible, out POINT at)
    {
        id = 0; visible = false; at = default;
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci))
        {
            LastPollError = $"GetCursorInfo failed, Win32 error {Marshal.GetLastWin32Error()}";
            return false;
        }
        LastPollError = "";
        visible = (ci.flags & CURSOR_SHOWING) != 0;
        at = ci.ptScreenPos;
        id = ci.hCursor.ToInt64();
        return true;
    }

    /// <summary>
    /// Renders the pointer with the given handle to a PNG with a real alpha channel.
    ///
    /// Returns null rather than throwing: a cursor that cannot be read is a cosmetic loss and the
    /// console still has the ring to fall back on, whereas an exception here would take out the
    /// loop that also carries the pointer's POSITION, which is the half that actually matters.
    /// </summary>
    public static Image? TryRender(long id)
    {
        IntPtr h = new(id);
        if (h == IntPtr.Zero) return null;
        if (!GetIconInfo(h, out var ii)) return null;
        try
        {
            if (ii.hbmMask == IntPtr.Zero) return null;
            var mask = ReadBgra(ii.hbmMask, out int mw, out int mh);
            if (mask == null) return null;

            Bitmap rendered;
            if (ii.hbmColor != IntPtr.Zero)
            {
                // A colour cursor. The colour bitmap is the picture and the mask says which of it
                // shows -- but many colour cursors already carry a real alpha channel, in which
                // case the mask is all zeroes and applying it would erase the whole pointer. So
                // the alpha is used when there is any, and the mask only when there is not.
                var colour = ReadBgra(ii.hbmColor, out int cw, out int ch);
                if (colour == null) return null;
                rendered = FromColour(colour, cw, ch, mask, mw, mh);
            }
            else
            {
                rendered = FromMonochrome(mask, mw, mh);
            }

            using (rendered)
            {
                using var ms = new MemoryStream();
                rendered.Save(ms, ImageFormat.Png);
                return new Image(id, rendered.Width, rendered.Height, ii.xHotspot, ii.yHotspot, ms.ToArray());
            }
        }
        catch { return null; }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
    }

    /// <summary>
    /// Reads an HBITMAP's real pixels as top-down 32-bit BGRA.
    ///
    /// **`Bitmap.FromHbitmap` cannot be used here and the first version of this used it anyway.**
    /// It throws the alpha channel away, so every pixel of every colour cursor came back opaque —
    /// which made the "does this cursor carry its own alpha?" test answer yes for all of them, so
    /// the mask was never applied and the pointer rendered as a solid 48x48 rectangle. Six of the
    /// seven standard cursors were wrong and the only one that survived was the one with no colour
    /// bitmap at all. `codecbench --cursor` found it on its first run by counting transparent
    /// pixels, which is the entire reason that check exists.
    /// </summary>
    private static byte[]? ReadBgra(IntPtr hbm, out int width, out int height)
    {
        width = height = 0;
        var bm = new BITMAP();
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bm) == 0) return null;
        width = bm.bmWidth; height = bm.bmHeight;
        if (width <= 0 || height <= 0 || width > 1024 || height > 2048) return null;

        var bi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,      // negative: top-down, so row 0 is the top row
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,       // BI_RGB
        };
        var buf = new byte[width * height * 4];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                int got = GetDIBits(dc, hbm, 0, (uint)height, handle.AddrOfPinnedObject(), ref bi, 0);
                if (got == 0) return null;
            }
            finally { handle.Free(); }
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        return buf;
    }

    /// <summary>
    /// The facts about one cursor, for when a pointer renders wrongly and the question is which of
    /// the three formats it actually is. Diagnostics rather than decoration: the I-beam rendered
    /// completely empty once and no amount of reading the code said why.
    /// </summary>
    public static string Describe(long id)
    {
        IntPtr h = new(id);
        if (h == IntPtr.Zero || !GetIconInfo(h, out var ii)) return "could not be read";
        try
        {
            string s = $"hot {ii.xHotspot},{ii.yHotspot}";
            if (ii.hbmMask != IntPtr.Zero)
            {
                var m = ReadBgra(ii.hbmMask, out int mw, out int mh);
                s += $"; mask {mw}x{mh}";
                if (m != null)
                {
                    int white = 0;
                    for (int i = 2; i < m.Length; i += 4) if (m[i] > 127) white++;
                    s += $" ({white * 100 / Math.Max(1, mw * mh)}% white)";
                }
            }
            else s += "; no mask";
            if (ii.hbmColor != IntPtr.Zero)
            {
                var c = ReadBgra(ii.hbmColor, out int cw, out int ch);
                s += $"; colour {cw}x{ch}";
                if (c != null)
                {
                    int maxA = 0, nonBlack = 0;
                    for (int i = 0; i < c.Length; i += 4)
                    {
                        if (c[i + 3] > maxA) maxA = c[i + 3];
                        if (c[i] != 0 || c[i + 1] != 0 || c[i + 2] != 0) nonBlack++;
                    }
                    s += $" (max alpha {maxA}, {nonBlack * 100 / Math.Max(1, cw * ch)}% non-black)";
                }
            }
            else s += "; no colour bitmap (monochrome)";
            return s;
        }
        catch (Exception ex) { return "threw: " + ex.Message; }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
    }

    private static Bitmap FromColour(byte[] colour, int w, int h, byte[] mask, int mw, int mh)
    {
        var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        bool anyAlpha = false;
        for (int i = 3; i < colour.Length && !anyAlpha; i += 4) if (colour[i] != 0) anyAlpha = true;

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            byte b = colour[o], g = colour[o + 1], r = colour[o + 2];
            int a;
            if (anyAlpha) a = colour[o + 3];
            else
            {
                // No alpha, so the mask decides. AND 0 means "paint this pixel"; AND 1 means
                // "XOR it onto whatever is behind" -- and the second is not a corner case, it is
                // the ordinary I-beam. Its mask is 100% white and its colour bitmap holds the
                // shape, which is how a text cursor stays visible on any background.
                //
                // Treating AND 1 as plain transparency rendered the I-beam completely empty, and
                // `codecbench --cursor` is what caught it. An XOR cannot be expressed in a bitmap
                // with an alpha channel, so it is approximated by INVERTING the colour: the
                // I-beam's white becomes black, which is right over the documents and text fields
                // where an I-beam actually lives. Over a black background it will be wrong, and
                // that is the honest cost of drawing the pointer on the console instead of
                // compositing it on the host.
                int mo = (Math.Min(y, mh - 1) * mw + Math.Min(x, mw - 1)) * 4;
                bool and = mask[mo + 2] > 127;
                if (!and) a = 255;
                else if (r != 0 || g != 0 || b != 0) { a = 255; r = (byte)(255 - r); g = (byte)(255 - g); b = (byte)(255 - b); }
                else a = 0;
            }
            outBmp.SetPixel(x, y, Color.FromArgb(a, r, g, b));
        }
        return outBmp;
    }

    /// <summary>
    /// The ordinary arrow. The mask is 2H tall: AND on top, XOR below.
    ///
    ///   AND 1, XOR 0 -> transparent          AND 0, XOR 0 -> black
    ///   AND 0, XOR 1 -> white                AND 1, XOR 1 -> invert the screen
    ///
    /// The last case is what gives the text cursor its "always visible on any background" trick,
    /// and it cannot be expressed in a bitmap with an alpha channel because it depends on what is
    /// behind it. It is rendered as opaque BLACK — the inverse of the white it would be XORed
    /// against — which is right over the light documents and text fields where an inverting
    /// pointer actually lives. **Over a black background it will be wrong.** That is the honest
    /// cost of drawing the pointer on the console rather than compositing it on the host, and
    /// compositing it on the host would lose the ability to move it between frames, which is most
    /// of the point of stage 2.4.
    /// </summary>
    private static Bitmap FromMonochrome(byte[] mask, int mw, int mh)
    {
        int w = mw, h = mh / 2;
        if (h <= 0) h = mh;
        var outBmp = new Bitmap(w, Math.Max(1, h), PixelFormat.Format32bppArgb);
        bool Bit(int x, int y) => y < mh && mask[(y * mw + x) * 4 + 2] > 127;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool and = Bit(x, y);
            bool xor = Bit(x, y + h);
            Color c =
                !and && !xor ? Color.FromArgb(255, 0, 0, 0) :
                !and && xor ? Color.FromArgb(255, 255, 255, 255) :
                and && !xor ? Color.FromArgb(0, 0, 0, 0) :
                              Color.FromArgb(255, 0, 0, 0);   // invert, approximated for a light background
            outBmp.SetPixel(x, y, c);
        }
        return outBmp;
    }

    private const int CURSOR_SHOWING = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int c, ref BITMAP pv);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr hbm, uint start, uint lines, IntPtr bits, ref BITMAPINFOHEADER bi, uint usage);
}
