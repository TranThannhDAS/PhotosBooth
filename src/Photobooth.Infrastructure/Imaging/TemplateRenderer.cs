using System.Text;
using Photobooth.Application.Composition;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Templates;
using SkiaSharp;

namespace Photobooth.Infrastructure.Imaging;

/// <summary>
/// Vẽ template lên canvas: nền canvas trước, sau đó mọi phần tử (ô ảnh, hình, chữ, ảnh trang trí)
/// theo một danh sách thống nhất sắp xếp bằng zIndex (<see cref="TemplateLayers.GetRenderOrder"/>).
/// Dùng chung cho ảnh thành phẩm và ảnh xem trước template để bố cục luôn nhất quán.
/// </summary>
internal static class TemplateRenderer
{
    public const string DefaultFontFamily = "Segoe UI";

    public static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <param name="localTimestamp">Thời điểm thống nhất cho {date}/{time}/{datetime}.</param>
    /// <param name="drawSlotContent">
    /// Vẽ nội dung một ô (ảnh hoặc vùng giữ chỗ); canvas đã được clip theo khung bo góc.
    /// Tham số int là chỉ số ô trong <c>template.Slots</c> (để áp Manual Crop theo từng ô).
    /// </param>
    public static void Render(
        SKCanvas canvas,
        PhotoTemplate template,
        DateTimeOffset localTimestamp,
        Action<SKCanvas, int, TemplateSlot, SKRect> drawSlotContent)
    {
        canvas.Clear(ToColor(template.Canvas.BackgroundColor));

        foreach (var element in TemplateLayers.GetRenderOrder(template))
        {
            if (element.Slot is { } slot)
            {
                // Với ô ảnh, DeclarationOrder chính là chỉ số trong template.Slots.
                DrawSlot(canvas, element.DeclarationOrder, slot, drawSlotContent);
            }
            else if (element.Decoration is { } decoration)
            {
                DrawDecoration(canvas, template, decoration, localTimestamp);
            }
        }
    }

    /// <summary>
    /// Vẽ ảnh vào ô theo fit. Cover: cắt theo focal point; contain: đặt vừa, giữ tỉ lệ.
    /// Mirror: lật ngang quanh tâm ô (chỉ ảnh). Focal point tính theo hướng ảnh hiển thị (sau khi lật).
    /// <paramref name="crop"/> (Manual Crop, chỉ cho cover) thay focal point/zoom của template; cùng một công thức Auto Crop.
    /// </summary>
    public static void DrawPhoto(SKCanvas canvas, SKImage image, TemplateSlot slot, SKRect destination, bool mirror, SKColorFilter? filter, SlotCrop? crop = null)
    {
        SKRect source;
        SKRect target;
        if (slot.Fit == ImageFit.Contain)
        {
            source = SKRect.Create(image.Width, image.Height);
            target = ToRect(CropCalculator.Fit(image.Width, image.Height, ToPixelRect(destination)));
        }
        else
        {
            // Cùng quy tắc với lớp phủ khung chụp: ảnh gốc là khung hình thô; nếu hiển thị lật gương thì
            // focal x tương ứng phía đối diện trên ảnh thô. Vùng cắt kẹp trong ảnh nên phép lật đối xứng.
            source = ToRect(CropCalculator.CoverSourceRect(image.Width, image.Height, slot, crop, mirror));
            target = destination;
        }

        canvas.Save();
        if (mirror)
        {
            canvas.Scale(-1, 1, destination.MidX, 0);
        }

        using var paint = new SKPaint { ColorFilter = filter, IsAntialias = true };
        canvas.DrawImage(image, source, target, Sampling, paint);
        canvas.Restore();
    }

    public static SKColorFilter? CreateColorFilter(ColorFilter filter) =>
        GetColorMatrix(filter) is { } matrix ? SKColorFilter.CreateColorMatrix(matrix) : null;

