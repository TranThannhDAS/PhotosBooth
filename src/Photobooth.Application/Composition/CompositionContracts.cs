using Photobooth.Application.Templates;

namespace Photobooth.Application.Composition;

/// <summary>Bộ lọc màu cơ bản, chỉ áp dụng cho ô ảnh (không áp dụng cho khung/chữ).</summary>
public enum ColorFilter
{
    None,
    Warm,
    Cool,
    Bright,
    Pastel,
    SoftRose,
    GoldenHour,
    Film,
    Cinematic,
    Moody,
    Sepia,
    Grayscale,
}

/// <summary>Một ảnh gốc đưa vào ghép. <paramref name="Mirror"/> = lật ngang để khớp với hướng live preview lúc chụp.</summary>
public sealed record CompositionPhoto(string FilePath, bool Mirror);

/// <param name="Photos">Ảnh theo PhotoIndex (phần tử thứ i dùng cho các ô có PhotoIndex = i).</param>
/// <param name="LocalTimestamp">
/// Thời điểm thống nhất (giờ địa phương) cho {date}/{time}/{datetime} trong suốt lượt render — lấy từ dữ liệu phiên,
/// không đọc lại đồng hồ hệ thống khi render.
/// </param>
/// <param name="SlotCrops">
/// Manual Crop theo <b>chỉ số ô</b> trong <c>Template.Slots</c> (không theo photoIndex: hai ô dùng chung ảnh vẫn cắt khác nhau).
/// null hoặc phần tử null = Auto Crop theo template. Nếu có, số phần tử phải bằng số ô.
/// </param>
public sealed record CompositionRequest(
    Guid SessionId,
    PhotoTemplate Template,
    IReadOnlyList<CompositionPhoto> Photos,
    ColorFilter Filter,
    DateTimeOffset LocalTimestamp,
    IReadOnlyList<SlotCrop?>? SlotCrops = null);

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

    /// <summary>
    /// Render bố cục template bằng ĐÚNG pipeline ghép ảnh (nền, lớp theo zIndex, Auto Crop, bo góc, chữ, ảnh trang trí)
    /// nhưng không cần ảnh chụp: ô ảnh là ảnh mẫu có sẵn hoặc vùng giữ chỗ đánh số. Trả về PNG.
    /// Dùng cho "Xem thử" trong Template Editor và ảnh đại diện template. Không bao giờ đọc thumbnail.png.
    /// </summary>
    Task<byte[]> RenderTemplateSampleAsync(PhotoTemplate template, TemplateSampleContent content, int maxWidthPixels, CancellationToken cancellationToken = default);
}

/// <summary>Nội dung đặt vào ô ảnh khi render mẫu template.</summary>
public enum TemplateSampleContent
{
    /// <summary>Vùng giữ chỗ màu xám, ghi số thứ tự ảnh (photoIndex + 1).</summary>
    Placeholders,

    /// <summary>Ảnh mẫu tự sinh (khung hình 16:9 như webcam) để thấy đúng cách Auto Crop cắt ảnh.</summary>
    SamplePhotos,
}

/// <summary>Tạo ảnh xem trước thu nhỏ (JPEG) từ một file ảnh, có thể lật ngang và áp dụng bộ lọc màu.</summary>
public interface IImagePreviewRenderer
{
    Task<byte[]> RenderPreviewAsync(
        string imageFilePath,
        bool mirror,
        int maxWidthPixels,
        CancellationToken cancellationToken = default) =>
        RenderPreviewAsync(imageFilePath, mirror, maxWidthPixels, ColorFilter.None, cancellationToken);

    Task<byte[]> RenderPreviewAsync(
        string imageFilePath,
        bool mirror,
        int maxWidthPixels,
        ColorFilter filter,
        CancellationToken cancellationToken = default);
}
