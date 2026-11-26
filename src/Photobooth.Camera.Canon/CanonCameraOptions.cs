namespace Photobooth.Camera.Canon;

/// <summary>Thời gian chờ/thử lại khi làm việc với máy ảnh Canon. Giá trị mặc định dựa trên đo đạc với EOS M50 Mark II.</summary>
public sealed record CanonCameraOptions
{
    /// <summary>
    /// Số lần thử kết nối khi mở camera. Ngay sau khi cắm lại cáp, máy ảnh có thể chưa xuất hiện vài giây.
    /// </summary>
    public int ConnectAttempts { get; init; } = 3;

    public TimeSpan ConnectRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Thời gian chờ ảnh về sau khi bấm chụp. Đo thực tế ~1,3 giây.</summary>
    public TimeSpan TransferTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Số lần thử lại khi máy ảnh báo bận (<c>EDS_ERR_DEVICE_BUSY</c>).</summary>
    public int BusyRetryAttempts { get; init; } = 5;

    /// <summary>Khoảng cách giữa các lần thử lại khi máy bận; Canon khuyến nghị ~500 ms.</summary>
    public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public void Validate()
    {
        if (ConnectAttempts is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectAttempts), ConnectAttempts, "Số lần thử kết nối phải trong khoảng 1..20.");
        }

        if (BusyRetryAttempts is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(BusyRetryAttempts), BusyRetryAttempts, "Số lần thử lại khi máy bận phải trong khoảng 0..20.");
        }

        if (ConnectRetryDelay < TimeSpan.Zero || BusyRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectRetryDelay), "Thời gian chờ thử lại không được âm.");
        }

        if (TransferTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TransferTimeout), TransferTimeout, "Thời gian chờ ảnh phải dương.");
        }
    }
}