    /// <summary>
    /// Ma trận màu 4×5 (SkiaSharp: cột dịch chuyển thứ 5 chuẩn hóa trong khoảng 0..1) cho từng bộ lọc.
    /// Chỉ áp dụng khi vẽ ảnh chụp; không áp dụng cho nền, chữ, khung hay ảnh trang trí của template.
    /// </summary>
    public static float[]? GetColorMatrix(ColorFilter filter) => filter switch
    {
        ColorFilter.Warm =>
        [
            1.08f, 0f, 0f, 0f, 0f,
            0f, 1.0f, 0f, 0f, 0f,
            0f, 0f, 0.88f, 0f, 0f,
            0f, 0f, 0f, 1f, 0f,
        ],
        ColorFilter.Cool =>
        [
            // Tông lạnh trong trẻo: giảm nhẹ sắc đỏ ấm, nâng nhẹ sắc lam và xanh ngọc.
            0.91f, 0.02f, 0f, 0f, 0f,
            0f, 1.01f, 0.02f, 0f, 0f,
            0f, 0.04f, 1.08f, 0f, 0.01f,
            0f, 0f, 0f, 1f, 0f,
        ],
        ColorFilter.Bright =>
        [
            // Tươi sáng: nâng độ sáng đều ba kênh kèm tăng nhẹ độ trong và độ tươi màu, tránh cháy sáng.
            1.13f, -0.06f, -0.01f, 0f, 0.035f,
            -0.02f, 1.09f, -0.01f, 0f, 0.035f,
            -0.02f, -0.06f, 1.14f, 0f, 0.035f,
            0f, 0f, 0f, 1f, 0f,
        ],
        ColorFilter.Pastel =>
        [
            // Dịu nhẹ: giảm nhẹ độ bão hòa gắt, nâng vùng tối mềm mại theo phong cách kem phấn.
            0.82f, 0.11f, 0.02f, 0f, 0.065f,
            0.04f, 0.87f, 0.03f, 0f, 0.055f,
            0.04f, 0.10f, 0.81f, 0f, 0.060f,
            0f, 0f, 0f, 1f, 0f,
        ],
        ColorFilter.SoftRose =>
        [
            // Hồng phấn: nâng nhẹ sắc hồng đào (Red + chút Blue), giúp tông da hồng hào cho tiệc cưới/sinh nhật.
            1.05f, 0.03f, 0.02f, 0f, 0.025f,
            0.01f, 0.96f, 0.01f, 0f, 0.010f,
            0.03f, 0.02f, 0.98f, 0f, 0.020f,
            0f, 0f, 0f, 1f, 0f,
        ],
        ColorFilter.GoldenHour =>
        [
            // Nắng vàng: ánh vàng hổ phách (Red + Green) ấm áp và mềm mại của buổi hoàng hôn.
            1.09f, 0.06f, 0f, 0f, 0.025f,
            0.03f, 1.01f, 0f, 0f, 0.015f,
            0f, 0f, 0.82f, 0f, -0.010f,
            0f, 0f, 0f, 1f, 0f,
        ],
        ColorFilter.Film =>
        [
            // Màu phim analog: giữ đa sắc màu với tương phản rõ, vùng tối ánh xanh phim mờ, vùng sáng ấm dịu.
            1.12f, -0.04f, -0.02f, 0f, -0.010f,
            -0.02f, 1.07f, 0.01f, 0f, -0.010f,
            -0.01f, -0.04f, 1.02f, 0f, 0.015f,
            0f, 0f, 0f, 1f, 0f,
        ],
        ColorFilter.Cinematic =>
        [
            // Điện ảnh (Teal & Orange): tương phản sâu, vùng tối ngả xanh cổ vịt, vùng sáng và màu da nổi bật.
            1.18f, -0.06f, -0.04f, 0f, -0.045f,
            -0.04f, 1.12f, 0.02f, 0f, -0.025f,
            -0.06f, 0.04f, 1.14f, 0f, -0.010f,
            0f, 0f, 0f, 1f, 0f,
        ],
        ColorFilter.Moody =>
        [
            // Trầm nghệ thuật: giảm độ bão hòa vừa phải (~25%), hạ nhẹ độ sáng và tăng chiều sâu vùng tối.
            0.74f, 0.16f, 0.02f, 0f, -0.030f,
            0.06f, 0.82f, 0.03f, 0f, -0.030f,
            0.06f, 0.15f, 0.72f, 0f, -0.020f,
            0f, 0f, 0f, 1f, 0f,
        ],
        ColorFilter.Sepia =>
        [
            0.393f, 0.769f, 0.189f, 0f, 0f,
            0.349f, 0.686f, 0.168f, 0f, 0f,
            0.272f, 0.534f, 0.131f, 0f, 0f,
            0f, 0f, 0f, 1f, 0f,
        ],
        ColorFilter.Grayscale =>
        [
            0.2126f, 0.7152f, 0.0722f, 0f, 0f,
            0.2126f, 0.7152f, 0.0722f, 0f, 0f,
            0.2126f, 0.7152f, 0.0722f, 0f, 0f,
            0f, 0f, 0f, 1f, 0f,
        ],
        _ => null,
    };

    public static SKColor ToColor(string? hex) =>
        HexColor.TryParse(hex, out var argb) ? new SKColor(argb) : SKColors.Transparent;

