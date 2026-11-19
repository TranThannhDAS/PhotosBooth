using System.Globalization;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Composition;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Storage;
using SkiaSharp;

namespace Photobooth.Infrastructure.Imaging;

/// <summary>
/// Engine ghép ảnh bằng SkiaSharp. Chạy ngoài UI thread, chỉ đọc ảnh gốc và ghi thành phẩm PNG mới
/// vào Sessions/{SessionId}/Outputs (không bao giờ ghi đè).
/// </summary>
public sealed class SkiaPhotoComposer : IPhotoComposer
{
    private const int PreviewJpegQuality = 85;

    /// <summary>Ảnh đại diện tùy chọn đặt cạnh template.json.</summary>
    public const string ThumbnailFileName = "thumbnail.png";

    private readonly PhotoboothStoragePaths _paths;
    private readonly ILogger<SkiaPhotoComposer> _logger;

    public SkiaPhotoComposer(PhotoboothStoragePaths paths, ILogger<SkiaPhotoComposer> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<ComposedImage> ComposeAsync(CompositionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = TemplateValidator.Validate(request.Template);
        if (!validation.IsValid)
        {
            throw new CompositionException($"Template {request.Template.Id} không hợp lệ: {string.Join(" ", validation.Errors)}");
        }

        // Mỗi ô phải tìm được ảnh nguồn theo photoIndex (một ảnh có thể dùng cho nhiều ô).
        for (var i = 0; i < request.Template.Slots.Count; i++)
        {
            var slot = request.Template.Slots[i];
            if (slot.PhotoIndex >= request.Photos.Count)
            {
                throw new CompositionException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Ô ảnh slots[{i}]{(slot.Id is null ? string.Empty : $"({slot.Id})")} cần ảnh photoIndex {slot.PhotoIndex} nhưng chỉ có {request.Photos.Count} ảnh."));
            }
        }

        var png = await Task.Run(() => RenderComposition(request, cancellationToken), cancellationToken).ConfigureAwait(false);

        var directory = _paths.GetOutputsDirectory(request.SessionId);
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"composed-{Guid.NewGuid():N}.png"));

        // File thành phẩm chỉ xuất hiện khi đã ghi đầy đủ.
        await SafeFileWriter.WriteAsync(
                filePath,
                (stream, token) => stream.WriteAsync(png, token).AsTask(),
                overwrite: false,
                cancellationToken)
            .ConfigureAwait(false);

        return new ComposedImage(filePath, request.Template.Canvas.WidthPixels, request.Template.Canvas.HeightPixels);
    }

    public Task<byte[]> RenderTemplatePreviewAsync(PhotoTemplate template, int maxWidthPixels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidthPixels);

        // Một thời điểm duy nhất cho mọi biến {date}/{time} trong lượt render này.
        var timestamp = DateTimeOffset.Now;
        return Task.Run(
            () =>
            {
                using var thumbnail = LoadThumbnail(template);
                var canvasInfo = template.Canvas;
                var sourceWidth = thumbnail?.Width ?? canvasInfo.WidthPixels;
                var sourceHeight = thumbnail?.Height ?? canvasInfo.HeightPixels;
                var scale = Math.Min(1f, maxWidthPixels / (float)sourceWidth);
                var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
                var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

                using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                    ?? throw new CompositionException("Không tạo được vùng vẽ.");
                var canvas = surface.Canvas;
                if (thumbnail is not null)
                {
                    canvas.Clear(SKColors.White);
                    canvas.DrawImage(thumbnail, SKRect.Create(width, height), TemplateRenderer.Sampling);
                }
                else
                {
                    canvas.Scale(scale);
                    TemplateRenderer.Render(canvas, template, timestamp, DrawPlaceholder);
                }

                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, PreviewJpegQuality);
                return data.ToArray();
            },
            cancellationToken);
    }

    /// <summary>thumbnail.png tùy chọn trong thư mục template; lỗi đọc thì dùng ảnh render tự động.</summary>
    private SKImage? LoadThumbnail(PhotoTemplate template)
    {
        if (template.AssetDirectory is null)
        {
            return null;
        }

        var path = Path.Combine(template.AssetDirectory, ThumbnailFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        using var bitmap = SKBitmap.Decode(path);
        if (bitmap is null)
        {
            _logger.LogWarning("Không đọc được thumbnail của template {TemplateId}, dùng ảnh render tự động", template.Id);
            return null;
        }

        return SKImage.FromBitmap(bitmap).ToRasterImage(ensurePixelData: true);
    }

    private byte[] RenderComposition(CompositionRequest request, CancellationToken cancellationToken)
    {
        var images = new SKImage[request.Photos.Count];
        try
        {
            for (var i = 0; i < request.Photos.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                images[i] = LoadOriginal(request.Photos[i].FilePath);
            }

            var canvasInfo = request.Template.Canvas;
            using var surface = SKSurface.Create(new SKImageInfo(canvasInfo.WidthPixels, canvasInfo.HeightPixels, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new CompositionException("Không tạo được vùng vẽ cho ảnh thành phẩm.");
            using var filter = TemplateRenderer.CreateColorFilter(request.Filter);

            TemplateRenderer.Render(
                surface.Canvas,
                request.Template,
                request.LocalTimestamp,
                (canvas, slot, rect) => TemplateRenderer.DrawPhoto(canvas, images[slot.PhotoIndex], slot, rect, request.Photos[slot.PhotoIndex].Mirror, filter));

            cancellationToken.ThrowIfCancellationRequested();
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new CompositionException("Không mã hóa được ảnh thành phẩm.");
            return data.ToArray();
        }
        finally
        {
            foreach (var image in images)
            {
                image?.Dispose();
            }
        }
    }

    private SKImage LoadOriginal(string filePath)
    {
        if (!_paths.IsInsideRoot(filePath) || !File.Exists(filePath))
        {
            throw new CompositionException("Không tìm thấy ảnh gốc trong thư mục dữ liệu của ứng dụng.");
        }

        using var bitmap = SKBitmap.Decode(filePath);
        if (bitmap is null)
        {
            _logger.LogWarning("Không giải mã được ảnh gốc khi ghép");
            throw new CompositionException("Ảnh gốc bị hỏng hoặc không đúng định dạng.");
        }

        // Ảnh raster bất biến: an toàn để dùng lại cho nhiều ô và sau khi bitmap được giải phóng.
        return SKImage.FromBitmap(bitmap).ToRasterImage(ensurePixelData: true);
    }

    private static void DrawPlaceholder(SKCanvas canvas, TemplateSlot slot, SKRect rect)
    {
        using var fill = new SKPaint { Color = new SKColor(0xFFCBD5E1), Style = SKPaintStyle.Fill };
        canvas.DrawRect(rect, fill);

        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, Math.Min(rect.Width, rect.Height) * 0.35f);
        using var paint = new SKPaint { Color = new SKColor(0xFF64748B), IsAntialias = true };
        var metrics = font.Metrics;
        canvas.DrawText(
            (slot.PhotoIndex + 1).ToString(CultureInfo.InvariantCulture),
            rect.MidX,
            rect.MidY - ((metrics.Ascent + metrics.Descent) / 2),
            SKTextAlign.Center,
            font,
            paint);
    }
}
