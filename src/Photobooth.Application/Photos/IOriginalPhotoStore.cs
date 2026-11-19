namespace Photobooth.Application.Photos;

/// <summary>Ảnh gốc đã được ghi hoàn chỉnh vào vùng dữ liệu ứng dụng.</summary>
public sealed record StoredOriginal(string FilePath, long SizeBytes);

/// <summary>
/// Lưu trữ ảnh gốc của phiên chụp. Ảnh gốc là bất biến: không ghi đè, không chỉnh sửa.
/// </summary>
public interface IOriginalPhotoStore
{
    /// <summary>
    /// Ghi ảnh gốc. Chỉ trả về khi file đã được ghi hoàn chỉnh; ném exception I/O nếu thất bại.
    /// </summary>
    Task<StoredOriginal> SaveOriginalAsync(
        Guid sessionId,
        Guid photoId,
        int sequence,
        ReadOnlyMemory<byte> jpegData,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadOriginalAsync(string filePath, CancellationToken cancellationToken = default);
}

