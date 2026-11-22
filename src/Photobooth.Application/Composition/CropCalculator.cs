namespace Photobooth.Application.Composition;

/// <summary>Vùng chữ nhật theo pixel.</summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height);

public static class CropCalculator
{
    /// <summary>
    /// Center-crop: vùng lớn nhất của ảnh nguồn có cùng tỉ lệ với ô đích, căn giữa.
    /// Ảnh được phóng/thu đều hai chiều nên không bao giờ bị kéo méo.
    /// </summary>
    public static PixelRect CenterCrop(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight) =>
        Cover(sourceWidth, sourceHeight, targetWidth, targetHeight, 0.5, 0.5);

    /// <summary>
    /// Cover: vùng lớn nhất của ảnh nguồn có cùng tỉ lệ với ô đích, đặt sao cho điểm ưu tiên
    /// (<paramref name="focalX"/>, <paramref name="focalY"/> trong khoảng 0..1) nằm càng gần tâm vùng cắt càng tốt,
    /// nhưng không vượt ra ngoài ảnh. Ảnh được phóng/thu đều hai chiều nên không bị kéo méo.
    /// <paramref name="zoom"/> &gt; 1 thu nhỏ vùng cắt (phóng to ảnh) theo cùng tỉ lệ — dùng cho Manual Crop;
    /// vùng cắt luôn nằm trong ảnh nên ô không bao giờ có khoảng trống.
    /// </summary>
    public static PixelRect Cover(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, double focalX, double focalY, double zoom = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);
        if (!double.IsFinite(zoom) || zoom < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), zoom, "Zoom phải là số hữu hạn ≥ 1.");
        }

        var sourceRatio = (double)sourceWidth / sourceHeight;
        var targetRatio = (double)targetWidth / targetHeight;

        double width;
        double height;
        if (sourceRatio > targetRatio)
        {
            // Ảnh nguồn rộng hơn: cắt bớt hai bên.
            height = sourceHeight;
            width = sourceHeight * targetRatio;
        }
        else
        {
            // Ảnh nguồn cao hơn (hoặc bằng tỉ lệ): cắt bớt trên dưới.
            width = sourceWidth;
            height = sourceWidth / targetRatio;
        }

        width /= zoom;
        height /= zoom;

        var x = Math.Clamp((sourceWidth * Math.Clamp(focalX, 0, 1)) - (width / 2), 0, sourceWidth - width);
        var y = Math.Clamp((sourceHeight * Math.Clamp(focalY, 0, 1)) - (height / 2), 0, sourceHeight - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// Vùng của ảnh nguồn THÔ (chưa lật) được vẽ vào một ô <c>cover</c>: Auto Crop theo focalPoint của template,
    /// hoặc Manual Crop nếu có. Với ảnh chụp ở chế độ gương, focal/crop tính theo hướng khách nhìn thấy nên
    /// tọa độ x được đổi sang phía đối diện trên ảnh thô. Đây là quy tắc DUY NHẤT dùng chung cho Photo Composer
    /// và lớp phủ khung chụp (Camera Framing Overlay).
    /// </summary>
    public static PixelRect CoverSourceRect(int sourceWidth, int sourceHeight, Templates.TemplateSlot slot, SlotCrop? crop, bool mirror)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var effective = crop ?? SlotCrop.AutoFor(slot);
        var focalX = mirror ? 1 - effective.FocalX : effective.FocalX;
        return Cover(sourceWidth, sourceHeight, Math.Max(1, slot.Width), Math.Max(1, slot.Height), focalX, effective.FocalY, effective.Zoom);
    }

    /// <summary>Contain: đặt vừa ảnh vào khung, giữ tỉ lệ, căn giữa (dùng cho logo/hình trang trí).</summary>
    public static PixelRect Fit(int sourceWidth, int sourceHeight, PixelRect target)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);

        var scale = Math.Min(target.Width / sourceWidth, target.Height / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        return new PixelRect(target.X + ((target.Width - width) / 2), target.Y + ((target.Height - height) / 2), width, height);
    }
}
