using Photobooth.Application.Camera;

namespace Photobooth.Application.Sessions;

public enum CaptureFailureReason
{
    /// <summary>Phiên không ở trạng thái cho phép chụp (chưa sẵn sàng hoặc đã kết thúc).</summary>
    NotReady,

    /// <summary>Đang có một lượt chụp khác diễn ra.</summary>
    Busy,

    /// <summary>Người dùng hủy/rời màn hình trong lúc chụp.</summary>
    Cancelled,

    /// <summary>Camera không cung cấp được khung hình.</summary>
    CameraFailed,

    /// <summary>Dữ liệu nhận được không phải JPEG hợp lệ hoặc quá lớn.</summary>
    InvalidImage,

    /// <summary>Không ghi được ảnh gốc xuống ổ đĩa.</summary>
    StorageFailed,
}

/// <summary>Kết quả một lượt chụp (một hoặc nhiều ảnh liên tiếp).</summary>
/// <param name="CapturedCount">Số ảnh đã lưu thành công trong lượt này (giữ lại kể cả khi lượt bị dừng giữa chừng).</param>
public sealed record CaptureSequenceResult(int CapturedCount, CaptureFailureReason? Failure, CameraError? CameraError)
{
    public bool Succeeded => Failure is null;

    public static CaptureSequenceResult Completed(int capturedCount) => new(capturedCount, null, null);

    public static CaptureSequenceResult Failed(int capturedCount, CaptureFailureReason reason, CameraError? cameraError = null) =>
        new(capturedCount, reason, cameraError);
}

public enum CaptureStage
{
    /// <summary>Đang đếm ngược; <see cref="CaptureProgress.SecondsRemaining"/> &gt; 0.</summary>
    Countdown,

    /// <summary>Đang lấy khung hình và ghi ảnh gốc.</summary>
    Shooting,

    /// <summary>Ảnh vừa được lưu thành công.</summary>
    Saved,
}

/// <param name="Position">Vị trí ảnh (0-based) đang chụp.</param>
/// <param name="PhotoCount">Tổng số ảnh của phiên (để hiển thị "Ảnh X/Y").</param>
/// <param name="ShotNumber">Thứ tự trong lượt chụp hiện tại (1-based).</param>
/// <param name="ShotCount">Số ảnh của lượt chụp hiện tại.</param>
public sealed record CaptureProgress(
    int Position,
    int PhotoCount,
    int ShotNumber,
    int ShotCount,
    CaptureStage Stage,
    int SecondsRemaining);

public enum CompositionOutcome
{
    Succeeded,
    NotReady,
    Busy,
    Failed,
}
