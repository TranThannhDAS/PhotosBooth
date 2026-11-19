using Photobooth.Application.Templates;

namespace Photobooth.Application.Composition;

/// <summary>Bộ lọc màu cơ bản, chỉ áp dụng cho ô ảnh (không áp dụng cho khung/chữ).</summary>
public enum ColorFilter
{
    None,
    Grayscale,
    Sepia,
    Warm,
}

/// <summary>Một ảnh gốc đưa vào ghép. <paramref name="Mirror"/> = lật ngang để khớp với hướng live preview lúc chụp.</summary>
public sealed record CompositionPhoto(string FilePath, bool Mirror);

/// <param name="Photos">Ảnh theo PhotoIndex (phần tử thứ i dùng cho các ô có PhotoIndex = i).</param>
/// <param name="LocalTimestamp">
/// Thời điểm thống nhất (giờ địa phương) cho {date}/{time}/{datetime} trong suốt lượt render — lấy từ dữ liệu phiên,
/// không đọc lại đồng hồ hệ thống khi render.
/// </param>
public sealed record CompositionRequest(
    Guid SessionId,
    PhotoTemplate Template,
    IReadOnlyList<CompositionPhoto> Photos,
    ColorFilter Filter,
    DateTimeOffset LocalTimestamp);

/// <summary>Ảnh thành phẩm (PNG không nén mất dữ liệu) dùng làm nguồn chung cho mọi định dạng xuất.</summary>
public sealed record ComposedImage(string FilePath, int WidthPixels, int HeightPixels);

/// <summary>Lỗi ghép ảnh có thể giải thích (ảnh hỏng, template không hợp lệ...).</summary>
public sealed class CompositionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Engine ghép ảnh theo template. Độc lập với UI và camera; chỉ làm việc với file ảnh gốc.
/// </summary>
public interface IPhotoComposer
{
    /// <summary>Ghép và ghi ảnh thành phẩm mới vào thư mục Outputs của phiên. Không sửa ảnh gốc.</summary>
    /// <exception cref="CompositionException">Template/ảnh không hợp lệ.</exception>
    Task<ComposedImage> ComposeAsync(CompositionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ảnh xem trước bố cục template dạng JPEG: dùng thumbnail.png trong thư mục template nếu có,
    /// ngược lại render bố cục với ô ảnh là vùng giữ chỗ.
    /// </summary>
    Task<byte[]> RenderTemplatePreviewAsync(PhotoTemplate template, int maxWidthPixels, CancellationToken cancellationToken = default);
}

/// <summary>Tạo ảnh xem trước thu nhỏ (JPEG) từ một file ảnh, có thể lật ngang.</summary>
public interface IImagePreviewRenderer
{
    Task<byte[]> RenderPreviewAsync(string imageFilePath, bool mirror, int maxWidthPixels, CancellationToken cancellationToken = default);
}
