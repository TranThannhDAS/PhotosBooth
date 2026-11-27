namespace Photobooth.Application.Templates;

/// <summary>
/// Một template trong Template Library (template chung hoặc thuộc một sự kiện).
/// <see cref="Key"/> = tên thư mục trong thư viện; template lỗi vẫn được liệt kê (Template = null) để quản trị viên xử lý.
/// </summary>
public sealed record TemplateLibraryEntry(
    string Key,
    PhotoTemplate? Template,
    bool Enabled,
    IReadOnlyList<TemplateValidationIssue> Issues,
    Guid? EventId = null)
{
    /// <summary>Không gắn sự kiện thì dùng được trong mọi sự kiện.</summary>
    public bool IsShared => EventId is null;

    public bool IsValid => Template is not null;

    public string DisplayName => Template?.Name ?? Key;
}

/// <summary>Thông tin tạo template mới (kích thước canvas tính bằng pixel).</summary>
public sealed record NewTemplateRequest(string Name, string? Description, int WidthPixels, int HeightPixels, int Dpi, string BackgroundColor, Guid? EventId = null);

/// <summary>Kết quả thao tác có kiểm tra (nhập, lưu): lỗi cụ thể theo vị trí thuộc tính, không ghi đè bản hợp lệ khi thất bại.</summary>
public sealed record TemplateOperationResult(
    bool Succeeded,
    TemplateLibraryEntry? Entry,
    IReadOnlyList<TemplateValidationIssue> Issues,
    string? Message = null)
{
    public static TemplateOperationResult Fail(string message, IReadOnlyList<TemplateValidationIssue>? issues = null) =>
        new(false, null, issues ?? [], message);
}

/// <summary>Asset ảnh vừa được thêm vào thư mục template.</summary>
public sealed record TemplateAsset(string AssetPath, int WidthPixels, int HeightPixels);

/// <summary>Lỗi nghiệp vụ của Template Library có thông báo hiển thị được cho quản trị viên.</summary>
public sealed class TemplateLibraryException(string message, Exception? innerException = null) : Exception(message, innerException);
