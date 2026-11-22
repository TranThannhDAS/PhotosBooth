using System.IO;
using Photobooth.Application.Export;

namespace Photobooth.Desktop.Platform;

/// <summary>
/// Thư mục lưu file xuất do người dùng chọn (hộp thoại chọn thư mục của Windows).
/// Ghi nhớ trong suốt thời gian chạy ứng dụng; mặc định là <see cref="ExportOptions.DefaultFolder"/>
/// nếu được cấu hình và tồn tại, ngược lại là thư mục Ảnh (Pictures).
/// </summary>
public sealed class ExportFolderService
{
    private readonly string _defaultFolder;

    public ExportFolderService(ExportOptions options)
    {
        _defaultFolder = !string.IsNullOrWhiteSpace(options.DefaultFolder) && Directory.Exists(options.DefaultFolder)
            ? options.DefaultFolder
            : GetSystemPicturesFolder();
        CurrentFolder = _defaultFolder;
    }

    public string CurrentFolder { get; private set; }

    /// <summary>Mở hộp thoại chọn thư mục. Trả về false nếu người dùng đóng hộp thoại.</summary>
    public async Task<bool> PickFolderAsync()
    {
        var selected = await FolderPicker.PickAsync(
            "Chọn thư mục lưu ảnh",
            Directory.Exists(CurrentFolder) ? CurrentFolder : _defaultFolder);

        if (string.IsNullOrWhiteSpace(selected))
        {
            return false;
        }

        CurrentFolder = selected;
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
