using System.Globalization;
using EDSDKLib;

namespace Photobooth.Camera.Canon;

/// <summary>Các thông số máy ảnh Canon được chỉnh từ phần mềm (Admin › Camera).</summary>
/// <remarks>Giá trị enum được lưu theo tên trong file bộ thông số: chỉ thêm mới, không đổi tên.</remarks>
public enum CanonSetting
{
    /// <summary>Chế độ chụp theo vòng chế độ trên máy (chỉ đọc): quyết định thông số nào chỉnh được.</summary>
    ExposureMode,
    Iso,
    Aperture,
    ShutterSpeed,
    ExposureCompensation,
    WhiteBalance,

    /// <summary>Chỉ chỉnh được khi cân bằng trắng đang ở "Nhiệt độ màu".</summary>
    ColorTemperature,
    PictureStyle,

    /// <summary>Độ tương phản của Picture Style đang chọn (-4..+4).</summary>
    PictureStyleContrast,

    /// <summary>Độ sắc nét của Picture Style đang chọn (0..7).</summary>
    PictureStyleSharpness,

    /// <summary>Độ bão hòa màu của Picture Style đang chọn (-4..+4).</summary>
    PictureStyleSaturation,

    /// <summary>Tông màu (da) của Picture Style đang chọn (-4 đỏ hơn .. +4 vàng hơn).</summary>
    PictureStyleColorTone,

    /// <summary>Phương pháp lấy nét trên Live View (khuôn mặt + bám theo, 1 điểm...).</summary>
    LiveViewAfMethod,

    /// <summary>Thao tác lấy nét (One-Shot, AI Servo...).</summary>
    FocusOperation,

    /// <summary>Chất lượng/kích thước ảnh. Chỉ cho chọn JPEG (RAW/HEIF làm hỏng luồng chụp).</summary>
    ImageQuality,

    /// <summary>Tỉ lệ khung ảnh (thuộc tính riêng của Canon, cần mở khóa khi mở session).</summary>
    AspectRatio,

    /// <summary>Thời gian tự tắt nguồn (giây; 0 = không tự tắt). Thuộc tính riêng của Canon.</summary>
    AutoPowerOff,
}

/// <summary>Nhóm hiển thị thông số trên giao diện.</summary>
public enum CanonSettingGroup
{
    Exposure,
    Color,
    Focus,
    ImageAndOperation,
}

/// <summary>Một giá trị của thông số: mã EDSDK và nhãn hiển thị.</summary>
public sealed record CanonSettingValue(uint Code, string Label);

/// <summary>
/// Trạng thái một thông số: giá trị hiện tại và các giá trị máy ảnh cho phép lúc này.
/// <see cref="IsWritable"/> = false khi chế độ chụp hiện tại không cho chỉnh (ví dụ ISO ở chế độ Tự động).
/// </summary>
public sealed record CanonSettingState(
    CanonSetting Setting,
    string Name,
    CanonSettingValue? Current,
    IReadOnlyList<CanonSettingValue> Options,
    bool IsWritable);

/// <summary>
/// Mã thuộc tính EDSDK và bảng nhãn cho từng thông số. Bảng mã lấy từ sample CameraControl của Canon (EDSDK 13.21.10).
/// </summary>
public static class CanonSettingCatalog
{
    /// <summary>Mã cân bằng trắng "Nhiệt độ màu" (chỉnh Kelvin).</summary>
    public const uint WhiteBalanceColorTemperature = 9;

    public const uint MinColorTemperatureKelvin = 2500;
    public const uint MaxColorTemperatureKelvin = 10000;
    public const uint ColorTemperatureStepKelvin = 100;

    /// <summary>Thứ tự hiển thị.</summary>
    public static IReadOnlyList<CanonSetting> All { get; } =
    [
        CanonSetting.ExposureMode,
        CanonSetting.Iso,
        CanonSetting.Aperture,
        CanonSetting.ShutterSpeed,
        CanonSetting.ExposureCompensation,
        CanonSetting.WhiteBalance,
        CanonSetting.ColorTemperature,
        CanonSetting.PictureStyle,
        CanonSetting.PictureStyleContrast,
        CanonSetting.PictureStyleSaturation,
        CanonSetting.PictureStyleColorTone,
        CanonSetting.PictureStyleSharpness,
        CanonSetting.LiveViewAfMethod,
        CanonSetting.FocusOperation,
        CanonSetting.ImageQuality,
        CanonSetting.AspectRatio,
        CanonSetting.AutoPowerOff,
    ];

