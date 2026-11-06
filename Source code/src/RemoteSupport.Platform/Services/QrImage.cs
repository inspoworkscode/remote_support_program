using QRCoder;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The QR code an authenticator app scans to add an account (2026-10-01). Drawn here, once, as a
/// PNG — the portal shows it as an image, and the console is sent the same bytes inside its
/// sign-in answer, so neither needs a QR encoder of its own and the two can never disagree.
///
/// QRCoder (MIT, no dependencies) is the one library brought in for this: a QR encoder is
/// Reed-Solomon arithmetic with a dozen lookup tables, and a hand-rolled one that is subtly wrong
/// produces a code that some phones read and others do not.
/// </summary>
public static class QrImage
{
    public static byte[] Png(string text, int pixelsPerModule = 6)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    public static string DataUri(string text) => "data:image/png;base64," + Convert.ToBase64String(Png(text));
}
