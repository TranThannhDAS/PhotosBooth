using Photobooth.Infrastructure.Imaging;
using SkiaSharp;

namespace Photobooth.Tests.Composition;

public sealed class TemplateTextFontResolverTests
{
    [Theory]
    [InlineData("TÌNH YÊU NỞ HOA", "Georgia")]
    [InlineData("CÙNG NHAU VIẾT NÊN CHUYỆN TÌNH", "Georgia")]
    [InlineData("HÔM NAY VÀ MÃI MÃI", "Georgia")]
    [InlineData("CÔ DÂU & CHÚ RỂ", "Symbol")]
    [InlineData("Nguyễn Thị Minh & Trần Văn Linh", "Wingdings")]
    public void Resolve_FindsAFontCoveringEveryVietnameseGlyph(string text, string preferredFont)
    {
        using var typeface = TemplateTextFontResolver.Resolve(text, preferredFont, SKFontStyle.Normal);
        using var font = new SKFont(typeface, 48);

        // Tránh lỗi chữ Việt hiển thị ô vuông khi font trang trí không đủ glyph.
        Assert.True(font.ContainsGlyphs(text), $"Font '{typeface.FamilyName}' thiếu glyph của: {text}");
    }

    [Fact]
    public void Resolve_NormalizesUnicodeAfterCallerAndKeepsFullCoverage()
    {
        const string decomposed = "TÌNH YÊU NO\u031B\u0309 HOA"; // NỞ được tạo từ combining marks.
        var normalized = decomposed.Normalize(System.Text.NormalizationForm.FormC);

        using var typeface = TemplateTextFontResolver.Resolve(normalized, "Georgia", SKFontStyle.Bold);
        using var font = new SKFont(typeface, 64);

        Assert.True(font.ContainsGlyphs(normalized));
    }

    [Fact]
    public void Resolve_KeepsPreferredFontWhenItContainsEveryGlyph()
    {
        const string text = "OUR LOVE STORY";
        using var preferred = SKTypeface.FromFamilyName("Georgia", SKFontStyle.Normal);
        using var chosen = TemplateTextFontResolver.Resolve(text, "Georgia", SKFontStyle.Normal);
        using var font = new SKFont(chosen, 40);

        Assert.True(font.ContainsGlyphs(text));
        if (preferred is not null)
        {
            Assert.Equal(preferred.FamilyName, chosen.FamilyName);
        }
    }
}
