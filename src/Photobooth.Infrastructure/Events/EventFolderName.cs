using System.Text;

namespace Photobooth.Infrastructure.Events;

/// <summary>Đổi tên sự kiện thành tên thư mục Windows hợp lệ (giữ tiếng Việt có dấu, khoảng trắng).</summary>
public static class EventFolderName
{
    public const int MaxLength = 100;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Ký tự không được phép (<c>\ / : * ? " &lt; &gt; |</c>, ký tự điều khiển) thay bằng "-", gộp khoảng trắng, bỏ dấu chấm/khoảng trắng
    /// ở cuối, tối đa <see cref="MaxLength"/> ký tự.
    /// </summary>
    /// <returns>false nếu không còn tên dùng được (rỗng, chỉ gồm ký tự không hợp lệ, hoặc là tên dành riêng của Windows như CON, PRN).</returns>
    public static bool TryCreate(string? eventName, out string folderName)
    {
        folderName = string.Empty;
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return false;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(eventName.Length);
        var lastWasSpace = false;
        foreach (var c in eventName.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '-' : c);
            lastWasSpace = false;
        }

        var name = builder.ToString();
        if (name.Length > MaxLength)
        {
            name = name[..(char.IsHighSurrogate(name[MaxLength - 1]) ? MaxLength - 1 : MaxLength)];
        }

        name = name.TrimEnd(' ', '.').TrimStart(' ');
        if (name.Length == 0 || name.All(c => c is '-' or '.') || ReservedNames.Contains(name.Split('.')[0].TrimEnd()))
        {
            return false;
        }

        folderName = name;
        return true;
    }
}
