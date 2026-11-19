using Photobooth.Application.Templates;

namespace Photobooth.Application.Export;

/// <param name="ComposedFilePath">Ảnh thành phẩm (PNG) — nguồn chung cho mọi định dạng.</param>
/// <param name="BaseFileName">Tên file không có phần mở rộng; nếu trùng sẽ thêm hậu tố _2, _3...</param>
/// <param name="Canvas">Dùng để xác định kích thước trang PDF theo kích thước vật lý.</param>
public sealed record ExportRequest(
    string ComposedFilePath,
    ExportFormat Format,
    string DestinationDirectory,
    string BaseFileName,
    TemplateCanvas Canvas);

/// <summary>Xuất ảnh thành phẩm ra JPEG/PNG/PDF. Không bao giờ ghi đè file đã có.</summary>
public interface IComposedPhotoExporter
{
    Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default);
}

public static class ExportFormats
{
    public static string GetExtension(ExportFormat format) => format switch
    {
        ExportFormat.Jpeg => ".jpg",
        ExportFormat.Png => ".png",
        ExportFormat.Pdf => ".pdf",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };
}
