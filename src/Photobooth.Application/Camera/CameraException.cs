namespace Photobooth.Application.Camera;

/// <summary>Ngoại lệ có phân loại khi thao tác camera thất bại (ví dụ chụp lúc camera đã mất kết nối).</summary>
public sealed class CameraException : Exception
{
    public CameraException(CameraError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public CameraError Error { get; }
}

