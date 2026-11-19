namespace Photobooth.Infrastructure.Storage;

/// <summary>
/// Ghi file theo kiểu "ghi tạm rồi đổi tên": file đích chỉ xuất hiện khi nội dung đã được ghi
/// và flush hoàn chỉnh. File tạm được dọn khi có lỗi.
/// </summary>
internal static class SafeFileWriter
{
    private const int BufferSize = 81920;

    public static async Task WriteAsync(
        string destinationPath,
        Func<Stream, CancellationToken, Task> writeContent,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("Đường dẫn đích không có thư mục.", nameof(destinationPath));

        // File tạm cùng thư mục để thao tác đổi tên không phải sao chép qua ổ đĩa khác.
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                BufferSize = BufferSize,
            };

            await using (var stream = new FileStream(tempPath, options))
            {
                await writeContent(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, destinationPath, overwrite);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Không che lỗi gốc; file tạm có tên bắt đầu bằng "." và đuôi .tmp, có thể dọn sau.
            System.Diagnostics.Debug.WriteLine($"Không xóa được file tạm: {ex.Message}");
        }
    }
}

