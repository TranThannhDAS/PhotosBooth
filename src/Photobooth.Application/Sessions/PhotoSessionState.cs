namespace Photobooth.Application.Sessions;

/// <summary>Trạng thái phiên chụp (xem BUSINESS_RULES.md mục 5).</summary>
public enum PhotoSessionState
{
    /// <summary>Phiên vừa tạo: đang chọn template hoặc đã chọn và đang chờ camera.</summary>
    Created,

    /// <summary>Camera sẵn sàng, chờ khách bắt đầu (hoặc tiếp tục/chụp lại) lượt chụp.</summary>
    Previewing,

    /// <summary>Đang chạy lượt chụp (đếm ngược/chụp/ghi ảnh gốc). Không nhận lệnh chụp khác.</summary>
    Capturing,

    /// <summary>Đã đủ ảnh, đang xem lại.</summary>
    Reviewing,

    /// <summary>Đang ghép ảnh thành phẩm.</summary>
    Composing,

    /// <summary>Có ảnh thành phẩm, có thể đổi bộ lọc và xuất file.</summary>
    Ready,

    /// <summary>Phiên kết thúc sau khi đã xuất file thành công ít nhất một lần.</summary>
    Completed,

    /// <summary>Phiên kết thúc mà chưa xuất file. Ảnh gốc vẫn được giữ trên ổ đĩa.</summary>
    Cancelled,
}
