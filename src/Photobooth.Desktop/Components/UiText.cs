using System.Globalization;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Sessions;
using Photobooth.Application.Templates;

namespace Photobooth.Desktop.Components;

/// <summary>
/// Ánh xạ mã lỗi nghiệp vụ sang thông điệp tiếng Việt ngắn gọn, có hướng dẫn hành động.
/// Không hiển thị exception, stack trace hay đường dẫn hệ thống cho khách.
/// </summary>
public static class UiText
{
    public static readonly string[] StepLabels = ["Chọn khung", "Chụp ảnh", "Xem lại", "Thành phẩm", "Hoàn thành"];

    public static StatusInfo CameraConnecting { get; } =
        new(StatusKind.Info, "Đang kết nối máy ảnh…", "Nếu Windows hỏi quyền sử dụng camera, vui lòng chọn Cho phép.");

    public static StatusInfo CameraReady { get; } =
        new(StatusKind.Success, "Máy ảnh đã sẵn sàng", "Tạo dáng thật đẹp rồi nhấn nút chụp.");

    public static StatusInfo CameraOff { get; } =
        new(StatusKind.Info, "Máy ảnh đang tắt");

    public static StatusInfo Capturing { get; } =
        new(StatusKind.Info, "Đang chụp…", "Giữ nguyên tư thế và nhìn vào camera.");

    public static StatusInfo CompositionFailed { get; } =
        new(StatusKind.Error, "Không thể ghép ảnh lúc này.", "Ảnh đã chụp vẫn được giữ nguyên. Hãy thử lại.");

    public static StatusInfo UnexpectedError { get; } =
        new(StatusKind.Error, "Đã xảy ra lỗi. Vui lòng thử lại.", "Ảnh đã chụp vẫn được giữ nguyên.");

    public static string PhotoLabel(int position, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"Ảnh {position + 1}/{count}");

    public static string PrintStatusLabel(Photobooth.Application.Printing.PrintJobStatus status) => status switch
    {
        Photobooth.Application.Printing.PrintJobStatus.Pending => "Chưa rõ trạng thái",
        Photobooth.Application.Printing.PrintJobStatus.Submitted => "Windows đã tiếp nhận",
        Photobooth.Application.Printing.PrintJobStatus.Simulated => "Giả lập (không in thật)",
        Photobooth.Application.Printing.PrintJobStatus.Completed => "Đã in xong (đã xác nhận)",
        Photobooth.Application.Printing.PrintJobStatus.Failed => "Thất bại",
        Photobooth.Application.Printing.PrintJobStatus.Cancelled => "Đã hủy",
        _ => status.ToString(),
    };

    public static string SessionStateLabel(PhotoSessionState state, bool hasOutputVersion) => state switch
    {
        PhotoSessionState.Completed => "Đã hoàn thành",
        PhotoSessionState.Ready when hasOutputVersion => "Đã có thành phẩm",
        PhotoSessionState.Ready => "Chưa có thành phẩm",
        PhotoSessionState.Reviewing => "Chưa ghép thành phẩm",
        PhotoSessionState.Composing => "Đang ghép ảnh",
        PhotoSessionState.Capturing => "Đang chụp",
        PhotoSessionState.Previewing => "Đang xem trước",
        PhotoSessionState.Cancelled => "Đã hủy",
        PhotoSessionState.Created => "Mới tạo",
        _ => state.ToString(),
    };

    public static string LocalTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Nhãn ô ảnh: số thứ tự ô (theo template) và ảnh nguồn mà ô dùng — tránh nhầm ô với photoIndex.</summary>
    public static string SlotLabel(int slotIndex, TemplateSlot slot) =>
        string.Create(CultureInfo.InvariantCulture, $"Ô {slotIndex + 1} (ảnh {slot.PhotoIndex + 1})");

    public static string FilterLabel(ColorFilter filter) => filter switch
    {
        ColorFilter.Warm => "Ấm áp",
        ColorFilter.Cool => "Mát dịu",
        ColorFilter.Bright => "Tươi sáng",
        ColorFilter.Pastel => "Dịu nhẹ",
        ColorFilter.SoftRose => "Hồng phấn",
        ColorFilter.GoldenHour => "Nắng vàng",
        ColorFilter.Film => "Màu phim",
        ColorFilter.Cinematic => "Điện ảnh",
        ColorFilter.Moody => "Trầm",
        ColorFilter.Sepia => "Hoài cổ",
        ColorFilter.Grayscale => "Đen trắng",
        _ => "Gốc",
    };

