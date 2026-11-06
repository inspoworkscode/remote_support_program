using System.Drawing;
using System.Runtime.InteropServices;
using RemoteSupport.Shared;
using RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// Does the customer's desktop shortcut carry the logo?
//
// It did not, on every machine installed so far, and the reason is worth keeping: a .lnk draws
// its icon from an IconLocation PATH that the shell reads long after our process has gone, so the
// tray app's rendered icon could never have served it — and with no IconLocation set Windows
// falls back to the icon embedded in the exe, which is the generic .NET one because there is no
// ApplicationIcon. There cannot be one: every reseller downloads the same binaries with their own
// logo.jpg beside them, so a compiled-in icon would put Anywhere IT's logo on somebody else's
// customers' desktops.
//
// This proves the fix without installing anything. Two halves, and the second is the one that
// matters:
//
//   1. The .ico we write is a real icon file — the frames Explorer asks for at each view size
//      actually exist and load at that size. A single-frame icon looks fine in a screenshot and
//      blurry on the desktop, which is the failure nobody files a bug about.
//   2. A shortcut built THE WAY THE INSTALLER BUILDS IT, through the same WScript.Shell COM
//      object, is read back from disk and asked what icon it carries. Asserting that we set a
//      property on an object we still hold would prove nothing: the question is what survives
//      into the .lnk file.
//
//   dotnet run -c Release --project tools/iconcheck/iconcheck.csproj -- [--out DIR]
// ---------------------------------------------------------------------------------------------

internal static class IconCheck
{
    private static int _pass, _fail;

    /// <summary>The API the shell itself uses to pull a sized icon out of a file.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIcons(string file, int index, int cx, int cy,
                                                  IntPtr[] icons, int[] ids, int count, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var i = Array.IndexOf(args, "--out");
        var outDir = i >= 0 && i + 1 < args.Length
            ? args[i + 1]
            : Path.Combine(Path.GetTempPath(), "rs-iconcheck");
        Directory.CreateDirectory(outDir);

        var brand = Branding.Load();
        Console.WriteLine();
        Console.WriteLine($"  branding: {brand.CompanyName}, logo {(brand.HasLogo ? brand.LogoPath : "(none)")}");
        Console.WriteLine();
        Console.WriteLine("== CHECK 1: the .ico file itself ==");

        if (!brand.HasLogo)
        {
            // Not a pass and not a crash: without a logo there is nothing to render, and saying
            // so beats reporting a failure in code that was never reached.
            Check("a logo to render", false, "no logo beside this harness — run it from the repo root");
            Console.WriteLine($"\n  {_pass} passed, {_fail} failed");
            return 1;
        }

        var ico = Path.Combine(outDir, "app-shortcut.ico");
        var wrote = BrandIcon.WriteIcoFile(brand, ico);
        Check("an .ico is written from the tenant's logo", wrote && File.Exists(ico), ico);
        if (!wrote) { Console.WriteLine($"\n  {_pass} passed, {_fail} failed"); return 1; }

        var bytes = File.ReadAllBytes(ico);
        Check("it is big enough to hold real frames", bytes.Length > 2000, $"{bytes.Length:N0} bytes");

        // The directory header, read the way the shell reads it rather than the way we wrote it.
        ushort reserved = BitConverter.ToUInt16(bytes, 0);
        ushort type = BitConverter.ToUInt16(bytes, 2);
        ushort count = BitConverter.ToUInt16(bytes, 4);
        Check("the header says it is an icon", reserved == 0 && type == 1, $"reserved={reserved} type={type}");
        Check("it carries several sizes, not one", count >= 5, $"{count} frames");

