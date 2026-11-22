using Photobooth.Application.Composition;
using Photobooth.Infrastructure.Storage;
using SkiaSharp;

namespace Photobooth.Infrastructure.Imaging;

/// <summary>
/// Tạo ảnh xem trước JPEG thu nhỏ cho giao diện. Đây là nơi DUY NHẤT (cùng với Photo Composer) áp dụng
/// lật ngang và bộ lọc màu cho ảnh tĩnh, nên ảnh xem lại, thumbnail bộ lọc và ảnh thành phẩm luôn nhất quán.
/// </summary>
public sealed class SkiaImagePreviewRenderer : IImagePreviewRenderer
{
    private const int PreviewJpegQuality = 85;
    private const int MaxCachedThumbWidth = 240;

    private readonly PhotoboothStoragePaths _paths;
    private readonly Lock _cacheGate = new();
    private ThumbCacheKey? _cachedKey;
    private Lazy<CachedThumb>? _cachedThumb;

    public SkiaImagePreviewRenderer(PhotoboothStoragePaths paths)
    {
        _paths = paths;
    }

    public Task<byte[]> RenderPreviewAsync(
        string imageFilePath,
        bool mirror,
        int maxWidthPixels,
        CancellationToken cancellationToken = default) =>
        RenderPreviewAsync(imageFilePath, mirror, maxWidthPixels, ColorFilter.None, cancellationToken);

    public Task<byte[]> RenderPreviewAsync(
        string imageFilePath,
        bool mirror,
        int maxWidthPixels,
        ColorFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidthPixels);
        if (!_paths.IsInsideRoot(imageFilePath))
        {
            throw new UnauthorizedAccessException("Chỉ được đọc ảnh nằm trong thư mục dữ liệu của ứng dụng.");
        }

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (maxWidthPixels <= MaxCachedThumbWidth)
                {
                    return RenderFromCachedThumb(imageFilePath, mirror, maxWidthPixels, filter);
                }

                return RenderDirect(imageFilePath, mirror, maxWidthPixels, filter);
            },
            cancellationToken);
    }

    private byte[] RenderFromCachedThumb(string imageFilePath, bool mirror, int maxWidthPixels, ColorFilter filter)
    {
        var fullPath = Path.GetFullPath(imageFilePath);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new IOException("Không đọc được ảnh để hiển thị.");
        }

        var key = new ThumbCacheKey(fullPath, fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length, mirror, maxWidthPixels);
        Lazy<CachedThumb> lazy;
        lock (_cacheGate)
        {
            if (_cachedKey != key || _cachedThumb is null)
            {
                _cachedKey = key;
                _cachedThumb = new Lazy<CachedThumb>(
                    () => BuildBaseThumb(fullPath, mirror, maxWidthPixels),
                    LazyThreadSafetyMode.ExecutionAndPublication);
            }

            lazy = _cachedThumb;
        }

        CachedThumb thumb;
        try
        {
            thumb = lazy.Value;
        }
        catch
        {
            lock (_cacheGate)
            {
                if (ReferenceEquals(_cachedThumb, lazy))
                {
                    _cachedKey = null;
                    _cachedThumb = null;
                }
            }

            throw;
        }

        if (filter == ColorFilter.None)
        {
            return thumb.UnfilteredJpeg;
        }

        var info = new SKImageInfo(thumb.Width, thumb.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var baseImage = SKImage.FromPixelCopy(info, thumb.Pixels)
            ?? throw new IOException("Không tạo được ảnh thu nhỏ.");
        using var surface = SKSurface.Create(info)
            ?? throw new IOException("Không tạo được vùng vẽ.");
        using var colorFilter = TemplateRenderer.CreateColorFilter(filter);
        using var paint = new SKPaint { ColorFilter = colorFilter, IsAntialias = true };

        surface.Canvas.DrawImage(baseImage, 0, 0, TemplateRenderer.Sampling, paint);

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, PreviewJpegQuality);
        return data.ToArray();
    }

    private static CachedThumb BuildBaseThumb(string fullPath, bool mirror, int maxWidthPixels)
    {
        using var bitmap = SKBitmap.Decode(fullPath)
            ?? throw new IOException("Không đọc được ảnh để hiển thị.");
        using var image = SKImage.FromBitmap(bitmap);

        var scale = Math.Min(1f, maxWidthPixels / (float)image.Width);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

        using var surface = SKSurface.Create(info)
            ?? throw new IOException("Không tạo được vùng vẽ.");
        var canvas = surface.Canvas;
        if (mirror)
        {
            canvas.Scale(-1, 1, width / 2f, 0);
        }

        canvas.DrawImage(image, SKRect.Create(width, height), TemplateRenderer.Sampling);

        using var snapshot = surface.Snapshot();
        using var raster = new SKBitmap(info);
        if (!snapshot.ReadPixels(info, raster.GetPixels(), info.RowBytes, 0, 0))
        {
            throw new IOException("Không đọc được dữ liệu điểm ảnh thu nhỏ.");
        }

        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, PreviewJpegQuality);
        return new CachedThumb(width, height, raster.Bytes, data.ToArray());
    }

    private static byte[] RenderDirect(string imageFilePath, bool mirror, int maxWidthPixels, ColorFilter filter)
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

        using var colorFilter = TemplateRenderer.CreateColorFilter(filter);
        using var paint = new SKPaint { ColorFilter = colorFilter, IsAntialias = true };
        canvas.DrawImage(image, SKRect.Create(width, height), TemplateRenderer.Sampling, paint);

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, PreviewJpegQuality);
        return data.ToArray();
    }

    private readonly record struct ThumbCacheKey(string FullPath, long LastWriteTicks, long FileLength, bool Mirror, int MaxWidth);

    private sealed record CachedThumb(int Width, int Height, byte[] Pixels, byte[] UnfilteredJpeg);
}
