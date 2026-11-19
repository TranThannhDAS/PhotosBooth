namespace Photobooth.Infrastructure.Storage;

/// <summary>
/// Vị trí dữ liệu cục bộ của ứng dụng. Mặc định: %LOCALAPPDATA%\Photobooth
/// (không ghi dữ liệu vào thư mục cài đặt/build).
/// </summary>
public sealed class PhotoboothStoragePaths
{
    public PhotoboothStoragePaths(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("Thư mục gốc phải là đường dẫn tuyệt đối.", nameof(rootDirectory));
        }

        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
    }

    public string RootDirectory { get; }

    public string SessionsDirectory => Path.Combine(RootDirectory, "Sessions");

    public string LogsDirectory => Path.Combine(RootDirectory, "Logs");

    /// <summary>Dữ liệu hồ sơ WebView2 (cache, quyền...). Không đặt cạnh file .exe để tránh lỗi quyền ghi khi cài vào Program Files.</summary>
    public string WebViewDataDirectory => Path.Combine(RootDirectory, "WebView2");

    public static PhotoboothStoragePaths CreateDefault() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Photobooth"));

    public string GetSessionDirectory(Guid sessionId) =>
        Path.Combine(SessionsDirectory, sessionId.ToString("N"));

    public string GetOriginalsDirectory(Guid sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "Originals");

    /// <summary>Ảnh thành phẩm (bản phát sinh), tách riêng khỏi ảnh gốc.</summary>
    public string GetOutputsDirectory(Guid sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "Outputs");

    /// <summary>File cấu hình tùy chọn (không bắt buộc tồn tại).</summary>
    public string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>Kiểm tra đường dẫn nằm trong thư mục dữ liệu ứng dụng (chống path traversal).</summary>
    public bool IsInsideRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var rootWithSeparator = RootDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}

