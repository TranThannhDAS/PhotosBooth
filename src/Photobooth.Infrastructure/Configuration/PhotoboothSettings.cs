using System.Text.Json;
using Microsoft.Extensions.Logging;
using Photobooth.Application.Export;
using Photobooth.Application.Printing;
using Photobooth.Application.Sessions;
using Photobooth.Application.Sharing;

namespace Photobooth.Infrastructure.Configuration;

/// <summary>
/// Cấu hình Desktop đọc từ appsettings.json cạnh file thực thi (không bắt buộc tồn tại).
/// Ví dụ: { "capture": { "maxPhotosPerSession": 10, "countdownSeconds": 3, "mirrorByDefault": true }, "export": { "jpegQuality": 92 } }
/// Số ảnh mỗi phiên do template quyết định; <c>maxPhotosPerSession</c> chỉ là giới hạn an toàn.
/// </summary>
public sealed record PhotoboothSettings
{
    public CaptureOptions Capture { get; init; } = new();

    public ExportOptions Export { get; init; } = new();

    /// <summary>Mục "print": ví dụ { "maxCopies": 10 }.</summary>
    public PrintOptions Print { get; init; } = new();

    /// <summary>
    /// Mục "cloud" (chia sẻ ảnh qua QR): ví dụ { "enabled": true, "baseUrl": "https://photo.example.com", "uploadTimeoutSeconds": 60 }.
    /// Mặc định tắt. Không được Validate() kiểm tra: cấu hình sai chỉ tắt chia sẻ QR, không làm mất các cấu hình khác.
    /// </summary>
    public CloudShareOptions Cloud { get; init; } = new();

    public void Validate()
    {
        Capture.Validate();
        Export.Validate();
        Print.Validate();
    }

    /// <summary>Đọc cấu hình; thiếu file → mặc định; file lỗi/không hợp lệ → ghi log và dùng mặc định.</summary>
    public static PhotoboothSettings Load(string filePath, ILogger logger)
    {
        if (!File.Exists(filePath))
        {
            return new PhotoboothSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<PhotoboothSettings>(File.ReadAllText(filePath), SerializerOptions)
                ?? new PhotoboothSettings();
            settings.Validate();
            logger.LogInformation(
                "Đã đọc cấu hình: tối đa {MaxPhotoCount} ảnh/phiên, đếm ngược {Countdown}s, gương mặc định {Mirror}",
                settings.Capture.MaxPhotosPerSession, settings.Capture.CountdownSeconds, settings.Capture.MirrorByDefault);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            logger.LogWarning(ex, "File cấu hình không hợp lệ, dùng cấu hình mặc định");
            return new PhotoboothSettings();
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
