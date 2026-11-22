namespace Photobooth.Application.Events;

/// <summary>Thông tin một sự kiện (lưu trong <c>event.json</c> ở gốc Event Workspace).</summary>
public sealed record EventInfo(Guid EventId, string Name, DateTimeOffset CreatedAt);

/// <summary>
/// Một sự kiện đã từng mở trên máy này, dùng cho bộ lọc của Thư viện ảnh.
/// <see cref="EventKey"/> là mã sự kiện (không phải đường dẫn): giao diện chỉ truyền mã, Infrastructure tự tra thư mục.
/// </summary>
/// <param name="IsCurrent">Sự kiện đang dùng để lưu ảnh của Kiosk.</param>
/// <param name="Problem">Không truy cập được (thư mục bị xóa/đổi tên, ổ đĩa chưa cắm...), null nếu dùng được.</param>
public sealed record KnownEvent(string EventKey, string Name, bool IsCurrent, DateTimeOffset LastOpenedAt, string? Problem)
{
    public bool IsAvailable => Problem is null;
}

/// <summary>
/// Không có Event Workspace hợp lệ (chưa chọn, thư mục bị xóa/đổi tên, mất quyền ghi...).
/// Ứng dụng không được âm thầm lưu ảnh sang chỗ khác khi gặp lỗi này.
/// </summary>
public sealed class EventWorkspaceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
