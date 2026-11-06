using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using RemoteSupport.Shared;

namespace RemoteSupport.UI;

/// <summary>
/// Builds a window/tray icon from the tenant's logo, so customers see the branding they were sold
/// rather than a generic Windows icon. Tenant branding ships a logo image (jpg/png), not an .ico,
/// so the icon is rendered at run time.
/// </summary>
public static class BrandIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon? _cached;
    private static bool _tried;

    /// <summary>The icon <see cref="Load"/> made, or null — for a window that must set it itself.</summary>
    public static Icon? Current => _cached;

    /// <summary>
    /// The tenant icon, or null if there is no usable logo. Cached: an icon built from
    /// <c>GetHicon</c> owns an unmanaged handle, so exactly one is made per process.
    /// </summary>
    public static Icon? Load(Branding brand)
    {
        if (_tried) return _cached;
        _tried = true;

        // A hand-made app.ico next to the branding wins — it will have proper multi-resolution
        // frames, which a rendered one cannot.
        try
        {
            var ico = Path.Combine(brand.BaseDirectory, "branding", "app.ico");
            if (File.Exists(ico)) { _cached = new Icon(ico); return _cached; }
        }
        catch { }

        try
        {
            if (!brand.HasLogo) return null;
            using var src = Image.FromFile(brand.LogoPath);
            _cached = Render(src, 32);
        }
        catch { _cached = null; }
        return _cached;
    }

    /// <summary>
    /// Makes the tenant's icon the one EVERY window in this process starts with — title bar,
    /// taskbar button and Alt+Tab — not just the windows that remember to set <c>Icon</c>.
    ///
    /// The console set it on its main window only, so the sign-in window, every session window,
    /// Tickets, chat and each tool window showed WinForms' own default icon (2026-10-07, Clint:
    /// "it shows like a normal windows icon"). Twenty-odd forms, plus the ones built inline, and
    /// the next one would forget too; so the default itself is replaced, once, before the first
    /// window. WinForms has no public setter for it — the static field behind
    /// <c>Form.DefaultIcon</c> is set by reflection, and if a future .NET renames it this says so
    /// (returns false, traced by the caller) and every window simply keeps the default, as before.
    /// <c>tools/iconcheck</c> asks a fresh window what icon it actually carries.
    /// </summary>
    /// <remarks>
    /// <b>Not a fixed dialog.</b> WinForms deliberately gives a <c>FixedDialog</c> NO icon unless one
    /// is set on it, default or not — and one that shows in the taskbar (the sign-in window, the
    /// reason and password window) then has a generic button. Those set <c>Icon = BrandIcon.Current</c>
    /// themselves.
    /// </remarks>
    /// <returns>True when a window made from now on carries the tenant's icon.</returns>
    public static bool UseForEveryWindow(Branding brand)
    {
        var icon = Load(brand);
        if (icon == null) return false;
        try
        {
            var field = typeof(Form).GetField("s_defaultIcon", BindingFlags.NonPublic | BindingFlags.Static)
                     ?? typeof(Form).GetField("defaultIcon", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null || field.FieldType != typeof(Icon)) return false;
            // A COPY: WinForms treats setting a window's Icon to the default object itself as "reset
            // to the default", so a fixed dialog given BrandIcon.Current would lose it again.
            field.SetValue(null, (Icon)icon.Clone());
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Writes the tenant's logo out as a real multi-size <c>.ico</c> FILE.
    ///
    /// <see cref="Load"/> is not enough for everything that needs branding. A desktop or Start
    /// menu shortcut carries an <c>IconLocation</c>, which is a path on disk — the shell reads it
    /// when it draws the icon, long after this process has gone, so an in-memory
    /// <see cref="Icon"/> cannot serve it. The same is true of anything else Windows draws on our
    /// behalf rather than asking us for.
    ///
    /// It cannot be an <c>ApplicationIcon</c> compiled into the exe either: this is a multi-tenant
    /// product, every reseller downloads the SAME binaries with their own <c>logo.jpg</c> beside
    /// them, and an icon baked in at build time would show Anywhere IT's logo on every reseller's
    /// customers' desktops. Rendering per install is the whole point.
    ///
    /// Several sizes, because Explorer picks a frame per view — 16 px in a list, 48 px on the
    /// desktop, 256 px in Extra Large Icons — and a single-frame icon gets scaled by the shell to
    /// whatever it wanted, which looks exactly like the blurry mess it is.
    /// </summary>
    /// <returns>False when there is no usable logo, having written nothing.</returns>
    public static bool WriteIcoFile(Branding brand, string path)
    {
        // A hand-made app.ico wins here for the same reason it wins in Load: real frames drawn at
        // each size beat anything resampled from one image.
        try
        {
            var supplied = Path.Combine(brand.BaseDirectory, "branding", "app.ico");
            if (File.Exists(supplied)) { File.Copy(supplied, path, true); return true; }
        }
        catch { }

        if (!brand.HasLogo) return false;

        try
        {
            using var src = Image.FromFile(brand.LogoPath);
            // Every frame is an uncompressed DIB, including the 256, where the convention is to
            // PNG-compress it. One code path rather than two, and the widest set of readers.
            //
            // The saving a PNG frame would make is about 250 KB, and it buys nothing here: this
            // file is rendered on the customer's machine at install time from a logo that is
            // already in the download, so it is never transferred and nobody waits for it.
            //
            // Do not read the choice as "PNG frames do not work" — they do, and the shell reads
            // them. `System.Drawing` will not return a 256 px frame in EITHER form, handing back
            // the next size down in silence, which is worth knowing before concluding that a 256
            // frame is missing. `tools/iconcheck` reads these with PrivateExtractIcons, the API
            // the shell itself uses, for exactly that reason.
            var frames = new List<(int size, byte[] data)>();
            foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256 })
            {
                using var bmp = RenderSquare(src, size);
                frames.Add((size, DibFrame(bmp)));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            w.Write((ushort)0);                  // reserved
            w.Write((ushort)1);                  // type 1 = icon
            w.Write((ushort)frames.Count);

            // Every entry is 16 bytes, so the first image starts after the directory.
            int offset = 6 + frames.Count * 16;
            foreach (var (size, data) in frames)
            {
                w.Write((byte)(size >= 256 ? 0 : size));   // 0 MEANS 256 in this field
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)0);                // colours in palette: 0 for true colour
                w.Write((byte)0);                // reserved
                w.Write((ushort)1);              // planes
                w.Write((ushort)32);             // bits per pixel
                w.Write(data.Length);
                w.Write(offset);
                offset += data.Length;
            }
            foreach (var (_, data) in frames) w.Write(data);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Fits the logo into a square of <paramref name="size"/> px, centred, transparent around it.</summary>
    private static Bitmap RenderSquare(Image src, int size)
    {
        var square = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(square);
        g.Clear(Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;

        double scale = Math.Min((double)size / src.Width, (double)size / src.Height);
        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
        g.DrawImage(src, new Rectangle((size - w) / 2, (size - h) / 2, w, h));
        return square;
    }

    private static Icon Render(Image src, int size)
    {
        using var square = RenderSquare(src, size);
        // GetHicon hands back a handle we own; clone into a managed Icon and release it, so the
        // long-lived object does not depend on an unmanaged handle we would have to track.
        IntPtr h32 = square.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h32);
            return (Icon)tmp.Clone();
        }
        finally { DestroyIcon(h32); }
    }

    /// <summary>
    /// One icon frame as an uncompressed 32-bit DIB, the form an .ico has always carried.
    ///
    /// Two things here are easy to get wrong and both fail silently, as a frame the shell simply
    /// declines to draw: the height in the header is <b>doubled</b>, because the structure
    /// describes the colour image and the AND mask stacked; and the mask must be present even
    /// when the alpha channel makes it redundant, with its rows padded to four bytes. A DIB is
    /// also bottom-up, so the rows go out in reverse.
    /// </summary>
    private static byte[] DibFrame(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int maskStride = ((w + 31) / 32) * 4;      // 1 bit per pixel, rows padded to 4 bytes

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40);                 // BITMAPINFOHEADER size
        bw.Write(w);
        bw.Write(h * 2);              // colour image AND mask
        bw.Write((ushort)1);          // planes
        bw.Write((ushort)32);         // bits per pixel
        bw.Write(0);                  // BI_RGB, uncompressed
        bw.Write(w * h * 4 + maskStride * h);
        bw.Write(0); bw.Write(0);     // pixels per metre, both unused
        bw.Write(0); bw.Write(0);     // palette entries, both unused

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--)     // bottom-up
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                bw.Write(row);
            }
        }
        finally { bmp.UnlockBits(data); }

        bw.Write(new byte[maskStride * h]);      // all zero: "opaque", alpha decides the rest
        bw.Flush();
        return ms.ToArray();
    }
}
