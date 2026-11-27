using System.Collections.Concurrent;

namespace Photobooth.Infrastructure.Storage;

/// <summary>
/// Vị trí dữ liệu cục bộ của ứng dụng. Mặc định: %LOCALAPPDATA%\Photobooth
/// (không ghi dữ liệu vào thư mục cài đặt/build).
/// Thư mục của từng phiên chụp nằm trong Event Workspace và được đăng ký qua <see cref="RegisterSessionDirectory"/>.
/// </summary>
public sealed class PhotoboothStoragePaths
{
    private readonly ConcurrentDictionary<Guid, string> _sessionDirectories = new();
    private readonly ConcurrentDictionary<string, byte> _allowedRoots = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Thư mục phiên: thư mục đã đăng ký trong Event Workspace; phiên chưa đăng ký (chỉ dùng trong kiểm thử/dữ liệu cũ)
    /// nằm dưới <see cref="SessionsDirectory"/>.
    /// </summary>
    public string GetSessionDirectory(Guid sessionId) =>
        _sessionDirectories.TryGetValue(sessionId, out var directory)
            ? directory
            : Path.Combine(SessionsDirectory, sessionId.ToString("N"));

    /// <summary>Gắn phiên với thư mục của nó trong Event Workspace (cố định suốt vòng đời phiên).</summary>
    public void RegisterSessionDirectory(Guid sessionId, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("Thư mục phiên phải là đường dẫn tuyệt đối.", nameof(directory));
        }

        _sessionDirectories[sessionId] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    /// <summary>Cho phép đọc ảnh trong một Event Workspace (thư mục gốc của sự kiện).</summary>
    public void AllowRoot(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory))
        {
            _allowedRoots.TryAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), 0);
        }
    }

    /// <summary>File nháp (bản ghép tạm khi đang chỉnh sửa), tách khỏi phiên bản thành phẩm đã xác nhận.</summary>
    public string GetDraftsDirectory(Guid sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "Drafts");

    public string GetOriginalsDirectory(Guid sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "Originals");

    /// <summary>Ảnh thành phẩm (bản phát sinh), tách riêng khỏi ảnh gốc.</summary>
    public string GetOutputsDirectory(Guid sessionId) =>
        Path.Combine(GetSessionDirectory(sessionId), "Outputs");

    /// <summary>SQLite chứa metadata nghiệp vụ và cấu hình thiết bị (máy in, webcam, vùng hướng dẫn).</summary>
    public string DatabaseFilePath => Path.Combine(RootDirectory, "photobooth.db");

    /// <summary>File cấu hình Desktop cạnh file thực thi, không nằm trong AppData.</summary>
    public string SettingsFilePath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>
    /// Kiểm tra đường dẫn nằm trong thư mục dữ liệu ứng dụng, một Event Workspace đã mở hoặc thư mục phiên đã đăng ký
    /// (chống path traversal / đọc file tùy ý).
    /// </summary>
    public bool IsInsideRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        return IsUnder(fullPath, RootDirectory)
            || _allowedRoots.Keys.Any(root => IsUnder(fullPath, root))
            || _sessionDirectories.Values.Any(root => IsUnder(fullPath, root));
    }

    private static bool IsUnder(string fullPath, string root) =>
        fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

