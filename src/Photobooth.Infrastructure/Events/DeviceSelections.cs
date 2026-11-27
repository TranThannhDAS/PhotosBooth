using System.Text.Json.Serialization;

namespace Photobooth.Infrastructure.Events;

public enum PrinterKind
{
    /// <summary>Chưa cấu hình máy in.</summary>
    None,
    /// <summary>Máy in giả lập để kiểm thử.</summary>
    Mock,
    /// <summary>Máy in Windows theo tên.</summary>
    Windows,
}

public sealed record PrinterSelection
{
    public PrinterKind Kind { get; init; } = PrinterKind.None;
    public string? WindowsPrinterName { get; init; }
}

/// <summary>Webcam đã chọn trên máy; null = camera mặc định của Windows.</summary>
public sealed record CameraSelection
{
    public string? WebcamDeviceId { get; init; }
    public string? WebcamLabel { get; init; }

    [JsonIgnore]
    public bool IsDefault => string.IsNullOrEmpty(WebcamDeviceId);
}
