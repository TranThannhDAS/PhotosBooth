namespace Photobooth.Application.Camera;

/// <summary>Kết quả khởi động camera. Lỗi thiết bị dự kiến được trả về thay vì ném exception.</summary>
public sealed record CameraStartResult
{
    private CameraStartResult(bool succeeded, CameraError? error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    public bool Succeeded { get; }

    public CameraError? Error { get; }

    public static CameraStartResult Success() => new(true, null);

    public static CameraStartResult Failure(CameraError error) => new(false, error);
}

