using System.Drawing;
using RemoteSupport.Shared;

namespace RemoteSupport.Client;

internal static class BrandUtil
{
    public static Color Parse(string hex, Color fallback)
    {
        try { return ColorTranslator.FromHtml(hex); } catch { return fallback; }
    }

    public static Image? TryLoadLogo(Branding b)
    {
        try { if (b.HasLogo) return Image.FromFile(b.LogoPath); } catch { }
        return null;
    }

    /// <summary>brand.json stores an http(s) or ws(s) server URL; normalise to a ws(s) base.</summary>
    public static string WsBase(string serverUrl)
    {
        var u = serverUrl.Trim().TrimEnd('/');
        if (u.StartsWith("http://")) return "ws://" + u["http://".Length..];
        if (u.StartsWith("https://")) return "wss://" + u["https://".Length..];
        if (u.StartsWith("ws://") || u.StartsWith("wss://")) return u;
        return "wss://" + u;
    }
}
