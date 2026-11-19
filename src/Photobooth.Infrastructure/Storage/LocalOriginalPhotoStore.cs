using Photobooth.Application.Photos;

namespace Photobooth.Infrastructure.Storage;

/// <summary>
/// Lưu ảnh gốc vào Sessions/{SessionId}/Originals. Mỗi ảnh có tên duy nhất theo PhotoId,
/// không bao giờ ghi đè ảnh đã có (kể cả khi chụp lại).
/// </summary>
public sealed class LocalOriginalPhotoStore : IOriginalPhotoStore
{
    private readonly PhotoboothStoragePaths _paths;

    public LocalOriginalPhotoStore(PhotoboothStoragePaths paths)
    {
        _paths = paths;
    }

    public async Task<StoredOriginal> SaveOriginalAsync(
        Guid sessionId,
        Guid photoId,
        int sequence,
        ReadOnlyMemory<byte> jpegData,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        if (jpegData.IsEmpty)
        {
            throw new ArgumentException("Dữ liệu ảnh rỗng.", nameof(jpegData));
        }

        var directory = _paths.GetOriginalsDirectory(sessionId);
        Directory.CreateDirectory(directory);

        var filePath = Path.Combine(directory, $"photo-{sequence:D3}-{photoId:N}.jpg");

        await SafeFileWriter.WriteAsync(
                filePath,
                (stream, token) => stream.WriteAsync(jpegData, token).AsTask(),
                overwrite: false,
                cancellationToken)
            .ConfigureAwait(false);

        return new StoredOriginal(filePath, jpegData.Length);
    }

    public Task<byte[]> ReadOriginalAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!_paths.IsInsideRoot(filePath))
        {
            throw new UnauthorizedAccessException("Chỉ được đọc ảnh nằm trong thư mục dữ liệu của ứng dụng.");
        }

        return File.ReadAllBytesAsync(filePath, cancellationToken);
    }
}

