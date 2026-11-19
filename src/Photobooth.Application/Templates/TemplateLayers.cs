namespace Photobooth.Application.Templates;

/// <summary>Một phần tử cần vẽ: đúng một trong hai <see cref="Slot"/> hoặc <see cref="Decoration"/> khác null.</summary>
public sealed record TemplateRenderElement(TemplateSlot? Slot, TemplateDecoration? Decoration, int ZIndex, int DeclarationOrder);

public static class TemplateLayers
{
    /// <summary>
    /// Thứ tự vẽ thống nhất cho mọi loại phần tử (nền canvas luôn vẽ đầu tiên, không thuộc danh sách này):
    /// zIndex tăng dần; cùng zIndex thì theo thứ tự khai báo — ô ảnh (slots) trước, trang trí (decorations) sau.
    /// Sắp xếp ổn định nên kết quả render luôn giống nhau.
    /// </summary>
    public static IReadOnlyList<TemplateRenderElement> GetRenderOrder(PhotoTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var elements = template.Slots
            .Select((slot, i) => new TemplateRenderElement(slot, null, slot.ZIndex, i))
            .Concat(template.Decorations.Select((decoration, i) =>
                new TemplateRenderElement(null, decoration, decoration.ZIndex, template.Slots.Count + i)));

        // OrderBy của LINQ là sắp xếp ổn định; ThenBy làm rõ quy tắc.
        return elements.OrderBy(e => e.ZIndex).ThenBy(e => e.DeclarationOrder).ToArray();
    }
}
