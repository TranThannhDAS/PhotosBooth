namespace Photobooth.Application.Export;

/// <summary>Cấu hình xuất file.</summary>
public sealed record ExportOptions
{
    /// <summary>Chất lượng JPEG/ảnh trong PDF (1..100). 92 là giá trị tạm thời — giá trị chính thức CHƯA CHỐT.</summary>
    public int JpegQuality { get; init; } = 92;

    /// <summary>Thư mục lưu mặc định (đường dẫn tuyệt đối). Bỏ trống = thư mục Ảnh (Pictures) của Windows.</summary>
    public string? DefaultFolder { get; init; }

    public void Validate()
    {
        if (!string.IsNullOrWhiteSpace(DefaultFolder) && !Path.IsPathFullyQualified(DefaultFolder))
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultFolder), DefaultFolder, "Thư mục lưu mặc định phải là đường dẫn tuyệt đối.");
        }

        if (JpegQuality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(JpegQuality), JpegQuality, "Chất lượng JPEG phải trong khoảng 1..100.");
        }
    }
}
