using QRCoder;

namespace Photobooth.Desktop.Platform;

/// <summary>
/// Tạo ảnh QR (PNG) từ URL chia sẻ — hoàn toàn offline. QR chỉ chứa URL, không chứa ảnh hay dữ liệu Base64 của ảnh.
/// Dùng QRCoder (MIT): chỉ phần PNG thuần .NET, không phụ thuộc System.Drawing.
/// </summary>
public sealed class QrCodeRenderer
{
    private const int PixelsPerModule = 10;

    /// <summary>PNG đen trên nền trắng, có vùng lề (quiet zone) để điện thoại quét dễ; mức sửa lỗi M.</summary>
    public byte[] RenderPng(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(PixelsPerModule, [0, 0, 0], [255, 255, 255], drawQuietZones: true);
    }

    /// <summary>Data URI cho thẻ &lt;img&gt; (ảnh QR nhỏ vài KB, tạo một lần cho mỗi link).</summary>
    public string RenderDataUri(string text) => "data:image/png;base64," + Convert.ToBase64String(RenderPng(text));
}
