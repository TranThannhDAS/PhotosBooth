using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Printing;
using Photobooth.Application.Sessions;

namespace Photobooth.Infrastructure.Events;

// Cấu trúc session.json. Mọi đường dẫn file là TƯƠNG ĐỐI với thư mục phiên (sao chép thư mục sự kiện sang máy khác
// vẫn mở lại được), trừ file xuất ra ngoài (exports) — chỉ để tham khảo.
// Không có schemaVersion: thuộc tính thiếu dùng giá trị mặc định, thuộc tính lạ được bỏ qua.

internal sealed record SessionDocument
{
    public Guid SessionId { get; init; }

    public Guid? EventId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>inProgress | completed | cancelled (thông tin; không ảnh hưởng việc mở lại).</summary>
    public string Status { get; init; } = "inProgress";

    public string? TemplateId { get; init; }

    public string? TemplateName { get; init; }

    /// <summary>Snapshot template (kèm asset) trong thư mục phiên, ví dụ "Template/template.json".</summary>
    public string? TemplateFile { get; init; }

    public bool Mirror { get; init; }

    /// <summary>Mọi ảnh gốc đã lưu (kể cả ảnh bị thay khi chụp lại).</summary>
    public List<PhotoDocument> Photos { get; init; } = [];

    /// <summary>Ảnh đang chọn cho từng photoIndex (null = chưa chụp).</summary>
    public List<Guid?> SelectedPhotoIds { get; init; } = [];

    public string Filter { get; init; } = nameof(ColorFilter.None);

    public List<CropDocument?> SlotCrops { get; init; } = [];

    public List<VersionDocument> Versions { get; init; } = [];

    public List<ExportDocument> Exports { get; init; } = [];

    public List<PrintJobDocument> PrintJobs { get; init; } = [];
}

internal sealed record PhotoDocument(
    Guid PhotoId,
    int Sequence,
    int Position,
    DateTimeOffset CapturedAt,
    int WidthPixels,
    int HeightPixels,
    string File,
    long SizeBytes,
    bool Mirrored);

internal sealed record CropDocument(double Zoom, double FocalX, double FocalY)
{
    public static CropDocument? From(SlotCrop? crop) => crop is null ? null : new(crop.Zoom, crop.FocalX, crop.FocalY);

    public SlotCrop ToCrop() => new(Zoom, FocalX, FocalY);
}

internal sealed record VersionDocument
{
    public int Version { get; init; }

    public string File { get; init; } = string.Empty;

    public string? Thumbnail { get; init; }

    public int WidthPixels { get; init; }

    public int HeightPixels { get; init; }

    public int Dpi { get; init; }

    public string TemplateId { get; init; } = string.Empty;

    public string Filter { get; init; } = nameof(ColorFilter.None);

    public List<Guid> PhotoIds { get; init; } = [];

    public List<CropDocument?> SlotCrops { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed record ExportDocument(string Format, string FilePath, DateTimeOffset ExportedAt, int? Version);

internal sealed record PrintJobDocument(
    Guid PrintJobId,
    int Version,
    string File,
    int Copies,
    string PrinterName,
    bool IsPhysicalPrinter,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? ErrorMessage);

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
