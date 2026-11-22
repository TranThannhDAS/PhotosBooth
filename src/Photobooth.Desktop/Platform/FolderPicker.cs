using System.IO;
using Microsoft.Win32;

namespace Photobooth.Desktop.Platform;

/// <summary>Hộp thoại chọn thư mục native của Windows (có sẵn nút "New folder" để tạo thư mục mới).</summary>
public static class FolderPicker
{
    /// <summary>Trả về thư mục đã chọn, hoặc null nếu người dùng đóng hộp thoại.</summary>
    public static async Task<string?> PickAsync(string title, string? initialDirectory)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        var selected = await dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = title,
                Multiselect = false,
            };

            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            var owner = System.Windows.Application.Current.MainWindow;
            var accepted = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            return accepted == true ? dialog.FolderName : null;
        });

        return string.IsNullOrWhiteSpace(selected) ? null : selected;
    }
}
