using Photobooth.Infrastructure.Events;

namespace Photobooth.Infrastructure.Persistence;

/// <summary>
/// Cấu hình riêng của máy Photobooth. Một bản ghi Id=1 lưu lựa chọn máy in,
/// webcam và lớp phủ hướng dẫn Kiosk. Không thuộc một sự kiện hoặc phiên chụp.
/// </summary>
public sealed class DeviceSettingsRecord
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public PrinterKind PrinterKind { get; set; } = PrinterKind.None;
    public string? WindowsPrinterName { get; set; }

    public string? WebcamDeviceId { get; set; }
    public string? WebcamLabel { get; set; }

    public CaptureGuideMode GuideMode { get; set; } = CaptureGuideMode.DimOutside;
    public double GuideOutsideOpacity { get; set; } = CaptureGuideSettings.DefaultOutsideOpacity;
    public bool GuideShowLabel { get; set; } = true;
}