    public static string FilterSubtitle(ColorFilter filter) => filter switch
    {
        ColorFilter.Warm => "Warm",
        ColorFilter.Cool => "Cool",
        ColorFilter.Bright => "Bright",
        ColorFilter.Pastel => "Pastel",
        ColorFilter.SoftRose => "Soft Rose",
        ColorFilter.GoldenHour => "Golden Hour",
        ColorFilter.Film => "Film",
        ColorFilter.Cinematic => "Cinematic",
        ColorFilter.Moody => "Moody",
        ColorFilter.Sepia => "Vintage",
        ColorFilter.Grayscale => "B&W",
        _ => "Original",
    };

    public static string FormatLabel(ExportFormat format) => format switch
    {
        ExportFormat.Jpeg => "JPEG",
        ExportFormat.Png => "PNG",
        ExportFormat.Pdf => "PDF",
        _ => format.ToString(),
    };

    public static StatusInfo ForCameraError(CameraError? error) => error switch
    {
        CameraError.NotFound => new(StatusKind.Error,
            "Không tìm thấy máy ảnh trên thiết bị này.",
            "Kiểm tra webcam đã được bật hoặc kết nối, rồi nhấn “Thử lại”."),
        CameraError.PermissionDenied => new(StatusKind.Error,
            "Ứng dụng chưa được cấp quyền sử dụng máy ảnh.",
            "Mở Cài đặt Windows › Quyền riêng tư › Camera, bật “Cho phép ứng dụng máy tính truy cập camera”, rồi nhấn “Thử lại”."),
        CameraError.InUse => new(StatusKind.Error,
            "Máy ảnh đang được ứng dụng khác sử dụng.",
            "Đóng ứng dụng đang dùng camera (ví dụ Zoom, Teams, Camera) rồi nhấn “Thử lại”."),
        CameraError.Disconnected or CameraError.NotActive => new(StatusKind.Error,
            "Máy ảnh bị ngắt kết nối.",
            "Kiểm tra lại webcam rồi nhấn “Thử lại”. Ảnh đã chụp vẫn được giữ nguyên."),
        CameraError.NotSupported => new(StatusKind.Error,
            "Không thể truy cập máy ảnh trong môi trường này.",
            "Vui lòng cập nhật Microsoft Edge WebView2 Runtime rồi mở lại ứng dụng."),
        _ => new(StatusKind.Error,
            "Không mở được máy ảnh.",
            "Vui lòng thử lại. Nếu lỗi lặp lại, hãy khởi động lại ứng dụng."),
    };

    /// <summary>Trả về null với các trường hợp không cần báo (người dùng tự dừng, bấm trùng).</summary>
    public static StatusInfo? ForCaptureFailure(CaptureSequenceResult result)
    {
        var kept = result.CapturedCount > 0
            ? string.Create(CultureInfo.InvariantCulture, $" {result.CapturedCount} ảnh vừa chụp đã được giữ lại.")
            : string.Empty;

        return result.Failure switch
        {
            null or CaptureFailureReason.Busy => null,
            CaptureFailureReason.Cancelled => result.CapturedCount > 0
                ? new(StatusKind.Info, "Đã dừng chụp.", kept.Trim())
                : null,
            CaptureFailureReason.NotReady => new(StatusKind.Warning, "Máy ảnh chưa sẵn sàng để chụp.", "Vui lòng chờ máy ảnh kết nối rồi thử lại."),
            CaptureFailureReason.StorageFailed => new(StatusKind.Error, "Không lưu được ảnh vừa chụp.", "Kiểm tra dung lượng ổ đĩa rồi chụp tiếp." + kept),
            _ => new(StatusKind.Error, "Chưa chụp được ảnh. Vui lòng thử lại.", kept.Trim()),
        };
    }

    /// <summary>Thông điệp lỗi xuất cho một định dạng.</summary>
    public static string ForExportError(ExportError? error) => error switch
    {
        ExportError.AlreadyExists => "Không tìm được tên file trống, hãy chọn thư mục khác.",
        ExportError.AccessDenied => "Không có quyền ghi vào thư mục này. Hãy chọn thư mục khác.",
        ExportError.DiskFull => "Ổ đĩa đã đầy. Hãy giải phóng dung lượng hoặc chọn ổ khác.",
        ExportError.DirectoryNotFound => "Thư mục không còn tồn tại. Hãy chọn thư mục khác.",
        ExportError.InvalidPath => "Vị trí lưu không hợp lệ. Hãy chọn thư mục khác.",
        ExportError.SourceMissing => "Không tìm thấy ảnh thành phẩm. Hãy ghép lại ảnh.",
        ExportError.EncodingFailed => "Không tạo được file. Hãy thử lại.",
        ExportError.Busy => "Đang lưu ảnh, vui lòng chờ…",
        ExportError.NothingToSave => "Chưa có ảnh thành phẩm để lưu.",
        ExportError.Cancelled => "Đã hủy lưu.",
        _ => "Không thể lưu ảnh vào vị trí đã chọn.",
    };
}
