namespace Photobooth.Application.Camera;

/// <summary>
/// Nhóm lỗi camera đã được chuẩn hóa, độc lập với công nghệ (WebView2, native, SDK máy ảnh).
/// </summary>
public enum CameraError
{
    Unknown = 0,

    /// <summary>Không tìm thấy thiết bị camera nào.</summary>
    NotFound,

    /// <summary>Người dùng hoặc Windows từ chối quyền truy cập camera.</summary>
    PermissionDenied,

    /// <summary>Camera đang bị ứng dụng khác chiếm dụng hoặc không đọc được.</summary>
    InUse,

    /// <summary>Camera bị ngắt kết nối khi đang hoạt động.</summary>
    Disconnected,

    /// <summary>Môi trường hiện tại không hỗ trợ truy cập camera.</summary>
    NotSupported,

    /// <summary>Yêu cầu chụp khi camera chưa sẵn sàng.</summary>
    NotActive,

    /// <summary>Không mã hóa/đọc được khung hình chụp.</summary>
    CaptureFailed,

    /// <summary>Không tìm thấy camera đã được chọn trong Quản trị (bị rút hoặc đổi). Không tự chuyển sang camera khác.</summary>
    SelectedDeviceNotFound,
}

