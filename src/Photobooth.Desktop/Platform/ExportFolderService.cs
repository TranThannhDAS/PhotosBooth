using System.IO;
using Photobooth.Application.Export;
using Photobooth.Application.Sessions;

namespace Photobooth.Desktop.Platform;

/// <summary>
/// Thư mục lưu file xuất (JPEG/PNG/PDF). Mặc định nằm TRONG thư mục của chính phiên chụp được xuất —
/// <c>{thư mục sự kiện}\Sessions\{phiên}\Exports</c> — nên file xuất đi cùng đúng phiên và đúng sự kiện
/// (sự kiện tạo ở màn khởi động; phiên mở lại từ sự kiện khác vẫn lưu vào thư mục của sự kiện đó).
/// Thứ tự ưu tiên:
/// <list type="number">
/// <item>Thư mục quản trị viên vừa chọn bằng "Đổi thư mục" — chỉ cho đúng phiên đó, trong lần chạy này.</item>
/// <item><c>{thư mục phiên}\Exports</c> (tạo khi xuất).</item>
/// <item>Không có phiên/thư mục phiên: <see cref="ExportOptions.DefaultFolder"/> nếu cấu hình và tồn tại, ngược lại thư mục Ảnh (Pictures).</item>
/// </list>
/// </summary>
public sealed class ExportFolderService
{
    /// <summary>Thư mục con chứa file xuất trong thư mục phiên.</summary>
    public const string SessionExportsFolderName = "Exports";

    private readonly string _fallbackFolder;
    private Guid? _customForSession;
    private string? _customFolder;

    public ExportFolderService(ExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _fallbackFolder = !string.IsNullOrWhiteSpace(options.DefaultFolder) && Directory.Exists(options.DefaultFolder)
            ? options.DefaultFolder
            : GetSystemPicturesFolder();
    }

    /// <summary>Thư mục sẽ lưu file xuất của <paramref name="session"/> (chỉ tính đường dẫn, không tạo thư mục).</summary>
    public string GetFolder(PhotoSession? session)
    {
        if (session is not null && _customFolder is { } custom && _customForSession == session.SessionId)
        {
            return custom;
        }

        return session?.Directory is { } directory
            ? Path.Combine(directory, SessionExportsFolderName)
            : _fallbackFolder;
    }

    /// <summary>
    /// Thư mục dùng để xuất ngay bây giờ: như <see cref="GetFolder"/>, tạo <c>Exports</c> của phiên nếu chưa có.
    /// Không tạo được thì vẫn trả đường dẫn đó để lần xuất báo lỗi rõ ràng (không âm thầm lưu sang nơi khác).
    /// </summary>
    public string PrepareFolder(PhotoSession? session)
    {
        var folder = GetFolder(session);
        if (session?.Directory is not null && string.Equals(folder, Path.Combine(session.Directory, SessionExportsFolderName), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Bộ xuất ảnh sẽ báo lỗi thư mục không ghi được.
            }
        }

        return folder;
    }

    /// <summary>Mở hộp thoại chọn thư mục cho phiên <paramref name="session"/>. Trả về false nếu người dùng đóng hộp thoại.</summary>
    public async Task<bool> PickFolderAsync(PhotoSession? session)
    {
        var initial = GetFolder(session);
        var selected = await FolderPicker.PickAsync(
            "Chọn thư mục lưu ảnh",
            Directory.Exists(initial) ? initial : session?.Directory ?? _fallbackFolder);

        if (string.IsNullOrWhiteSpace(selected) || session is null)
        {
            return false;
        }

        _customFolder = selected;
        _customForSession = session.SessionId;
        return true;
    }

    private static string GetSystemPicturesFolder()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return string.IsNullOrEmpty(pictures)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : pictures;
    }
}
