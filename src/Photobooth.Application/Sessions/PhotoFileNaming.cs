namespace Photobooth.Application.Sessions;

public static class PhotoFileNaming
{
    /// <summary>
    /// Tên file xuất (không phần mở rộng) theo giờ địa phương. Ví dụ: Photobooth_20261119_100512.
    /// Các định dạng của cùng một lần xuất dùng chung tên này.
    /// </summary>
    public static string SuggestBaseFileName(DateTimeOffset timestamp, TimeZoneInfo? timeZone = null)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp, timeZone ?? TimeZoneInfo.Local);
        return $"Photobooth_{local:yyyyMMdd_HHmmss}";
    }
}
