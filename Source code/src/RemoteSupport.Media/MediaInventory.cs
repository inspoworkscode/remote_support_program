namespace RemoteSupport.Media;

/// <summary>
/// What this machine actually has, in Media Foundation's own words.
///
/// It exists because "no encoder was found" has two completely different causes that look
/// identical from the outside: the machine really has none, or we asked the question wrongly.
/// The second is a bug in <see cref="Mf"/> and the first is a supported state that falls back to
/// JPEG tiles, and no amount of staring at the calling code tells them apart. Listing everything
/// unfiltered does, immediately.
/// </summary>
public static class MediaInventory
{
    public sealed record Line(string Category, string Filter, string Name);

    public static List<Line> Take()
    {
        var lines = new List<Line>();
        try { Mf.Startup(); }
        catch (Exception ex) { lines.Add(new Line("platform", "MFStartup", ex.Message)); return lines; }

        const uint all = Mf.MFT_ENUM_FLAG_SYNCMFT | Mf.MFT_ENUM_FLAG_ASYNCMFT | Mf.MFT_ENUM_FLAG_HARDWARE
                       | Mf.MFT_ENUM_FLAG_LOCALMFT | Mf.MFT_ENUM_FLAG_FIELDOFUSE | Mf.MFT_ENUM_FLAG_SORTANDFILTER;

        // The encoder category is swept flag by flag, and the others are not, because this is the
        // one that came back empty on the machine this was written on. "There is no encoder" and
        // "the flags excluded it" are the same empty list, and the sweep is the only thing that
        // separates them without a second machine.
        Add(lines, "video encoder", "everything", Mf.MFT_CATEGORY_VIDEO_ENCODER, all);
        Add(lines, "video encoder", "hardware only", Mf.MFT_CATEGORY_VIDEO_ENCODER, Mf.MFT_ENUM_FLAG_HARDWARE);
        Add(lines, "video encoder", "synchronous only", Mf.MFT_CATEGORY_VIDEO_ENCODER, Mf.MFT_ENUM_FLAG_SYNCMFT);
        Add(lines, "video encoder", "asynchronous only", Mf.MFT_CATEGORY_VIDEO_ENCODER, Mf.MFT_ENUM_FLAG_ASYNCMFT);
        Add(lines, "video encoder", "no flags at all", Mf.MFT_CATEGORY_VIDEO_ENCODER, 0);
        Add(lines, "video decoder", "everything", Mf.MFT_CATEGORY_VIDEO_DECODER, all);
        Add(lines, "video processor", "everything", Mf.MFT_CATEGORY_VIDEO_PROCESSOR, all);

        Filtered(lines, "video encoder", "NV12 in, H.264 out", Mf.MFT_CATEGORY_VIDEO_ENCODER, all,
                 Mf.MFVideoFormat_NV12, Mf.MFVideoFormat_H264);
        Filtered(lines, "video encoder", "BGRA in, H.264 out", Mf.MFT_CATEGORY_VIDEO_ENCODER, all,
                 Mf.MFVideoFormat_ARGB32, Mf.MFVideoFormat_H264);
        Filtered(lines, "video decoder", "H.264 in, NV12 out", Mf.MFT_CATEGORY_VIDEO_DECODER, all,
                 Mf.MFVideoFormat_H264, Mf.MFVideoFormat_NV12);
        Filtered(lines, "video processor", "BGRA in, NV12 out", Mf.MFT_CATEGORY_VIDEO_PROCESSOR, all,
                 Mf.MFVideoFormat_ARGB32, Mf.MFVideoFormat_NV12);
        return lines;
    }

    private static void Add(List<Line> lines, string category, string filter, Guid cat, uint flags)
    {
        var names = Mf.Describe(cat, flags);
        if (names.Count == 0) { lines.Add(new Line(category, filter, "(none)")); return; }
        foreach (var n in names) lines.Add(new Line(category, filter, n));
    }

    private static void Filtered(List<Line> lines, string category, string filter, Guid cat, uint flags, Guid inSub, Guid outSub)
    {
        var found = Mf.Enumerate(cat, flags, Mf.MFMediaType_Video, inSub, Mf.MFMediaType_Video, outSub);
        if (Mf.LastEnumResult != Mf.S_OK) { lines.Add(new Line(category, filter, $"(the enumeration failed: {Mf.Explain(Mf.LastEnumResult)})")); return; }
        if (found.Count == 0) { lines.Add(new Line(category, filter, "(none)")); return; }
        foreach (var a in found) { lines.Add(new Line(category, filter, Mf.FriendlyName(a))); Mf.Release(a); }
    }
}
