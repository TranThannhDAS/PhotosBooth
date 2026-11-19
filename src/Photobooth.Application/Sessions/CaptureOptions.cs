namespace Photobooth.Application.Sessions;

/// <summary>
/// Cấu hình chụp ảnh tập trung. Không hardcode các giá trị này rải rác trong UI.
/// </summary>
public sealed record CaptureOptions
{
    public const int DefaultCountdownSeconds = 3;

    public const int DefaultPhotosPerSession = 4;

    /// <summary>Giới hạn trên tạm thời (giới hạn chính thức CHƯA CHỐT trong BUSINESS_RULES.md).</summary>
    public const int MaxCountdownSeconds = 30;

    /// <summary>Giới hạn trên tạm thời để tránh cấu hình bất thường.</summary>
    public const int MaxPhotosPerSession = 10;

    /// <summary>
    /// Chất lượng nén JPEG khi chụp (0..1]. 0.92 là giá trị tạm thời — giá trị chính thức còn CHƯA CHỐT.
    /// </summary>
    public const double DefaultJpegQuality = 0.92;

    /// <summary>Giới hạn an toàn cho một khung hình nhận từ camera.</summary>
    public const long DefaultMaxFrameBytes = 25 * 1024 * 1024;

    public int CountdownSeconds { get; init; } = DefaultCountdownSeconds;

    /// <summary>Số ảnh mỗi phiên. Template phải có đúng số vị trí ảnh này mới được chọn.</summary>
    public int PhotosPerSession { get; init; } = DefaultPhotosPerSession;

    public double JpegQuality { get; init; } = DefaultJpegQuality;

    public long MaxFrameBytes { get; init; } = DefaultMaxFrameBytes;

    /// <summary>
    /// Chế độ gương mặc định khi bắt đầu phiên. Khi bật: live preview lật như gương VÀ ảnh thành phẩm
    /// có cùng hướng với preview. Ảnh gốc trên ổ đĩa luôn là khung hình thô của camera.
    /// </summary>
    public bool MirrorByDefault { get; init; } = true;

    /// <summary>Độ phân giải mong muốn gửi tới camera (thiết bị có thể trả về thấp hơn).</summary>
    public int IdealWidthPixels { get; init; } = 1920;

    public int IdealHeightPixels { get; init; } = 1080;

    public void Validate()
    {
        if (CountdownSeconds is <= 0 or > MaxCountdownSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(CountdownSeconds), CountdownSeconds, $"Thời gian đếm ngược phải trong khoảng 1..{MaxCountdownSeconds} giây.");
        }

        if (PhotosPerSession is <= 0 or > MaxPhotosPerSession)
        {
            throw new ArgumentOutOfRangeException(nameof(PhotosPerSession), PhotosPerSession, $"Số ảnh mỗi phiên phải trong khoảng 1..{MaxPhotosPerSession}.");
        }

        if (JpegQuality is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(JpegQuality), JpegQuality, "Chất lượng JPEG phải trong khoảng (0, 1].");
        }

        if (MaxFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFrameBytes), MaxFrameBytes, "Giới hạn dung lượng khung hình phải dương.");
        }

        if (IdealWidthPixels <= 0 || IdealHeightPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(IdealWidthPixels), "Độ phân giải mong muốn phải dương.");
        }
    }
}
