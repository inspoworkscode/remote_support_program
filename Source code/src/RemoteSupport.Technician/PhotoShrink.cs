using System.Drawing.Imaging;

namespace RemoteSupport.Technician;

/// <summary>
/// Makes a workshop photo smaller before it is uploaded (Clint, 2026-09-29: "can the uploaded
/// photos be automatically resized so they don't use up so much space?"). The same rule as the
/// portal's <c>wwwroot/photos.js</c>, so both ways in store the same size: the longest side becomes
/// 2048 pixels, as a JPEG at 85% — a 4 MB phone photo comes out around half a megabyte, and a
/// scratch or a crack is still plain to see.
///
/// <para>Here rather than on the server, because the server is headed for Linux, where .NET's own
/// image code does not run. A photo already small enough is sent as it is, and so is anything
/// Windows cannot open (a HEIC with no codec installed) — the server's 12 MB limit still applies.
/// A phone's sideways photo is turned upright first: re-encoding drops the tag that told viewers
/// which way up it was.</para>
/// </summary>
public static class PhotoShrink
{
    public const int MaxSide = 2048;
    public const long Quality = 85;
    public const long SmallEnough = 1536 * 1024;

    /// <summary>The bytes to upload and the name to give them.</summary>
    public static (byte[] bytes, string name) Prepare(string path)
    {
        var original = File.ReadAllBytes(path);
        var name = Path.GetFileName(path);
        try
        {
            using var ms = new MemoryStream(original);
            using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            int longest = Math.Max(img.Width, img.Height);
            if (longest <= MaxSide && original.Length <= SmallEnough) return (original, name);

            Upright(img);
            double scale = Math.Min(1.0, (double)MaxSide / Math.Max(img.Width, img.Height));
            int w = Math.Max(1, (int)Math.Round(img.Width * scale)), h = Math.Max(1, (int)Math.Round(img.Height * scale));
            using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.Clear(Color.White);   // a PNG's transparency becomes white, not black
                g.DrawImage(img, 0, 0, w, h);
            }
            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            using var ep = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, Quality) } };
            using var outMs = new MemoryStream();
            small.Save(outMs, jpeg, ep);
            var shrunk = outMs.ToArray();
            return shrunk.Length < original.Length ? (shrunk, Path.GetFileNameWithoutExtension(path) + ".jpg") : (original, name);
        }
        catch { return (original, name); }
    }

    /// <summary>Applies the EXIF orientation tag (0x0112) to the pixels, then removes it.</summary>
    private static void Upright(Image img)
    {
        const int OrientationId = 0x0112;
        if (!img.PropertyIdList.Contains(OrientationId)) return;
        var flip = BitConverter.ToUInt16(img.GetPropertyItem(OrientationId)!.Value!, 0) switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };
        if (flip != RotateFlipType.RotateNoneFlipNone) img.RotateFlip(flip);
        img.RemovePropertyItem(OrientationId);
    }
}