    public static IReadOnlyList<CanonSettingGroup> Groups { get; } =
        [CanonSettingGroup.Exposure, CanonSettingGroup.Color, CanonSettingGroup.Focus, CanonSettingGroup.ImageAndOperation];

    public static bool IsPictureStyleDetail(CanonSetting setting) => setting is
        CanonSetting.PictureStyleContrast or CanonSetting.PictureStyleSharpness
        or CanonSetting.PictureStyleSaturation or CanonSetting.PictureStyleColorTone;

    public static uint PropertyId(CanonSetting setting) => setting switch
    {
        CanonSetting.ExposureMode => EDSDK.PropID_AEMode,
        CanonSetting.Iso => EDSDK.PropID_ISOSpeed,
        CanonSetting.Aperture => EDSDK.PropID_Av,
        CanonSetting.ShutterSpeed => EDSDK.PropID_Tv,
        CanonSetting.ExposureCompensation => EDSDK.PropID_ExposureCompensation,
        CanonSetting.WhiteBalance => EDSDK.PropID_WhiteBalance,
        CanonSetting.ColorTemperature => EDSDK.PropID_ColorTemperature,
        CanonSetting.PictureStyle => EDSDK.PropID_PictureStyle,
        CanonSetting.PictureStyleContrast or CanonSetting.PictureStyleSharpness
            or CanonSetting.PictureStyleSaturation or CanonSetting.PictureStyleColorTone => EDSDK.PropID_PictureStyleDesc,
        CanonSetting.LiveViewAfMethod => EDSDK.PropID_Evf_AFMode,
        CanonSetting.FocusOperation => EDSDK.PropID_AFMode,
        CanonSetting.ImageQuality => EDSDK.PropID_ImageQuality,
        CanonSetting.AspectRatio => EDSDK.PropID_Aspect,
        CanonSetting.AutoPowerOff => EDSDK.PropID_AutoPowerOffSetting,
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null),
    };

    public static CanonSetting? FromPropertyId(uint propertyId) =>
        All.Where(s => PropertyId(s) == propertyId).Select(s => (CanonSetting?)s).FirstOrDefault();

    public static CanonSettingGroup Group(CanonSetting setting) => setting switch
    {
        CanonSetting.ExposureMode or CanonSetting.Iso or CanonSetting.Aperture
            or CanonSetting.ShutterSpeed or CanonSetting.ExposureCompensation => CanonSettingGroup.Exposure,
        CanonSetting.LiveViewAfMethod or CanonSetting.FocusOperation => CanonSettingGroup.Focus,
        CanonSetting.ImageQuality or CanonSetting.AspectRatio or CanonSetting.AutoPowerOff => CanonSettingGroup.ImageAndOperation,
        _ => CanonSettingGroup.Color,
    };

    public static string GroupName(CanonSettingGroup group) => group switch
    {
        CanonSettingGroup.Exposure => "Phơi sáng",
        CanonSettingGroup.Color => "Màu sắc",
        CanonSettingGroup.Focus => "Lấy nét",
        _ => "Ảnh và vận hành",
    };

    public static string Name(CanonSetting setting) => setting switch
    {
        CanonSetting.ExposureMode => "Chế độ chụp",
        CanonSetting.Iso => "ISO",
        CanonSetting.Aperture => "Khẩu độ",
        CanonSetting.ShutterSpeed => "Tốc độ màn trập",
        CanonSetting.ExposureCompensation => "Bù sáng",
        CanonSetting.WhiteBalance => "Cân bằng trắng",
        CanonSetting.ColorTemperature => "Nhiệt độ màu",
        CanonSetting.PictureStyle => "Picture Style",
        CanonSetting.PictureStyleContrast => "Độ tương phản",
        CanonSetting.PictureStyleSaturation => "Độ bão hòa màu",
        CanonSetting.PictureStyleColorTone => "Tông màu da",
        CanonSetting.PictureStyleSharpness => "Độ sắc nét",
        CanonSetting.LiveViewAfMethod => "Kiểu lấy nét",
        CanonSetting.FocusOperation => "Thao tác lấy nét",
        CanonSetting.ImageQuality => "Chất lượng ảnh",
        CanonSetting.AspectRatio => "Tỉ lệ khung",
        CanonSetting.AutoPowerOff => "Tự tắt nguồn",
        _ => setting.ToString(),
    };

    public static string Label(CanonSetting setting, uint code)
    {
        switch (setting)
        {
            case CanonSetting.ColorTemperature:
                return string.Create(CultureInfo.InvariantCulture, $"{code} K");
            case CanonSetting.PictureStyleContrast or CanonSetting.PictureStyleSaturation or CanonSetting.PictureStyleColorTone:
                var signed = unchecked((int)code);
                return signed > 0 ? string.Create(CultureInfo.InvariantCulture, $"+{signed}") : signed.ToString(CultureInfo.InvariantCulture);
            case CanonSetting.PictureStyleSharpness:
                return code.ToString(CultureInfo.InvariantCulture);
            case CanonSetting.AutoPowerOff:
                return AutoPowerOffLabel(code);
            case CanonSetting.ImageQuality when !IsJpegQuality(code):
                return "RAW/HEIF — không dùng được cho photobooth";
        }

        var map = setting switch
        {
            CanonSetting.ExposureMode => ExposureModes,
            CanonSetting.Iso => IsoSpeeds,
            CanonSetting.Aperture => Apertures,
            CanonSetting.ShutterSpeed => ShutterSpeeds,
            CanonSetting.ExposureCompensation => ExposureCompensations,
            CanonSetting.WhiteBalance => WhiteBalances,
            CanonSetting.PictureStyle => PictureStyles,
            CanonSetting.LiveViewAfMethod => LiveViewAfMethods,
            CanonSetting.FocusOperation => FocusOperations,
            CanonSetting.ImageQuality => JpegQualities,
            CanonSetting.AspectRatio => AspectRatios,
            _ => null,
        };

        return map is not null && map.TryGetValue(code, out var label) ? label : $"Mã 0x{code:X}";
    }

    /// <summary>Chất lượng chỉ gồm JPEG (adapter chỉ nhận JPEG; RAW+JPEG thì bỏ RAW nhưng chậm hơn, HEIF thì không chụp được).</summary>
    public static bool IsJpegQuality(uint code) => JpegQualities.ContainsKey(code);

    /// <summary>Các mức nhiệt độ màu chuẩn (2500–10000 K, bước 100 K) khi máy không trả danh sách.</summary>
    public static IReadOnlyList<uint> DefaultColorTemperatures { get; } =
        Enumerable.Range(0, (int)((MaxColorTemperatureKelvin - MinColorTemperatureKelvin) / ColorTemperatureStepKelvin) + 1)
            .Select(i => MinColorTemperatureKelvin + ((uint)i * ColorTemperatureStepKelvin))
            .ToArray();

    /// <summary>Các mức của chi tiết Picture Style (theo thang của máy EOS).</summary>
    public static IReadOnlyList<uint> PictureStyleDetailRange(CanonSetting setting) => setting == CanonSetting.PictureStyleSharpness
        ? Enumerable.Range(0, 8).Select(v => (uint)v).ToArray()
        : Enumerable.Range(-4, 9).Select(v => unchecked((uint)v)).ToArray();

    private static string AutoPowerOffLabel(uint seconds) => seconds switch
    {
        0 => "Không tự tắt",
        0xffffffff => "Tắt nguồn ngay",
        < 60 => string.Create(CultureInfo.InvariantCulture, $"{seconds} giây"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} phút"),
    };

    private static readonly Dictionary<uint, string> ExposureModes = new()
    {
        [0] = "P (Chương trình)",
        [1] = "Tv (Ưu tiên tốc độ)",
        [2] = "Av (Ưu tiên khẩu độ)",
        [3] = "M (Chỉnh tay)",
        [4] = "Bulb",
        [9] = "Tự động",
        [10] = "Chân dung ban đêm",
        [11] = "Thể thao",
        [12] = "Chân dung",
        [13] = "Phong cảnh",
        [14] = "Cận cảnh",
        [15] = "Không đèn flash",
        [19] = "Creative Auto",
        [20] = "Quay phim",
        [22] = "Tự động thông minh",
        [25] = "SCN (Cảnh)",
        [29] = "Bộ lọc sáng tạo",
        [50] = "Tự chụp chân dung",
        [52] = "Da mịn",
        [55] = "Fv",
    };

    private static readonly Dictionary<uint, string> IsoSpeeds = new()
    {
        [0x00] = "Tự động", [0x28] = "6", [0x30] = "12", [0x38] = "25", [0x40] = "50", [0x48] = "100", [0x4b] = "125",
        [0x4d] = "160", [0x50] = "200", [0x53] = "250", [0x55] = "320", [0x58] = "400", [0x5b] = "500", [0x5d] = "640",
        [0x60] = "800", [0x63] = "1000", [0x65] = "1250", [0x68] = "1600", [0x6b] = "2000", [0x6d] = "2500",
        [0x70] = "3200", [0x73] = "4000", [0x75] = "5000", [0x78] = "6400", [0x7b] = "8000", [0x7d] = "10000",
        [0x80] = "12800", [0x83] = "16000", [0x85] = "20000", [0x88] = "25600", [0x8b] = "32000", [0x8d] = "40000",
        [0x90] = "51200", [0x93] = "64000", [0x95] = "80000", [0x98] = "102400", [0xa0] = "204800", [0xa8] = "409600",
        [0xb0] = "819200",
    };

    private static readonly Dictionary<uint, string> Apertures = new()
    {
        [0x08] = "f/1.0", [0x0B] = "f/1.1", [0x0C] = "f/1.2", [0x0D] = "f/1.2", [0x10] = "f/1.4", [0x13] = "f/1.6",
        [0x14] = "f/1.8", [0x15] = "f/1.8", [0x18] = "f/2.0", [0x1B] = "f/2.2", [0x1C] = "f/2.5", [0x1D] = "f/2.5",
        [0x20] = "f/2.8", [0x23] = "f/3.2", [0x80] = "f/3.3", [0x85] = "f/3.4", [0x24] = "f/3.5", [0x25] = "f/3.5",
        [0x28] = "f/4.0", [0x2B] = "f/4.5", [0x2C] = "f/4.5", [0x2D] = "f/5.0", [0x30] = "f/5.6", [0x33] = "f/6.3",
        [0x34] = "f/6.7", [0x35] = "f/7.1", [0x38] = "f/8.0", [0x3B] = "f/9.0", [0x3C] = "f/9.5", [0x3D] = "f/10",
        [0x40] = "f/11", [0x43] = "f/13", [0x44] = "f/13", [0x45] = "f/14", [0x48] = "f/16", [0x4B] = "f/18",
        [0x4C] = "f/19", [0x4D] = "f/20", [0x50] = "f/22", [0x53] = "f/25", [0x54] = "f/27", [0x55] = "f/29",
        [0x58] = "f/32", [0x5B] = "f/36", [0x5C] = "f/38", [0x5D] = "f/40", [0x60] = "f/45", [0x63] = "f/51",
        [0x64] = "f/54", [0x65] = "f/57", [0x68] = "f/64", [0x6B] = "f/72", [0x6C] = "f/76", [0x6D] = "f/80",
        [0x70] = "f/91", [0xFF] = "Tự động",
    };

    // Mã ≤ 0x45: phơi sáng tính bằng giây (30" … 0.3"); mã ≥ 0x48: phân số 1/x giây.
    private static readonly Dictionary<uint, string> ShutterSpeeds = new()
    {
        [0x04] = "Tự động", [0x0c] = "Bulb", [0x10] = "30\"", [0x13] = "25\"", [0x14] = "20\"", [0x15] = "20\"",
        [0x18] = "15\"", [0x1B] = "13\"", [0x1C] = "10\"", [0x1D] = "10\"", [0x20] = "8\"", [0x23] = "6\"",
        [0x24] = "6\"", [0x25] = "5\"", [0x28] = "4\"", [0x2B] = "3.2\"", [0x2C] = "3\"", [0x2D] = "2.5\"",
        [0x30] = "2\"", [0x33] = "1.6\"", [0x34] = "1.5\"", [0x35] = "1.3\"", [0x38] = "1\"", [0x3B] = "0.8\"",
        [0x3C] = "0.7\"", [0x3D] = "0.6\"", [0x40] = "0.5\"", [0x43] = "0.4\"", [0x44] = "0.3\"", [0x45] = "0.3\"",
        [0x48] = "1/4", [0x4B] = "1/5", [0x4C] = "1/6", [0x4D] = "1/6", [0x50] = "1/8", [0x53] = "1/10",
        [0x54] = "1/10", [0x55] = "1/13", [0x58] = "1/15", [0x5B] = "1/20", [0x5C] = "1/20", [0x5D] = "1/25",
        [0x60] = "1/30", [0x63] = "1/40", [0x64] = "1/45", [0x65] = "1/50", [0x68] = "1/60", [0x6B] = "1/80",
        [0x6C] = "1/90", [0x6D] = "1/100", [0x70] = "1/125", [0x73] = "1/160", [0x74] = "1/180", [0x75] = "1/200",
        [0x78] = "1/250", [0x7B] = "1/320", [0x7C] = "1/350", [0x7D] = "1/400", [0x80] = "1/500", [0x83] = "1/640",
        [0x84] = "1/750", [0x85] = "1/800", [0x88] = "1/1000", [0x8B] = "1/1250", [0x8C] = "1/1500", [0x8D] = "1/1600",
        [0x90] = "1/2000", [0x93] = "1/2500", [0x94] = "1/3000", [0x95] = "1/3200", [0x98] = "1/4000", [0x9B] = "1/5000",
        [0x9C] = "1/6000", [0x9D] = "1/6400", [0xA0] = "1/8000", [0xA3] = "1/10000", [0xA4] = "1/12000",
        [0xA5] = "1/12800", [0xA8] = "1/16000", [0xAB] = "1/20000", [0xAC] = "1/24000", [0xAD] = "1/25600",
        [0xB0] = "1/32000", [0xB3] = "1/40000", [0xB4] = "1/48000", [0xB5] = "1/51200", [0xB8] = "1/64000",
    };

    private static readonly Dictionary<uint, string> ExposureCompensations = new()
    {
        [0x28] = "+5", [0x25] = "+4 2/3", [0x24] = "+4 1/2", [0x23] = "+4 1/3", [0x20] = "+4", [0x1D] = "+3 2/3",
        [0x1C] = "+3 1/2", [0x1B] = "+3 1/3", [0x18] = "+3", [0x15] = "+2 2/3", [0x14] = "+2 1/2", [0x13] = "+2 1/3",
        [0x10] = "+2", [0x0d] = "+1 2/3", [0x0c] = "+1 1/2", [0x0b] = "+1 1/3", [0x08] = "+1", [0x05] = "+2/3",
        [0x04] = "+1/2", [0x03] = "+1/3", [0x00] = "0", [0xfd] = "-1/3", [0xfc] = "-1/2", [0xfb] = "-2/3",
        [0xf8] = "-1", [0xf5] = "-1 1/3", [0xf4] = "-1 1/2", [0xf3] = "-1 2/3", [0xf0] = "-2", [0xed] = "-2 1/3",
        [0xec] = "-2 1/2", [0xeb] = "-2 2/3", [0xe8] = "-3", [0xE5] = "-3 1/3", [0xE4] = "-3 1/2", [0xE3] = "-3 2/3",
        [0xE0] = "-4", [0xDD] = "-4 1/3", [0xDC] = "-4 1/2", [0xDB] = "-4 2/3", [0xD8] = "-5",
    };

    private static readonly Dictionary<uint, string> WhiteBalances = new()
    {
        [0] = "Tự động (giữ không khí)", [23] = "Tự động (ưu tiên trắng)", [1] = "Ánh nắng", [2] = "Trời mây",
        [8] = "Bóng râm", [3] = "Đèn sợi đốt", [4] = "Đèn huỳnh quang trắng", [5] = "Đèn flash", [17] = "Dưới nước",
        [9] = "Nhiệt độ màu (K)", [24] = "Nhiệt độ màu 2", [25] = "Nhiệt độ màu 3", [26] = "Nhiệt độ màu 4",
        [6] = "Tùy chỉnh 1", [15] = "Tùy chỉnh 2", [16] = "Tùy chỉnh 3", [18] = "Tùy chỉnh 4", [19] = "Tùy chỉnh 5",
        [10] = "Tùy chỉnh PC-1", [11] = "Tùy chỉnh PC-2", [12] = "Tùy chỉnh PC-3", [20] = "Tùy chỉnh PC-4",
        [21] = "Tùy chỉnh PC-5",
    };

    private static readonly Dictionary<uint, string> PictureStyles = new()
    {
        [0x0087] = "Tự động", [0x0081] = "Tiêu chuẩn", [0x0082] = "Chân dung", [0x0083] = "Phong cảnh",
        [0x0088] = "Chi tiết", [0x0084] = "Trung tính", [0x0085] = "Trung thực", [0x0086] = "Đơn sắc",
        [0x0021] = "Người dùng 1", [0x0022] = "Người dùng 2", [0x0023] = "Người dùng 3",
        [0x0041] = "PC 1", [0x0042] = "PC 2", [0x0043] = "PC 3",
    };

    private static readonly Dictionary<uint, string> LiveViewAfMethods = new()
    {
        [0x00] = "Nhanh (Quick)", [0x01] = "1 điểm", [0x02] = "Khuôn mặt + bám theo", [0x03] = "Nhiều điểm (FlexiZone)",
        [0x04] = "Vùng", [0x05] = "Mở rộng vùng AF", [0x06] = "Mở rộng vùng AF: xung quanh", [0x07] = "Vùng lớn: ngang",
        [0x08] = "Vùng lớn: dọc", [0x09] = "Catch AF", [0x0a] = "Điểm nhỏ (Spot)", [0x0b] = "Vùng linh hoạt 1",
        [0x0c] = "Vùng linh hoạt 2", [0x0d] = "Vùng linh hoạt 3", [0x0e] = "Toàn khung", [0x0f] = "Điểm nhỏ (không bám)",
        [0x10] = "1 điểm (không bám)", [0x11] = "Mở rộng vùng AF (không bám)", [0x12] = "Mở rộng xung quanh (không bám)",
    };

    private static readonly Dictionary<uint, string> FocusOperations = new()
    {
        [0] = "One-Shot (lấy nét một lần)", [1] = "AI Servo (bám liên tục)", [2] = "AI Focus (tự chọn)", [3] = "Lấy nét tay (MF)",
    };

    private static readonly Dictionary<uint, string> JpegQualities = new()
    {
        [0x0013ff0f] = "JPEG Lớn – Tinh (L Fine)", [0x0012ff0f] = "JPEG Lớn – Thường (L Normal)", [0x0010ff0f] = "JPEG Lớn (L)",
        [0x0113ff0f] = "JPEG Vừa – Tinh (M Fine)", [0x0112ff0f] = "JPEG Vừa – Thường (M Normal)", [0x0110ff0f] = "JPEG Vừa (M)",
        [0x0510ff0f] = "JPEG Vừa 1 (M1)", [0x0610ff0f] = "JPEG Vừa 2 (M2)",
        [0x0213ff0f] = "JPEG Nhỏ – Tinh (S Fine)", [0x0212ff0f] = "JPEG Nhỏ – Thường (S Normal)", [0x0210ff0f] = "JPEG Nhỏ (S)",
        [0x0E13ff0f] = "JPEG Nhỏ 1 – Tinh (S1 Fine)", [0x0E12ff0f] = "JPEG Nhỏ 1 – Thường (S1 Normal)", [0x0e10ff0f] = "JPEG Nhỏ 1 (S1)",
        [0x0F13ff0f] = "JPEG Nhỏ 2 (S2)", [0x0f10ff0f] = "JPEG Nhỏ 2 (S2)", [0x1013ff0f] = "JPEG Nhỏ 3 (S3)",
    };

    private static readonly Dictionary<uint, string> AspectRatios = new()
    {
        [0x0000] = "3:2 (toàn khung)", [0x0001] = "1:1", [0x0002] = "4:3", [0x0007] = "16:9", [0x000d] = "Crop 1.6x",
    };
}
