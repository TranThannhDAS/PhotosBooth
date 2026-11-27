using Photobooth.Application.Printing;
using Photobooth.Infrastructure.Storage;
using QRCoder;
using SkiaSharp;

namespace Photobooth.Infrastructure.Imaging;

/// <summary>
/// Tạo bản in kèm mã QR: đọc ảnh thành phẩm (chỉ đọc), vẽ QR của link chia sẻ vào vùng QR của template rồi ghi ra file bản in riêng.
/// QR vẽ bằng ô vuông nguyên pixel (sắc nét khi in), nền trắng có lề (quiet zone), mức sửa lỗi Q để chịu được trầy xước/mực in.
/// </summary>
public sealed class SkiaPrintImageRenderer : IPrintImageRenderer
{
    public Task RenderWithQrAsync(string sourceImagePath, string destinationPath, PrintQrPlacement placement, string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (File.Exists(destinationPath))
        {
            // Bản in của một phiên bản luôn giống nhau (link chia sẻ không bao giờ đổi): dùng lại.
            return Task.CompletedTask;
        }

        return RenderCoreAsync(sourceImagePath, destinationPath, placement, url, cancellationToken);
    }

    private static async Task RenderCoreAsync(string sourceImagePath, string destinationPath, PrintQrPlacement placement, string url, CancellationToken cancellationToken)
    {
        var png = await Task.Run(() => Render(sourceImagePath, placement, url, cancellationToken), cancellationToken).ConfigureAwait(false);
        try
        {
            await SafeFileWriter.WriteAsync(destinationPath, (stream, token) => stream.WriteAsync(png, token).AsTask(), overwrite: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            // Một yêu cầu khác vừa ghi xong cùng bản in (nội dung như nhau).
        }
    }

    internal static byte[] Render(string sourceImagePath, PrintQrPlacement placement, string url, CancellationToken cancellationToken)
    {
        using var source = SKBitmap.Decode(sourceImagePath)
            ?? throw new IOException("Không đọc được ảnh thành phẩm để tạo bản in.");
        cancellationToken.ThrowIfCancellationRequested();

        using var surface = SKSurface.Create(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new IOException("Không tạo được vùng vẽ cho bản in.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, 0, 0);

        // Ảnh thành phẩm có cùng kích thước canvas template; nếu khác (dữ liệu cũ) thì quy đổi theo tỉ lệ.
        var scaleX = source.Width / (double)Math.Max(1, placement.CanvasWidthPixels);
        var scaleY = source.Height / (double)Math.Max(1, placement.CanvasHeightPixels);
        var left = (int)Math.Round(placement.X * scaleX);
        var top = (int)Math.Round(placement.Y * scaleY);
        var size = (int)Math.Floor(placement.Size * Math.Min(scaleX, scaleY));
        DrawQr(canvas, url, left, top, size);

        cancellationToken.ThrowIfCancellationRequested();
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new IOException("Không mã hóa được bản in.");
        return data.ToArray();
    }

    /// <summary>Vẽ QR (kèm lề trắng 4 ô) vừa khít hình vuông <paramref name="size"/>, căn giữa, mỗi ô là số nguyên pixel.</summary>
    internal static void DrawQr(SKCanvas canvas, string url, int left, int top, int size)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var matrix = data.ModuleMatrix; // Đã gồm quiet zone 4 ô mỗi phía.
        var modules = matrix.Count;
        if (size < modules)
        {
            throw new IOException("Vùng QR quá nhỏ để vẽ mã QR.");
        }

        using var white = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = false };
        using var black = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false };
        canvas.DrawRect(SKRect.Create(left, top, size, size), white);

        var cell = size / modules;
        var offsetX = left + (size - cell * modules) / 2;
        var offsetY = top + (size - cell * modules) / 2;
        for (var row = 0; row < modules; row++)
        {
            var bits = matrix[row];
            for (var col = 0; col < modules; col++)
            {
                if (bits[col])
                {
                    canvas.DrawRect(SKRect.Create(offsetX + col * cell, offsetY + row * cell, cell, cell), black);
                }
            }
        }
    }
}
