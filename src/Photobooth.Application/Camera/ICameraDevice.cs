namespace Photobooth.Application.Camera;

/// <summary>
/// Hợp đồng của một nguồn camera. Giai đoạn 1 được hiện thực bằng webcam qua WebView2
/// (nằm ở Photobooth.Desktop); về sau có thể thay bằng adapter native hoặc SDK máy ảnh
/// mà không phải sửa nghiệp vụ phiên chụp.
/// </summary>
/// <remarks>
/// Cách hiển thị live preview phụ thuộc công nghệ nên không thuộc hợp đồng này;
/// nghiệp vụ chỉ cần bật/tắt camera và lấy một khung hình khi chụp.
/// </remarks>
public interface ICameraDevice
{
    /// <summary>Camera đang mở và có thể chụp.</summary>
    bool IsActive { get; }

    /// <summary>Phát sinh khi camera đang hoạt động bị mất (ví dụ rút thiết bị).</summary>
    event EventHandler<CameraError>? Faulted;

    Task<CameraStartResult> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Dừng camera và giải phóng thiết bị. Gọi nhiều lần không gây lỗi.</summary>
    Task StopAsync();

    /// <summary>Chụp một khung hình. Ném <see cref="CameraException"/> khi camera không chụp được.</summary>
    Task<CapturedFrame> CaptureFrameAsync(CancellationToken cancellationToken = default);
}

