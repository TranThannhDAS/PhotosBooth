using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Photobooth.Desktop.Platform;

/// <summary>
/// Các thao tác shell Windows được phép: mở trang cài đặt quyền camera và mở thư mục chứa
/// file vừa lưu. Không mở URL ngoài.
/// </summary>
public sealed class WindowsShellService
{
    private const string CameraPrivacySettingsUri = "ms-settings:privacy-webcam";

    private readonly ILogger<WindowsShellService> _logger;

    public WindowsShellService(ILogger<WindowsShellService> logger)
    {
        _logger = logger;
    }

    /// <summary>Mở Settings › Privacy &amp; security › Camera.</summary>
    public bool OpenCameraPrivacySettings() =>
        TryStart(new ProcessStartInfo(CameraPrivacySettingsUri) { UseShellExecute = true }, "mở cài đặt quyền camera");

    /// <summary>Mở File Explorer và chọn sẵn file. Chỉ dùng cho file vừa được ứng dụng lưu.</summary>
    public bool RevealFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        startInfo.ArgumentList.Add($"/select,{Path.GetFullPath(filePath)}");
        return TryStart(startInfo, "mở thư mục chứa ảnh");
    }

    private bool TryStart(ProcessStartInfo startInfo, string action)
    {
        try
        {
            using var process = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Không thể {Action}", action);
            return false;
        }
    }
}

