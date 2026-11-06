using System.Drawing;
using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

internal static class BrandUtil
{
    public static Color Parse(string hex, Color fallback)
    { try { return ColorTranslator.FromHtml(hex); } catch { return fallback; } }

    public static Image? TryLoadLogo(Branding b)
    { try { if (b.HasLogo) return Image.FromFile(b.LogoPath); } catch { } return null; }
}
