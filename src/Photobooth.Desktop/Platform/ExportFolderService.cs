using System.IO;
using Microsoft.Win32;
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
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        var selected = await dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Chọn thư mục lưu ảnh",
                Multiselect = false,
                InitialDirectory = Directory.Exists(CurrentFolder) ? CurrentFolder : _defaultFolder,
            };

            var owner = System.Windows.Application.Current.MainWindow;
            var accepted = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            return accepted == true ? dialog.FolderName : null;
        });

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
