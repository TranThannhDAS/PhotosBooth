using System.Globalization;

namespace Photobooth.Application.Templates;

/// <summary>Đọc màu dạng #RRGGBB hoặc #AARRGGBB thành giá trị ARGB 32 bit.</summary>
public static class HexColor
{
    public static bool TryParse(string? value, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(value) || value[0] != '#')
        {
            return false;
        }

        var hex = value.AsSpan(1);
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        argb = hex.Length == 6 ? 0xFF000000u | parsed : parsed;
        return true;
    }
}
