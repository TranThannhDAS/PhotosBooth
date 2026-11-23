namespace Photobooth.Application.Sharing;

/// <summary>
/// Mục "cloud" trong settings.json, ví dụ:
/// <c>{ "cloud": { "enabled": true, "baseUrl": "https://photo.example.com", "uploadTimeoutSeconds": 60, "apiKey": "…" } }</c>.
/// Không hardcode địa chỉ. Cấu hình sai KHÔNG làm hỏng các cấu hình khác: chỉ tắt chia sẻ QR và báo cho người vận hành.
/// </summary>
public sealed record CloudShareOptions
{
    public const int DefaultUploadTimeoutSeconds = 60;
    public const int MinUploadTimeoutSeconds = 5;
    public const int MaxUploadTimeoutSeconds = 600;

    public bool Enabled { get; init; }

    /// <summary>Địa chỉ công khai của Photobooth Cloud (QR trỏ tới {BaseUrl}/s/{token}).</summary>
    public string? BaseUrl { get; init; }

    public int UploadTimeoutSeconds { get; init; } = DefaultUploadTimeoutSeconds;

    /// <summary>API Key của thiết bị (header X-Api-Key) nếu Cloud bật bảo vệ upload. Chỉ đặt trong settings.json của máy, không đưa vào source code.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Kiểm tra cấu hình (không ném lỗi).</summary>
    public CloudShareConfiguration Describe()
    {
        if (!Enabled)
        {
            return new CloudShareConfiguration(false, null, "Chia sẻ qua QR đang tắt (cloud.enabled = false).");
        }

        if (string.IsNullOrWhiteSpace(BaseUrl)
            || !Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return new CloudShareConfiguration(false, null, "Địa chỉ Cloud (cloud.baseUrl) chưa hợp lệ — cần dạng https://ten-mien.");
        }

        if (UploadTimeoutSeconds is < MinUploadTimeoutSeconds or > MaxUploadTimeoutSeconds)
        {
            return new CloudShareConfiguration(false, null,
                $"Thời gian chờ upload (cloud.uploadTimeoutSeconds) phải từ {MinUploadTimeoutSeconds} đến {MaxUploadTimeoutSeconds} giây.");
        }

        return new CloudShareConfiguration(true, new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), null);
    }
}

/// <summary>Kết quả kiểm tra cấu hình chia sẻ.</summary>
/// <param name="IsReady">Đủ điều kiện tạo QR và upload.</param>
/// <param name="BaseUrl">Địa chỉ Cloud đã chuẩn hóa (kết thúc bằng "/").</param>
/// <param name="Problem">Lý do chưa sẵn sàng (hiển thị cho người vận hành).</param>
public sealed record CloudShareConfiguration(bool IsReady, Uri? BaseUrl, string? Problem);
