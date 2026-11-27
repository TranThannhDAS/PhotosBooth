namespace Photobooth.Application.Templates;

/// <summary>Kết quả kiểm tra một file template khi nạp (để ghi log và phục vụ Template Editor sau này).</summary>
/// <param name="TemplateFile">Đường dẫn tương đối với thư mục Templates.</param>
/// <param name="Loaded">Template có được nạp để sử dụng hay không.</param>
public sealed record TemplateFileDiagnostics(string TemplateFile, string? TemplateId, bool Loaded, IReadOnlyList<TemplateValidationIssue> Issues);

/// <summary>Danh sách template đi kèm ứng dụng. Template lỗi bị bỏ qua (không làm dừng ứng dụng) và được báo cáo.</summary>
public interface ITemplateCatalog
{
    /// <summary>Các template hợp lệ (không có lỗi; có thể có cảnh báo).</summary>
    Task<IReadOnlyList<PhotoTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiosk: chỉ template chung và template thuộc sự kiện hiện tại.
    /// Catalog tĩnh cũ giữ hành vi mặc định để tương thích interface.
    /// </summary>
    Task<IReadOnlyList<PhotoTemplate>> GetTemplatesForEventAsync(Guid eventId, CancellationToken cancellationToken = default)
        => GetTemplatesAsync(cancellationToken);

    /// <summary>Kết quả kiểm tra của từng file template, kể cả file bị bỏ qua.</summary>
    Task<IReadOnlyList<TemplateFileDiagnostics>> GetDiagnosticsAsync(CancellationToken cancellationToken = default);
}
