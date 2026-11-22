using System.Globalization;
using SkiaSharp;

namespace Photobooth.Infrastructure.Imaging;

/// <summary>
/// Bộ ảnh mẫu dùng để xem thử template mà không cần webcam: khung hình 16:9 (như webcam laptop),
/// màu nền khác nhau theo ảnh, hình người ở giữa, lưới 1/3 và số thứ tự lớn — để thấy rõ Auto Crop cắt phần nào.
/// Tạo hoàn toàn trong bộ nhớ, xác định (cùng đầu vào → cùng điểm ảnh).
/// </summary>
public static class TemplateSamplePhotos
{
    public const int DefaultWidthPixels = 1280;
    public const int DefaultHeightPixels = 720;

    private static readonly SKColor[] Backgrounds =
    [
        new(0xFF7DD3FC), new(0xFFFDBA74), new(0xFF86EFAC), new(0xFFF9A8D4), new(0xFFC4B5FD),
        new(0xFFFDE047), new(0xFF5EEAD4), new(0xFFFCA5A5), new(0xFFA5B4FC), new(0xFFBEF264),
    ];

    /// <summary>Tạo ảnh mẫu cho <paramref name="photoIndex"/>. Người gọi chịu trách nhiệm Dispose.</summary>
    public static SKImage Create(int photoIndex, int widthPixels = DefaultWidthPixels, int heightPixels = DefaultHeightPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(photoIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPixels);

        using var surface = SKSurface.Create(new SKImageInfo(widthPixels, heightPixels, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Không tạo được ảnh mẫu.");
        var canvas = surface.Canvas;
        float w = widthPixels, h = heightPixels;

        var background = Backgrounds[photoIndex % Backgrounds.Length];
        using (var shader = SKShader.CreateLinearGradient(
                   new SKPoint(0, 0), new SKPoint(0, h), [background, Darken(background, 0.75f)], SKShaderTileMode.Clamp))
        using (var fill = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(0, 0, w, h, fill);
        }

        // Lưới 1/3 để thấy vùng bị cắt.
        using (var grid = new SKPaint { Color = new SKColor(255, 255, 255, 110), StrokeWidth = Math.Max(2, h / 240), IsAntialias = true })
        {
            for (var i = 1; i < 3; i++)
            {
                canvas.DrawLine(w * i / 3, 0, w * i / 3, h, grid);
                canvas.DrawLine(0, h * i / 3, w, h * i / 3, grid);
            }
        }

        // Hình người: đầu + vai, đặt giữa khung (vị trí thường gặp khi đứng trước webcam).
        using (var person = new SKPaint { Color = new SKColor(0xFF1F2937), IsAntialias = true })
        {
            var cx = w / 2;
            var head = h * 0.14f;
            canvas.DrawCircle(cx, h * 0.38f, head, person);
            canvas.DrawRoundRect(SKRect.Create(cx - (h * 0.30f), h * 0.58f, h * 0.60f, h * 0.50f), h * 0.18f, h * 0.18f, person);
        }

        // Số thứ tự ảnh ở góc trái trên.
        using (var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default)
        using (var font = new SKFont(typeface, h * 0.22f))
        using (var label = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            canvas.DrawText((photoIndex + 1).ToString(CultureInfo.InvariantCulture), w * 0.04f, h * 0.25f, SKTextAlign.Left, font, label);
        }

        // Snapshot giữ tham chiếu riêng tới điểm ảnh (vẫn hợp lệ sau khi surface được giải phóng).
        // Không gọi ToRasterImage rồi Dispose snapshot: với ảnh đã là raster, hai biến trỏ cùng một đối tượng.
        return surface.Snapshot();
    }

    private static SKColor Darken(SKColor color, float factor) =>
        new((byte)(color.Red * factor), (byte)(color.Green * factor), (byte)(color.Blue * factor), color.Alpha);
}
