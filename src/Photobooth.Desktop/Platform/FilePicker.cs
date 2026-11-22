using System.IO;
using Microsoft.Win32;

namespace Photobooth.Desktop.Platform;

/// <summary>Hộp thoại mở/lưu file native của Windows (quản trị viên chọn file, không phải gõ đường dẫn).</summary>
public static class FilePicker
{
    public const string ImageFilter = "Ảnh (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif";
    public const string ZipFilter = "Gói template (*.zip)|*.zip";

    /// <summary>Trả về file đã chọn, hoặc null nếu người dùng đóng hộp thoại.</summary>
    public static Task<string?> OpenAsync(string title, string filter) => ShowAsync(new OpenFileDialog
    {
        Title = title,
        Filter = filter,
        Multiselect = false,
        CheckFileExists = true,
    });

    /// <summary>Chọn nơi lưu file (hộp thoại tự hỏi trước khi ghi đè file đã có).</summary>
    public static Task<string?> SaveAsync(string title, string filter, string defaultFileName, string defaultExtension) => ShowAsync(new SaveFileDialog
    {
        Title = title,
        Filter = filter,
        FileName = defaultFileName,
        DefaultExt = defaultExtension,
        AddExtension = true,
        OverwritePrompt = true,
    });

    private static async Task<string?> ShowAsync(FileDialog dialog)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        var selected = await dispatcher.InvokeAsync(() =>
        {
            var owner = System.Windows.Application.Current.MainWindow;
            var accepted = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            return accepted == true ? dialog.FileName : null;
        });

        return string.IsNullOrWhiteSpace(selected) || !Path.IsPathFullyQualified(selected) ? null : selected;
    }
}
