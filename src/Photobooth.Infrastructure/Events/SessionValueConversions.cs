using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Printing;

namespace Photobooth.Infrastructure.Events;

/// <summary>Manual Crop của một ô khi lưu trong database (cột JSON nhỏ, null = Auto Crop).</summary>
internal sealed record CropDocument(double Zoom, double FocalX, double FocalY)
{
    public static CropDocument? From(SlotCrop? crop) => crop is null ? null : new(crop.Zoom, crop.FocalX, crop.FocalY);

    public SlotCrop ToCrop() => new(Zoom, FocalX, FocalY);
}

/// <summary>Đọc giá trị chuỗi trong database thành enum nghiệp vụ (giá trị lạ → mặc định an toàn).</summary>
internal static class SessionDocumentEnums
{
    public static ColorFilter ParseFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ColorFilter.None;
        }

        var trimmed = value.Trim();
        if (!int.TryParse(trimmed, out _)
            && Enum.TryParse<ColorFilter>(trimmed, ignoreCase: true, out var filter)
            && Enum.IsDefined(filter))
        {
            return filter;
        }

        return trimmed.ToUpperInvariant() switch
        {
            "ORIGINAL" => ColorFilter.None,
            "BLACKANDWHITE" or "BW" or "MONOCHROME" => ColorFilter.Grayscale,
            "VINTAGE" or "RETRO" => ColorFilter.Sepia,
            "ROSY" or "ROSE" => ColorFilter.SoftRose,
            "GOLDEN" or "GOLDEN_HOUR" => ColorFilter.GoldenHour,
            _ => ColorFilter.None,
        };
    }

    public static PrintJobStatus ParsePrintStatus(string? value) =>
        Enum.TryParse<PrintJobStatus>(value, ignoreCase: true, out var status) && Enum.IsDefined(status) ? status : PrintJobStatus.Pending;

    public static ExportFormat? ParseFormat(string? value) =>
        Enum.TryParse<ExportFormat>(value, ignoreCase: true, out var format) && Enum.IsDefined(format) ? format : null;
}
