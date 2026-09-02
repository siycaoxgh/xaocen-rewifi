using System.Drawing;
using QRCoder;

namespace XAOCEN.ReWiFi;

internal static class QrCodeService
{
    public static Bitmap CreateBitmap(string payload, int pixelsPerModule = 8)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException("二维码内容不能为空。", nameof(payload));
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(data);
        var png = qrCode.GetGraphic(pixelsPerModule);
        using var stream = new MemoryStream(png);
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }
}
