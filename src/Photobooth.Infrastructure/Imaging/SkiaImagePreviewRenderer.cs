using Photobooth.Application.Composition;
using Photobooth.Infrastructure.Storage;
using SkiaSharp;

namespace Photobooth.Infrastructure.Imaging;

/// <summary>
/// Tạo ảnh xem trước JPEG thu nhỏ cho giao diện. Đây là nơi DUY NHẤT (cùng với Photo Composer) áp dụng
/// lật ngang cho ảnh tĩnh, nên ảnh xem lại và ảnh thành phẩm luôn cùng hướng với live preview.
/// </summary>
public sealed class SkiaImagePreviewRenderer : IImagePreviewRenderer
{
    private const int PreviewJpegQuality = 85;

    private readonly PhotoboothStoragePaths _paths;

    public SkiaImagePreviewRenderer(PhotoboothStoragePaths paths)
    {
        _paths = paths;
    }

    public Task<byte[]> RenderPreviewAsync(string imageFilePath, bool mirror, int maxWidthPixels, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidthPixels);
        if (!_paths.IsInsideRoot(imageFilePath))
        {
            throw new UnauthorizedAccessException("Chỉ được đọc ảnh nằm trong thư mục dữ liệu của ứng dụng.");
        }

        return Task.Run(
            () =>
            {
                using var bitmap = SKBitmap.Decode(imageFilePath)
                    ?? throw new IOException("Không đọc được ảnh để hiển thị.");
                using var image = SKImage.FromBitmap(bitmap);

                var scale = Math.Min(1f, maxWidthPixels / (float)image.Width);
                var width = Math.Max(1, (int)Math.Round(image.Width * scale));
                var height = Math.Max(1, (int)Math.Round(image.Height * scale));

                using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                    ?? throw new IOException("Không tạo được vùng vẽ.");
                var canvas = surface.Canvas;
                if (mirror)
                {
                    canvas.Scale(-1, 1, width / 2f, 0);
                }

                canvas.DrawImage(image, SKRect.Create(width, height), TemplateRenderer.Sampling);

                using var snapshot = surface.Snapshot();
                using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, PreviewJpegQuality);
                return data.ToArray();
            },
            cancellationToken);
    }
}
