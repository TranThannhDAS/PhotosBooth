namespace Photobooth.Infrastructure.Events;

/// <summary>Kiểu hiển thị khung hướng dẫn trong Live View Kiosk.</summary>
public enum CaptureGuideMode
{
    /// <summary>Chỉ kẻ đường viền vùng sẽ được giữ trong thành phẩm.</summary>
    Outline,

    /// <summary>Phủ tối bán trong suốt vùng nằm ngoài thành phẩm.</summary>
    DimOutside,

    /// <summary>Ẩn hoàn toàn vùng ngoài thành phẩm trên Live View, không crop file ảnh gốc.</summary>
    CropPreview,
}

/// <summary>
/// Cấu hình hiển thị vùng chụp trên Kiosk, lưu trong app-state.json theo máy.
/// Không tác động đến ảnh gốc, tiêu cự, tỷ lệ JPEG hay thuật toán Auto Crop.
/// </summary>
public sealed record CaptureGuideSettings
{
    public const double DefaultOutsideOpacity = 0.65;

    public CaptureGuideMode Mode { get; init; } = CaptureGuideMode.DimOutside;

    public double OutsideOpacity { get; init; } = DefaultOutsideOpacity;

    public bool ShowGuideLabel { get; init; } = true;

    /// <summary>File cấu hình có giá trị lạ: chỉ khôi phục phần hướng dẫn, không làm hỏng phiên chụp.</summary>
    public CaptureGuideSettings Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : CaptureGuideMode.DimOutside,
        OutsideOpacity = double.IsFinite(OutsideOpacity)
            ? Math.Clamp(OutsideOpacity, 0.2, 0.9)
            : DefaultOutsideOpacity,
    };

    /// <summary>Đầu vào từ Admin phải hợp lệ trước khi ghi file.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        if (!double.IsFinite(OutsideOpacity) || OutsideOpacity is < 0.2 or > 0.9)
        {
            throw new ArgumentOutOfRangeException(nameof(OutsideOpacity),
                "Độ tối ngoài khung phải nằm trong khoảng 20–90%.");
        }
    }
}
