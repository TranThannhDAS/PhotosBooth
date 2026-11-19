using System.Globalization;

namespace Photobooth.Application.Templates;

public static class TemplateText
{
    /// <summary>
    /// Thay biến trong chữ trang trí bằng một thời điểm thống nhất của lượt render:
    /// {date} → dd/MM/yyyy, {time} → HH:mm, {datetime} → dd/MM/yyyy HH:mm.
    /// </summary>
    public static string Resolve(string text, DateTimeOffset localTimestamp)
    {
        var culture = CultureInfo.InvariantCulture;
        return text
            .Replace("{datetime}", localTimestamp.ToString("dd/MM/yyyy HH:mm", culture), StringComparison.Ordinal)
            .Replace("{date}", localTimestamp.ToString("dd/MM/yyyy", culture), StringComparison.Ordinal)
            .Replace("{time}", localTimestamp.ToString("HH:mm", culture), StringComparison.Ordinal);
    }
}
