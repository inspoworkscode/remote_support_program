namespace RemoteSupport.Tools;

/// <summary>
/// Pictures whole enough for <c>TicketPhotoService.WholeImageType</c> (2026-10-23, the pentest's F7). The harnesses
/// used to feed the photo store a JPEG's first four bytes and some zeros; the server now walks a picture's headers
/// and refuses that, as it should. Nothing here is drawn, so a JPEG is headers only — a frame header with its size
/// and the end — and a PNG is a real one of one colour, as workflowdemo's.
/// </summary>
public static class TestImages
{
    /// <summary>A 1×1 JPEG's headers; <paramref name="padding"/> zero bytes ride in a comment, <paramref name="mark"/> too (so two can differ).</summary>
    public static byte[] Jpeg(int padding = 64, byte[]? mark = null)
    {
        var comment = (mark ?? Array.Empty<byte>()).Concat(new byte[padding]).ToArray();
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 });
        for (int at = 0; at < comment.Length; at += 65533)   // a segment holds at most 65533 bytes
        {
            var part = comment.Skip(at).Take(65533).ToArray();
            var len = part.Length + 2;
            ms.Write(new byte[] { 0xFF, 0xFE, (byte)(len >> 8), (byte)len });
            ms.Write(part);
        }
        ms.Write(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x01, 0x00, 0x01, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xD9 });
        return ms.ToArray();
    }

    /// <summary>A real PNG of one colour.</summary>
    public static byte[] Png(int w = 4, int h = 4, byte r = 200, byte g = 60, byte b = 60)
    {
        var raw = new byte[h * (1 + w * 3)];
        for (int y = 0; y < h; y++)
        {
            int row = y * (1 + w * 3);
            for (int x = 0; x < w; x++) { raw[row + 1 + x * 3] = r; raw[row + 2 + x * 3] = g; raw[row + 3 + x * 3] = b; }
        }
        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true)) zs.Write(raw);
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); ms.Write(len);
            var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            ms.Write(td);
            var crc = BitConverter.GetBytes(Crc(td)); Array.Reverse(crc); ms.Write(crc);
        }
        var ihdr = new byte[13];
        var bw = BitConverter.GetBytes(w); Array.Reverse(bw); bw.CopyTo(ihdr, 0);
        var bh = BitConverter.GetBytes(h); Array.Reverse(bh); bh.CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr); Chunk("IDAT", z.ToArray()); Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static uint Crc(byte[] d)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in d) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        return c ^ 0xFFFFFFFF;
    }
}
