namespace Photobooth.Camera.Canon;

/// <summary>
/// Mức cảnh báo nhiệt của máy ảnh (thuộc tính riêng <c>TempStatus</c>). Canon không cung cấp nhiệt độ theo °C,
/// chỉ báo mức ảnh hưởng tới hoạt động — đủ để cảnh báo trước khi máy ngừng Live View/ngừng chụp.
/// </summary>
public enum CanonTemperatureStatus
{
    Unknown,
    Normal,
    Warning,
    FrameRateDown,
    LiveViewDisabled,
    ShutterDisabled,
    StillQualityWarning,
    MovieRestricted,
}

/// <summary>Trạng thái máy ảnh (chỉ đọc). Giá trị null = máy không cung cấp.</summary>
public sealed record CanonCameraStatus(
    int? BatteryPercent,
    bool OnAcPower,
    CanonTemperatureStatus Temperature,
    string? LensName)
{
    /// <summary>Ngưỡng pin yếu (dưới mức này nên thay pin/cắm nguồn trước khi phục vụ khách).</summary>
    public const int LowBatteryPercent = 20;

    public bool IsBatteryLow => !OnAcPower && BatteryPercent is < LowBatteryPercent;

    /// <summary>Đang nóng đến mức ảnh hưởng hoạt động (giảm khung hình, tắt Live View, không cho chụp...).</summary>
    public bool IsOverheating => Temperature is not (CanonTemperatureStatus.Normal or CanonTemperatureStatus.Unknown);

    /// <summary>Đọc mã <c>TempStatus</c> theo bảng của sample Canon (16 bit cao = 0x0002 là hạn chế quay phim).</summary>
    public static CanonTemperatureStatus ParseTemperature(uint code)
    {
        if ((code & 0xffff0000) == 0x00020000)
        {
            return CanonTemperatureStatus.MovieRestricted;
        }

        return code switch
        {
            0 => CanonTemperatureStatus.Normal,
            1 => CanonTemperatureStatus.Warning,
            2 => CanonTemperatureStatus.FrameRateDown,
            3 => CanonTemperatureStatus.LiveViewDisabled,
            4 => CanonTemperatureStatus.ShutterDisabled,
            5 => CanonTemperatureStatus.StillQualityWarning,
            _ => CanonTemperatureStatus.Unknown,
        };
    }

    public static string TemperatureLabel(CanonTemperatureStatus status) => status switch
    {
        CanonTemperatureStatus.Normal => "Bình thường",
        CanonTemperatureStatus.Warning => "Máy đang nóng",
        CanonTemperatureStatus.FrameRateDown => "Quá nóng: Live View giảm tốc độ",
        CanonTemperatureStatus.LiveViewDisabled => "Quá nóng: đã tắt Live View",
        CanonTemperatureStatus.ShutterDisabled => "Quá nóng: không cho chụp",
        CanonTemperatureStatus.StillQualityWarning => "Quá nóng: chất lượng ảnh có thể giảm",
        CanonTemperatureStatus.MovieRestricted => "Quá nóng: hạn chế quay phim",
        _ => "Không rõ",
    };
}
