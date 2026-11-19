namespace Photobooth.Application.Export;

public enum ExportFormat
{
    Jpeg,
    Png,
    Pdf,
}

public enum ExportError
{
    Unknown = 0,

    /// <summary>Đường dẫn/tên file không hợp lệ.</summary>
    InvalidPath,

    /// <summary>Không tìm được tên file trống (đã thử nhiều hậu tố).</summary>
    AlreadyExists,

    DirectoryNotFound,

    AccessDenied,

    DiskFull,

    /// <summary>Không còn tìm thấy ảnh thành phẩm nguồn.</summary>
    SourceMissing,

    /// <summary>Không mã hóa được ảnh/PDF.</summary>
    EncodingFailed,

    /// <summary>Đang có một thao tác xuất khác chạy.</summary>
    Busy,

    /// <summary>Chưa có ảnh thành phẩm để xuất.</summary>
    NothingToSave,

    Cancelled,

    IoError,
}

/// <summary>Kết quả xuất file. Chỉ thành công khi file đích đã được ghi hoàn chỉnh.</summary>
public sealed record ExportResult
{
    private ExportResult(bool succeeded, string? filePath, ExportError? error)
    {
        Succeeded = succeeded;
        FilePath = filePath;
        Error = error;
    }

    public bool Succeeded { get; }

    public string? FilePath { get; }

    public ExportError? Error { get; }

    public static ExportResult Success(string filePath) => new(true, filePath, null);

    public static ExportResult Failure(ExportError error) => new(false, null, error);
}

/// <summary>Kết quả xuất của một định dạng (mỗi định dạng thành công/thất bại độc lập).</summary>
public sealed record FormatExportResult(ExportFormat Format, ExportResult Result);