    private static void DrawSlot(SKCanvas canvas, int slotIndex, TemplateSlot slot, Action<SKCanvas, int, TemplateSlot, SKRect> drawSlotContent)
    {
        var rect = SKRect.Create(slot.X, slot.Y, slot.Width, slot.Height);
        var radius = ClampRadius(slot.CornerRadius, rect);

        canvas.Save();
        if (radius > 0)
        {
            canvas.ClipRoundRect(new SKRoundRect(rect, radius), SKClipOperation.Intersect, antialias: true);
        }
        else
        {
            canvas.ClipRect(rect);
        }

        if (slot.BackgroundColor is not null)
        {
            using var background = new SKPaint { Color = ToColor(slot.BackgroundColor), Style = SKPaintStyle.Fill };
            canvas.DrawRect(rect, background);
        }

        drawSlotContent(canvas, slotIndex, slot, rect);
        canvas.Restore();

        if (slot.BorderWidth > 0)
        {
            using var border = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = slot.BorderWidth,
                Color = ToColor(slot.BorderColor),
                IsAntialias = true,
            };
            var inset = slot.BorderWidth / 2f;
            var borderRadius = Math.Max(0, radius - inset);
            canvas.DrawRoundRect(SKRect.Inflate(rect, -inset, -inset), borderRadius, borderRadius, border);
        }
    }

    private static void DrawDecoration(SKCanvas canvas, PhotoTemplate template, TemplateDecoration decoration, DateTimeOffset localTimestamp)
    {
        var rect = SKRect.Create(decoration.X, decoration.Y, decoration.Width, decoration.Height);
        switch (decoration.Type)
        {
            case DecorationKind.Rectangle:
                var radius = ClampRadius(decoration.CornerRadius, rect);
                DrawShape(decoration, paint => canvas.DrawRoundRect(rect, radius, radius, paint));
                break;
            case DecorationKind.Ellipse:
                DrawShape(decoration, paint => canvas.DrawOval(rect, paint));
                break;
            case DecorationKind.Text:
                DrawText(canvas, decoration, rect, localTimestamp);
                break;
            case DecorationKind.Image:
                DrawImage(canvas, template, decoration, rect);
                break;
            case DecorationKind.QrCode:
                // Ảnh thành phẩm không chứa QR: QR của đúng phiên bản chỉ được vẽ lên bản in (PrintImageRenderer).
                break;
        }
    }

    private static void DrawShape(TemplateDecoration decoration, Action<SKPaint> draw)
    {
        if (decoration.Color is not null)
        {
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = ToColor(decoration.Color), IsAntialias = true };
            draw(fill);
        }

        if (decoration.StrokeWidth > 0 && decoration.StrokeColor is not null)
        {
            using var stroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = decoration.StrokeWidth,
                Color = ToColor(decoration.StrokeColor),
                IsAntialias = true,
            };
            draw(stroke);
        }
    }

    /// <summary>Chữ căn giữa theo chiều dọc trong khung; tự thu nhỏ cỡ chữ nếu dài hơn chiều rộng khung.</summary>
    private static void DrawText(SKCanvas canvas, TemplateDecoration decoration, SKRect rect, DateTimeOffset localTimestamp)
    {
        var text = TemplateText.Resolve(decoration.Text ?? string.Empty, localTimestamp).Normalize(NormalizationForm.FormC);
        var style = decoration.Bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        // Cả Preview và ảnh thành phẩm cùng dùng renderer này.
        // Tự chọn font có đủ glyph (đặc biệt chữ Việt dấu) trước khi đo rộng và vẽ.
        using var typeface = TemplateTextFontResolver.Resolve(text, decoration.FontFamily, style);
        using var font = new SKFont(typeface, decoration.FontSizePixels) { Subpixel = true };
        using var paint = new SKPaint { Color = ToColor(decoration.Color ?? "#000000"), IsAntialias = true };

        var width = font.MeasureText(text);
        if (width > rect.Width && width > 0)
        {
            font.Size *= rect.Width / width;
        }

        var metrics = font.Metrics;
        var baseline = rect.MidY - ((metrics.Ascent + metrics.Descent) / 2);
        var (x, align) = decoration.TextAlign switch
        {
            TextAlignment.Left => (rect.Left, SKTextAlign.Left),
            TextAlignment.Right => (rect.Right, SKTextAlign.Right),
            _ => (rect.MidX, SKTextAlign.Center),
        };

        canvas.DrawText(text, x, baseline, align, font, paint);
    }

    /// <summary>Ảnh trang trí (PNG giữ nguyên kênh alpha). Không sửa file nguồn; giải phóng bitmap sau khi vẽ.</summary>
    private static void DrawImage(SKCanvas canvas, PhotoTemplate template, TemplateDecoration decoration, SKRect rect)
    {
        if (!TemplateAssetResolver.TryResolve(template.AssetDirectory, decoration.AssetPath, out var path, out var error))
        {
            throw new CompositionException($"Template {template.Id}: {error}");
        }

        using var bitmap = SKBitmap.Decode(path)
            ?? throw new CompositionException($"Template {template.Id}: không đọc được asset '{decoration.AssetPath}' (file hỏng hoặc không phải ảnh).");
        using var image = SKImage.FromBitmap(bitmap);

        canvas.Save();
        canvas.ClipRect(rect);
        if (decoration.Fit == ImageFit.Cover)
        {
            var crop = CropCalculator.CenterCrop(image.Width, image.Height, Math.Max(1, decoration.Width), Math.Max(1, decoration.Height));
            canvas.DrawImage(image, ToRect(crop), rect, Sampling);
        }
        else
        {
            canvas.DrawImage(image, ToRect(CropCalculator.Fit(image.Width, image.Height, ToPixelRect(rect))), Sampling);
        }

        canvas.Restore();
    }

    private static float ClampRadius(int radius, SKRect rect) =>
        Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2f);

    private static SKRect ToRect(PixelRect r) => SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);

    private static PixelRect ToPixelRect(SKRect r) => new(r.Left, r.Top, r.Width, r.Height);
}
