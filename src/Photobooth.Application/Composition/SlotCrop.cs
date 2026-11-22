using Photobooth.Application.Templates;

namespace Photobooth.Application.Composition;

/// <summary>
/// Chỉnh vùng cắt thủ công (Manual Crop) cho một ô ảnh <c>fit = cover</c> trong một phiên chụp.
/// Là dữ liệu của phiên, <b>không</b> ghi vào template.json và không sửa ảnh gốc.
/// </summary>
/// <remarks>
/// Dùng cùng nguyên lý với Auto Crop (<see cref="CropCalculator.Cover"/>): vùng cắt cơ sở là vùng lớn nhất
/// cùng tỉ lệ với ô, thu nhỏ theo <paramref name="Zoom"/>, rồi đặt tâm tại (<paramref name="FocalX"/>, <paramref name="FocalY"/>)
/// và kẹp trong ảnh. Tọa độ chuẩn hóa 0..1 theo <b>hướng ảnh khách nhìn thấy</b> (đã lật gương nếu ảnh chụp ở chế độ gương),
/// nên kết quả không phụ thuộc kích thước màn hình hay DPI.
/// Auto Crop tương đương <c>Zoom = 1</c> và focal point của template.
/// </remarks>
/// <param name="Zoom">Hệ số phóng so với vùng cắt cơ sở (1 = 100%).</param>
public sealed record SlotCrop(double Zoom, double FocalX, double FocalY)
{
    /// <summary>100%: đúng vùng cắt cơ sở của Auto Crop (ảnh vừa phủ kín ô).</summary>
    public const double MinZoom = 1.0;

    /// <summary>Giới hạn phóng to kỹ thuật (300%). Là nơi duy nhất quy định; UI đọc giá trị này.</summary>
    public const double MaxZoom = 3.0;

    public bool IsValid =>
        double.IsFinite(Zoom) && Zoom is >= MinZoom and <= MaxZoom
        && double.IsFinite(FocalX) && FocalX is >= 0 and <= 1
        && double.IsFinite(FocalY) && FocalY is >= 0 and <= 1;

    /// <summary>Vùng cắt Auto Crop của ô theo template (zoom 100%, focal point của template).</summary>
    public static SlotCrop AutoFor(TemplateSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return new SlotCrop(MinZoom, slot.FocalPoint.X, slot.FocalPoint.Y);
    }

    /// <summary>Chỉ ô <c>cover</c> hỗ trợ chỉnh thủ công; ô <c>contain</c> luôn hiển thị toàn bộ ảnh.</summary>
    public static bool IsSupported(TemplateSlot slot) => slot is { Fit: ImageFit.Cover };
}
