using System.Globalization;
using System.Text;

namespace Photobooth.Application.Templates.Editing;

/// <summary>Khổ giấy gợi ý khi tạo template (không bắt buộc mọi template là 4×6).</summary>
public sealed record CanvasPreset(string Key, string Label, double WidthInches, double HeightInches)
{
    public static IReadOnlyList<CanvasPreset> All { get; } =
    [
        new("2x6", "2×6 inch (dải dọc)", 2, 6),
        new("4x6", "4×6 inch (dọc)", 4, 6),
        new("6x4", "6×4 inch (ngang)", 6, 4),
    ];

    public static CanvasPreset? Find(string? key) => All.FirstOrDefault(p => p.Key == key);

    /// <summary>Kích thước pixel = inch × DPI (ví dụ 4×6 inch ở 300 DPI = 1200×1800 px).</summary>
    public (int WidthPixels, int HeightPixels) ToPixels(int dpi) => (ToPixels(WidthInches, dpi), ToPixels(HeightInches, dpi));

    public static int ToPixels(double inches, int dpi) => (int)Math.Round(inches * dpi, MidpointRounding.AwayFromZero);
}

/// <summary>Tạo id template hợp lệ (a-z, 0-9, '-', tối đa 64 ký tự) từ tên hiển thị tiếng Việt.</summary>
public static class TemplateIds
{
    public const int MaxLength = 64;

    /// <summary>"Đám cưới Hồng" → "dam-cuoi-hong". Không còn ký tự hợp lệ → "template".</summary>
    public static string Slugify(string? name)
    {
        var builder = new StringBuilder();
        var normalized = (name ?? string.Empty).Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(lower);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxLength - 4)
        {
            slug = slug[..(MaxLength - 4)].Trim('-');
        }

        return slug.Length == 0 ? "template" : slug;
    }

    /// <summary>Id chưa dùng: giữ nguyên nếu trống, ngược lại thêm hậu tố -2, -3…</summary>
    public static string MakeUnique(string baseId, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);
        var id = Slugify(baseId);
        if (!isTaken(id))
        {
            return id;
        }

        for (var n = 2; ; n++)
        {
            var suffix = string.Create(CultureInfo.InvariantCulture, $"-{n}");
            var candidate = (id.Length + suffix.Length > MaxLength ? id[..(MaxLength - suffix.Length)] : id) + suffix;
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }
    }
}
