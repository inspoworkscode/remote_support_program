namespace RemoteSupport.Media;

/// <summary>
/// NV12 to BGRA, on the CPU, because the console draws with GDI+ and GDI+ wants BGRA.
///
/// This is the one place in the whole video path where a frame is touched a pixel at a time, and
/// it is on the technician's machine rather than the customer's -- which is the right side of the
/// link for it to be on. The customer's machine is the one that might be a ten-year-old laptop on
/// battery; the technician's is sitting on a desk doing nothing else.
///
/// THE COEFFICIENTS MATTER AND ARE NOT OBVIOUS. YUV is not one thing: BT.601 and BT.709 disagree
/// about how much green is in luma, and decoding one as the other does not break the picture, it
/// tints it -- skin goes waxy, blues go purple. Nobody reports that as a bug; they report that our
/// colours "look a bit off" against AnyDesk, which is unfalsifiable and unfixable a year later.
/// So both matrices are here, the choice travels with the frame size the way Media Foundation's
/// own default does, and `tools/codeccheck` measures the round-trip colour error rather than
/// leaving it to the eye.
/// </summary>
public static class Nv12
{
    /// <summary>
    /// Which YUV matrix a decoded frame of this size should be read with.
    ///
    /// Media Foundation leaves the matrix unspecified on most desktop streams, and the convention
    /// everything downstream then applies -- including Windows' own renderer -- is BT.709 for high
    /// definition and BT.601 below it. Following the same convention is what makes our picture
    /// match what the same file looks like in a media player.
    /// </summary>
    public static bool Is709(int width, int height) => height >= 720 || width >= 1280;

    /// <summary>
    /// Converts an NV12 plane pair into BGRA.
    ///
    /// Chroma is half resolution in both directions, so four pixels share one U/V pair. The loop
    /// does two pixels at a time along a row for that reason: it is not an optimisation, it is the
    /// shape of the format, and writing it a pixel at a time means re-reading and re-computing the
    /// same chroma four times.
    /// </summary>
    public static unsafe void ToBgra(byte* y, int yStride, byte* uv, int uvStride,
                                     byte* dst, int dstStride, int width, int height, bool bt709)
    {
        // Fixed point, 16 bits of fraction. Limited range in ("studio swing", 16..235 luma) because
        // that is what every hardware encoder on this path produces unless told otherwise.
        //   601:  R = 1.164(Y-16) + 1.596(V-128)
        //         G = 1.164(Y-16) - 0.392(U-128) - 0.813(V-128)
        //         B = 1.164(Y-16) + 2.017(U-128)
        //   709:  R = 1.164(Y-16) + 1.793(V-128)
        //         G = 1.164(Y-16) - 0.213(U-128) - 0.533(V-128)
        //         B = 1.164(Y-16) + 2.112(U-128)
        const int Yc = 76309;                       // 1.164 << 16
        int Rv = bt709 ? 117489 : 104597;
        int Gu = bt709 ? -13975 : -25675;
        int Gv = bt709 ? -34925 : -53279;
        int Bu = bt709 ? 138438 : 132201;

        for (int row = 0; row < height; row++)
        {
            byte* ys = y + (long)row * yStride;
            byte* cs = uv + (long)(row >> 1) * uvStride;
            byte* d = dst + (long)row * dstStride;

            int col = 0;
            for (; col + 1 < width; col += 2)
            {
                int u = cs[col] - 128, v = cs[col + 1] - 128;
                int ru = Rv * v, gu = Gu * u + Gv * v, bu = Bu * u;

                int c = Yc * (ys[col] - 16);
                d[0] = Clamp(c + bu); d[1] = Clamp(c + gu); d[2] = Clamp(c + ru); d[3] = 255;
                c = Yc * (ys[col + 1] - 16);
                d[4] = Clamp(c + bu); d[5] = Clamp(c + gu); d[6] = Clamp(c + ru); d[7] = 255;
                d += 8;
            }
            // An odd width has one pixel with no partner. It cannot happen on the encoder's own
            // output -- H.264 dimensions are even -- but it can on a caller-supplied crop, and a
            // half-written last column is a thin bright line down the right of the screen.
            if (col < width)
            {
                int u = cs[col] - 128;
                int v = (col + 1 < uvStride ? cs[col + 1] : (byte)128) - 128;
                int c = Yc * (ys[col] - 16);
                d[0] = Clamp(c + Bu * u); d[1] = Clamp(c + Gu * u + Gv * v); d[2] = Clamp(c + Rv * v); d[3] = 255;
            }
        }
    }

    private static byte Clamp(int fixed16)
    {
        int v = (fixed16 + 32768) >> 16;
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }
}
