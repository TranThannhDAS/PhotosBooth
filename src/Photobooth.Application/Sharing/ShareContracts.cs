namespace Photobooth.Application.Sharing;

/// <summary>
/// Link chia sẻ (QR) của MỘT phiên bản ảnh thành phẩm. Sinh trên Desktop, không cần Internet; không bao giờ đổi
/// sau khi đã lưu (upload lại, in lại, mở lại phiên đều dùng đúng token này).
/// </summary>
/// <param name="Token">Share Token: 192 bit ngẫu nhiên, Base64URL 32 ký tự.</param>
/// <param name="Url">URL công khai trong QR: {Cloud.BaseUrl}/s/{Token}.</param>
public sealed record ShareLink(string Token, string Url, DateTimeOffset CreatedAt);

/// <summary>Trạng thái đồng bộ ảnh lên Cloud.</summary>
public enum CloudUploadStatus
{
    /// <summary>Chờ upload (lần đầu hoặc chờ thử lại sau lỗi tạm thời như mất mạng).</summary>
    Pending,

    /// <summary>Đang upload.</summary>
    Uploading,

    /// <summary>Cloud đã có ảnh — link trong QR mở được ảnh.</summary>
    Uploaded,

    /// <summary>Lỗi không tự hết (mất file local, Cloud báo trùng token khác ảnh, ảnh bị từ chối): không tự thử lại.</summary>
    Failed,
}

/// <summary>Trạng thái upload của một link chia sẻ.</summary>
/// <param name="LastError">Mô tả lỗi gần nhất (tiếng Việt, không chứa đường dẫn/token).</param>
/// <param name="IsOffline">Lỗi gần nhất là do không kết nối được Cloud (mất mạng, Cloud không phản hồi).</param>
public sealed record ShareUploadState(
    CloudUploadStatus Status,
    int Attempts,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? UploadedAt,
    string? LastError,
    bool IsOffline);

/// <summary>Một ảnh thành phẩm cần đồng bộ lên Cloud.</summary>
/// <param name="SessionDirectory">Thư mục phiên trong Event Workspace (nơi ghi trạng thái upload).</param>
/// <param name="FilePath">File phiên bản thành phẩm (Outputs/final-v{N}.png) — KHÔNG bao giờ là ảnh gốc.</param>
public sealed record ShareUploadItem(Guid SessionId, string SessionDirectory, int Version, string FilePath, ShareLink Link);

/// <summary>Tổng hợp đồng bộ cho màn quản trị.</summary>
public sealed record ShareUploadSummary(int Pending, int Uploading, int Uploaded, int Failed, string? LastError, DateTimeOffset? LastScanAt);

/// <summary>
/// Hàng đợi upload ảnh chia sẻ chạy nền, độc lập với UI: không chặn QR, in ảnh hay kết thúc phiên.
/// Trạng thái được ghi xuống thư mục phiên để khôi phục sau khi khởi động lại.
/// </summary>
public interface ICloudUploadQueue
{
    /// <summary>Phát sinh khi trạng thái upload thay đổi (có thể từ luồng nền).</summary>
    event EventHandler? StateChanged;

    /// <summary>Đưa ảnh vào hàng đợi (gọi lại nhiều lần với cùng token là an toàn — không upload trùng).</summary>
    void Enqueue(ShareUploadItem item);

    /// <summary>Trạng thái hiện tại theo Share Token (null = chưa biết).</summary>
    ShareUploadState? GetState(string token);
}
