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
    /// <param name="drawSlotContent">Vẽ nội dung một ô (ảnh hoặc vùng giữ chỗ); canvas đã được clip theo khung bo góc.</param>
    public static void Render(
        SKCanvas canvas,
        PhotoTemplate template,
        DateTimeOffset localTimestamp,
        Action<SKCanvas, TemplateSlot, SKRect> drawSlotContent)
    {
        canvas.Clear(ToColor(template.Canvas.BackgroundColor));

        foreach (var element in TemplateLayers.GetRenderOrder(template))
        {
            if (element.Slot is { } slot)
            {
                DrawSlot(canvas, slot, drawSlotContent);
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
    /// </summary>
    public static void DrawPhoto(SKCanvas canvas, SKImage image, TemplateSlot slot, SKRect destination, bool mirror, SKColorFilter? filter)
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
            // Ảnh gốc là khung hình thô; nếu hiển thị lật gương thì focal x tương ứng phía đối diện trên ảnh thô.
            var focalX = mirror ? 1 - slot.FocalPoint.X : slot.FocalPoint.X;
            var crop = CropCalculator.Cover(
                image.Width, image.Height,
                Math.Max(1, (int)Math.Round(destination.Width)), Math.Max(1, (int)Math.Round(destination.Height)),
                focalX, slot.FocalPoint.Y);
            source = ToRect(crop);
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

    public static SKColorFilter? CreateColorFilter(ColorFilter filter) => filter switch
    {
        ColorFilter.Grayscale => SKColorFilter.CreateColorMatrix(
        [
            0.2126f, 0.7152f, 0.0722f, 0, 0,
            0.2126f, 0.7152f, 0.0722f, 0, 0,
            0.2126f, 0.7152f, 0.0722f, 0, 0,
            0, 0, 0, 1, 0,
        ]),
        ColorFilter.Sepia => SKColorFilter.CreateColorMatrix(
        [
            0.393f, 0.769f, 0.189f, 0, 0,
            0.349f, 0.686f, 0.168f, 0, 0,
            0.272f, 0.534f, 0.131f, 0, 0,
            0, 0, 0, 1, 0,
        ]),
        ColorFilter.Warm => SKColorFilter.CreateColorMatrix(
        [
            1.08f, 0, 0, 0, 0,
            0, 1.0f, 0, 0, 0,
            0, 0, 0.88f, 0, 0,
            0, 0, 0, 1, 0,
        ]),
        _ => null,
    };

    public static SKColor ToColor(string? hex) =>
        HexColor.TryParse(hex, out var argb) ? new SKColor(argb) : SKColors.Transparent;

    private static void DrawSlot(SKCanvas canvas, TemplateSlot slot, Action<SKCanvas, TemplateSlot, SKRect> drawSlotContent)
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

        drawSlotContent(canvas, slot, rect);
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
        using var typeface = SKTypeface.FromFamilyName(decoration.FontFamily ?? DefaultFontFamily, style)
            ?? SKTypeface.FromFamilyName(DefaultFontFamily, style)
            ?? SKTypeface.Default;
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
