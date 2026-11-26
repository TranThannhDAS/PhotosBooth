using Microsoft.Extensions.Logging;

namespace Photobooth.Infrastructure.Logging;

/// <summary>
/// Mục "logging" của appsettings.json Desktop: ví dụ { "directory": "D:\\PhotoboothLogs", "minimumLevel": "Information" }.
/// Giá trị sai không làm mất các cấu hình khác: chỉ quay về mặc định cho riêng mục log và ghi cảnh báo.
/// </summary>
public sealed record LoggingOptions
{
    public const LogLevel DefaultMinimumLevel = LogLevel.Information;

    /// <summary>
    /// Thư mục chứa file log (photobooth-yyyyMMdd.log). Bỏ trống → %LOCALAPPDATA%\Photobooth\Logs.
    /// Cho phép biến môi trường (%LOCALAPPDATA%...). Đường dẫn tương đối tính từ thư mục dữ liệu %LOCALAPPDATA%\Photobooth,
    /// không phải thư mục cài đặt (có thể là Program Files, không ghi được).
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>Mức log tối thiểu: Trace, Debug, Information, Warning, Error, Critical. Bỏ trống → Information.</summary>
    public string? MinimumLevel { get; init; }

    /// <summary>Đọc mức log; giá trị không hợp lệ → <see cref="DefaultMinimumLevel"/> kèm mô tả vấn đề.</summary>
    public LogLevel ResolveMinimumLevel(out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(MinimumLevel))
        {
            return DefaultMinimumLevel;
        }

        if (Enum.TryParse<LogLevel>(MinimumLevel.Trim(), ignoreCase: true, out var level)
            && Enum.IsDefined(level)
            && level != LogLevel.None
            && !int.TryParse(MinimumLevel, out _))
        {
            return level;
        }

        problem = $"Mức log \"{MinimumLevel}\" không hợp lệ (dùng Trace, Debug, Information, Warning, Error hoặc Critical); dùng {DefaultMinimumLevel}.";
        return DefaultMinimumLevel;
    }

    /// <summary>
    /// Xác định thư mục log và kiểm tra ghi được. Không dùng được (sai đường dẫn, thiếu quyền, ổ không tồn tại)
    /// → <paramref name="defaultDirectory"/> kèm mô tả vấn đề, để ứng dụng vẫn có log.
    /// </summary>
    public string ResolveDirectory(string dataRootDirectory, string defaultDirectory, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(Directory))
        {
            return defaultDirectory;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(Directory.Trim());
            if (Path.IsPathRooted(expanded) && !Path.IsPathFullyQualified(expanded))
            {
                // Dạng "C:logs" hoặc "\logs" phụ thuộc thư mục hiện hành → không rõ ràng.
                throw new ArgumentException("Đường dẫn phải là tuyệt đối (ví dụ D:\\PhotoboothLogs) hoặc tương đối không có ổ đĩa.");
            }

            var fullPath = Path.GetFullPath(Path.IsPathFullyQualified(expanded) ? expanded : Path.Combine(dataRootDirectory, expanded));
            EnsureWritable(fullPath);
            return fullPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = $"Không dùng được thư mục log \"{Directory}\" ({ex.Message}); ghi log vào thư mục mặc định.";
            return defaultDirectory;
        }
    }

    private static void EnsureWritable(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
        using (File.Create(probe, bufferSize: 1, FileOptions.DeleteOnClose))
        {
        }
    }
}
