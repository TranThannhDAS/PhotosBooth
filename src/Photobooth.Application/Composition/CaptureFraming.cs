using Photobooth.Application.Templates;

namespace Photobooth.Application.Composition;

/// <summary>
/// Khung hướng dẫn trên live preview, tọa độ CHUẨN HÓA 0..1 theo khung hình camera ĐANG HIỂN THỊ
/// (đã lật gương nếu preview lật). <see cref="ShowsWholeImage"/> = ô <c>contain</c>: toàn bộ ảnh được giữ.
/// </summary>
/// <param name="SlotIndex">Ô trong template được dùng làm hướng dẫn (ô lớn nhất của photoIndex).</param>
public sealed record FramingGuide(int SlotIndex, bool ShowsWholeImage, double X, double Y, double Width, double Height);

/// <summary>
/// Camera Framing Overlay: vùng của khung hình camera sẽ nằm trong ô ảnh sau khi Photo Composer Auto Crop.
/// Dùng đúng <see cref="CropCalculator.CoverSourceRect"/> của Photo Composer nên hướng dẫn và thành phẩm không lệch nhau.
/// Chỉ để hiển thị: không cắt ảnh gốc.
/// </summary>
public static class CaptureFraming
{
    /// <summary>
    /// Ô dùng làm hướng dẫn cho ảnh <paramref name="photoIndex"/>: ô có diện tích lớn nhất; bằng nhau thì ô khai báo trước.
    /// </summary>
    public static int? GuideSlotIndex(PhotoTemplate template, int photoIndex)
    {
        ArgumentNullException.ThrowIfNull(template);
        int? best = null;
        long bestArea = -1;
        for (var i = 0; i < template.Slots.Count; i++)
        {
            var slot = template.Slots[i];
            var area = (long)Math.Max(0, slot.Width) * Math.Max(0, slot.Height);
            if (slot.PhotoIndex == photoIndex && area > bestArea)
            {
                best = i;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>
    /// Tính khung hướng dẫn cho ảnh sắp chụp. <paramref name="mirrored"/> = preview đang lật gương
    /// (ảnh chụp sẽ được ghép theo cùng hướng). Trả về null nếu không có ô nào dùng ảnh này hoặc kích thước không hợp lệ.
    /// </summary>
    public static FramingGuide? Compute(PhotoTemplate template, int photoIndex, int frameWidth, int frameHeight, bool mirrored)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (frameWidth <= 0 || frameHeight <= 0 || GuideSlotIndex(template, photoIndex) is not { } slotIndex)
        {
            return null;
        }

        var slot = template.Slots[slotIndex];
        if (!SlotCrop.IsSupported(slot))
        {
            return new FramingGuide(slotIndex, ShowsWholeImage: true, 0, 0, 1, 1);
        }

        // Vùng trên ảnh THÔ đúng như Photo Composer sẽ dùng (Auto Crop, chưa có Manual Crop lúc chụp).
        var raw = CropCalculator.CoverSourceRect(frameWidth, frameHeight, slot, crop: null, mirrored);
        var x = raw.X / frameWidth;
        var width = raw.Width / frameWidth;

        // Preview lật gương: đổi sang hướng khách đang nhìn.
        if (mirrored)
        {
            x = 1 - x - width;
        }

        return new FramingGuide(slotIndex, ShowsWholeImage: false, x, raw.Y / frameHeight, width, raw.Height / frameHeight);
    }
}
