using System.Text;
using Photobooth.Application.Composition;
using SkiaSharp;

namespace Photobooth.Infrastructure.Imaging;

/// <summary>
/// Tự tìm font có đầy đủ glyph cho CẢ dòng chữ sau khi chuẩn hóa Unicode.
/// SkiaSharp DrawText không tự thay font cho từng ký tự bị thiếu; dùng duy nhất một font
/// thiếu glyph khiến tiếng Việt có dấu hiển thị ô vuông dù template.json đúng UTF-8.
/// Giữ font cấu hình khi đủ glyph; chỉ thay bằng font gần phong cách khi cần.
/// </summary>
internal static class TemplateTextFontResolver
{
    private static readonly string[] LanguageTags = ["vi"];

    // Ưu tiên font serif cho mẫu cưới dùng Georgia để giữ cảm giác trang trọng.
    // Các font này thuộc Windows/Linux thông dụng, không đóng gói font có bản quyền.
    private static readonly string[] FallbackFamilies =
    [
        "Cambria",
        "Times New Roman",
        "Palatino Linotype",
        "Noto Serif",
        "DejaVu Serif",
        "Segoe UI",
        "Arial",
        "Tahoma",
        "Noto Sans",
        "DejaVu Sans",
        "Liberation Sans",
    ];

    /// <summary>
    /// Trả về typeface do caller sở hữu và cần Dispose.
    /// Không thay đổi nội dung text hoặc các giá trị fontFamily trong JSON.
    /// </summary>
    internal static SKTypeface Resolve(string text, string? preferredFamily, SKFontStyle style)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var family = string.IsNullOrWhiteSpace(preferredFamily) ? TemplateRenderer.DefaultFontFamily : preferredFamily;

        if (TryFamily(family, text, style, visited) is { } preferred)
        {
            return preferred;
        }

        foreach (var fallbackFamily in FallbackFamilies)
        {
            if (TryFamily(fallbackFamily, text, style, visited) is { } fallback)
            {
                return fallback;
            }
        }

        // Font được cài trên máy nhưng không nằm trong danh sách trên:
        // để Skia/Windows tự dò font theo Unicode codepoint với ưu tiên ngôn ngữ "vi".
        foreach (var rune in text.EnumerateRunes())
        {
            var matched = SKFontManager.Default.MatchCharacter(family, style, LanguageTags, rune.Value);
            if (matched is null)
            {
                continue;
            }

            if (HasAllGlyphs(matched, text))
            {
                return matched;
            }

            matched.Dispose();
        }

        // Không tạo ảnh thành phẩm với ký tự bị thay bằng ô vuông.
        throw new CompositionException(
            $"Không tìm thấy font có đủ ký tự cho nội dung '{text}'. "
            + "Hãy cài một font Unicode hỗ trợ tiếng Việt (ví dụ Segoe UI hoặc Cambria) trên máy Photobooth.");
    }

    private static SKTypeface? TryFamily(
        string family, string text, SKFontStyle style, HashSet<string> visited)
    {
        if (!visited.Add(family))
        {
            return null;
        }

        var face = SKTypeface.FromFamilyName(family, style);
        if (face is null)
        {
            return null;
        }

        if (HasAllGlyphs(face, text))
        {
            return face;
        }

        face.Dispose();
        return null;
    }

    private static bool HasAllGlyphs(SKTypeface face, string text)
    {
        using var font = new SKFont(face, 32);
        return font.ContainsGlyphs(text);
    }
}