        var sizes = new List<int>();
        bool offsetsSane = true;
        for (int f = 0; f < count; f++)
        {
            int e = 6 + f * 16;
            int w = bytes[e] == 0 ? 256 : bytes[e];
            int len = BitConverter.ToInt32(bytes, e + 8);
            int off = BitConverter.ToInt32(bytes, e + 12);
            sizes.Add(w);
            // A frame whose offset and length run past the end of the file is the classic way a
            // hand-written .ico fails: the file looks present, and the shell draws nothing.
            if (off < 6 + count * 16 || len <= 0 || off + len > bytes.Length) offsetsSane = false;
        }
        Check("every frame lies inside the file", offsetsSane, string.Join(", ", sizes.Select(s => s + "px")));
        Check("the 256 px frame is there, marked the way the format requires (0)",
              sizes.Contains(256) && bytes.Skip(6).Where((_, n) => n % 16 == 0).Any(b => b == 0));
        Check("and the small sizes Explorer uses in a list", sizes.Contains(16) && sizes.Contains(32));

        // The real question: can a reader PULL each size out? A frame that is present in the
        // directory but malformed inside fails here rather than on a customer's desktop.
        //
        // Read with PrivateExtractIcons, which is what the SHELL uses, and not with
        // System.Drawing. That distinction cost a wrong conclusion the first time this ran:
        // `new Icon(path, new Size(256, 256))` came back 128x128, which reads exactly like a
        // missing or broken 256 frame. It is neither — System.Drawing simply will not return a
        // 256 px frame, whether it is stored as a PNG or as a DIB, and it gives you the next size
        // down in silence rather than saying so. Asking the wrong reader produced a fault the
        // file did not have, which is the one thing a diagnostic must never do (§18, §19).
        foreach (var want in new[] { 16, 32, 48, 64, 128, 256 })
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                var handles = new IntPtr[1];
                var ids = new int[1];
                int n = PrivateExtractIcons(ico, 0, want, want, handles, ids, 1, 0);
                h = handles[0];
                if (n < 1 || h == IntPtr.Zero) { Check($"the shell can pull a {want} px frame", false, "nothing extracted"); continue; }
                using var got = Icon.FromHandle(h);
                Check($"the shell can pull a {want} px frame", got.Width == want && got.Height == want,
                      $"{got.Width}x{got.Height}");
            }
            catch (Exception ex) { Check($"the shell can pull a {want} px frame", false, ex.Message); }
            finally { if (h != IntPtr.Zero) DestroyIcon(h); }
        }

        // Rendered rather than blank. An icon of the right size full of nothing would pass every
        // structural check above and still show an empty square.
        try
        {
            var h48 = new IntPtr[1]; var id48 = new int[1];
            PrivateExtractIcons(ico, 0, 48, 48, h48, id48, 1, 0);
            using var icon48 = Icon.FromHandle(h48[0]);
            using var bmp = icon48.ToBitmap();
            int lit = 0;
            for (int y = 0; y < bmp.Height; y += 2)
                for (int x = 0; x < bmp.Width; x += 2)
                    if (bmp.GetPixel(x, y).A > 16) lit++;
            Check("there is actually a picture in it", lit > 40, $"{lit} of 576 sampled pixels are not transparent");
            bmp.Save(Path.Combine(outDir, "frame-48.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Check("there is actually a picture in it", false, ex.Message); }

        Console.WriteLine();
        Console.WriteLine("== CHECK 2: a shortcut made the way the installer makes one ==");

        // Through WScript.Shell, which is what Install-Client.ps1 uses. A different mechanism
        // here would be testing a different thing.
        var lnkPath = Path.Combine(outDir, "Anywhere IT Remote Support.lnk");
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = Path.Combine(AppContext.BaseDirectory, "iconcheck.exe");
            lnk.IconLocation = ico + ",0";
            lnk.Save();

            Check("the shortcut file was created", File.Exists(lnkPath), lnkPath);

            // Read back from DISK through a second object. Asking the object we just configured
            // what we configured it with proves nothing about the file Explorer will open.
            dynamic shell2 = Activator.CreateInstance(shellType)!;
            dynamic back = shell2.CreateShortcut(lnkPath);
            string carried = back.IconLocation;
            Check("it carries an icon at all", !string.IsNullOrWhiteSpace(carried) && !carried.StartsWith(","),
                  $"IconLocation = '{carried}'");
            Check("and the icon it names is the rendered one",
                  carried.StartsWith(ico, StringComparison.OrdinalIgnoreCase), carried);

            var named = carried.Split(',')[0];
            Check("which exists on disk where the shortcut says it is", File.Exists(named), named);

            // And the shell can load it from there — the last link in the chain, and the one the
            // customer actually sees.
            var hs = new IntPtr[1]; var ids2 = new int[1];
            int got2 = PrivateExtractIcons(named, 0, 48, 48, hs, ids2, 1, 0);
            Check("and the shell can load it from that path", got2 == 1 && hs[0] != IntPtr.Zero);
            if (hs[0] != IntPtr.Zero) DestroyIcon(hs[0]);
            Marshal.ReleaseComObject(back); Marshal.ReleaseComObject(shell2);
            Marshal.ReleaseComObject(lnk); Marshal.ReleaseComObject(shell);
        }
        catch (Exception ex) { Check("a shortcut carrying the icon", false, ex.Message); }

        Console.WriteLine();
        Console.WriteLine("== CHECK 3: every window carries it, not just the one that asked ==");

        // 2026-10-07: the console set the logo on its main window only, so the sign-in window and
        // every session, ticket and tool window showed WinForms' default. A window that never
        // mentions an icon is asked — through WM_GETICON, what the taskbar and Alt+Tab read —
        // before and after BrandIcon.UseForEveryWindow. The "before" proves this can fail.
        var brandIcon = BrandIcon.Load(brand)!;
        var before = WindowCarries(brandIcon, FormBorderStyle.Sizable, null);
        Check("before: a window that sets no icon has WinForms' default", before == false, Said(before));
        var used = BrandIcon.UseForEveryWindow(brand);
        Check("the company's icon is made every window's default", used);
        var after = WindowCarries(brandIcon, FormBorderStyle.Sizable, null);
        Check("after: a window that sets no icon carries the company's", after == true, Said(after));
        // WinForms gives a fixed dialog no icon unless one is set on it — the sign-in window is one,
        // and it shows in the taskbar. This is why those dialogs set BrandIcon.Current themselves.
        var bare = WindowCarries(brandIcon, FormBorderStyle.FixedDialog, null);
        Check("a fixed dialog that sets nothing still has no icon (why they set it)", bare == null, Said(bare));
        var dlg = WindowCarries(brandIcon, FormBorderStyle.FixedDialog, BrandIcon.Current);
        Check("a fixed dialog given BrandIcon.Current carries the company's", dlg == true, Said(dlg));

        Console.WriteLine();
        Console.WriteLine($"  wrote {outDir}");
        Console.WriteLine($"  {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static string Said(bool? r) => r switch { true => "the company's", false => "another icon", null => "no icon" };

    /// <summary>
    /// Makes a window the way the console does and asks it, by WM_GETICON (what the taskbar and
    /// Alt+Tab read), for its big icon: the company's (true), some other (false) or none (null).
    /// Compared as pixels, because WinForms hands the window a copy and handles never match.
    /// </summary>
    private static bool? WindowCarries(Icon brand, FormBorderStyle style, Icon? set)
    {
        const int WM_GETICON = 0x007F, ICON_BIG = 1;
        using var f = new Form { FormBorderStyle = style, ShowInTaskbar = false };
        if (set != null) f.Icon = set;
        var h = SendMessage(f.Handle, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
        if (h == IntPtr.Zero) return null;
        using var got = Icon.FromHandle(h);
        using var a = Draw(got); using var b = Draw(brand);
        long diff = 0;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                diff += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) + Math.Abs(p.A - q.A);
            }
        return diff / (32 * 32) < 12;   // the same picture, give or take resampling
    }

    private static Bitmap Draw(Icon icon)
    {
        var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.DrawIcon(icon, new Rectangle(0, 0, 32, 32));
        return bmp;
    }
}
