using System.Collections.Immutable;

namespace Photobooth.Application.Templates.Editing;

public enum EditorElementKind
{
    Slot,
    Rectangle,
    Ellipse,
    Text,
    Image,
    QrCode,
}

/// <summary>
/// Một lớp trong Template Editor. Bọc NGUYÊN record của Template Engine (<see cref="TemplateSlot"/> hoặc
/// <see cref="TemplateDecoration"/>) nên mọi thuộc tính — kể cả thuộc tính Editor chưa có ô nhập — được giữ nguyên khi lưu.
/// <see cref="Key"/> chỉ dùng trong phiên Editor, không ghi ra JSON.
/// </summary>
public sealed record EditorElement
{
    private EditorElement(Guid key, TemplateSlot? slot, TemplateDecoration? decoration)
    {
        Key = key;
        Slot = slot;
        Decoration = decoration;
    }

    public Guid Key { get; }

    public TemplateSlot? Slot { get; private init; }

    public TemplateDecoration? Decoration { get; private init; }

    public static EditorElement ForSlot(TemplateSlot slot, Guid? key = null) =>
        new(key ?? Guid.NewGuid(), slot ?? throw new ArgumentNullException(nameof(slot)), null);

    public static EditorElement ForDecoration(TemplateDecoration decoration, Guid? key = null) =>
        new(key ?? Guid.NewGuid(), null, decoration ?? throw new ArgumentNullException(nameof(decoration)));

    public bool IsSlot => Slot is not null;

    public EditorElementKind Kind => Slot is not null
        ? EditorElementKind.Slot
        : Decoration!.Type switch
        {
            DecorationKind.Rectangle => EditorElementKind.Rectangle,
            DecorationKind.Ellipse => EditorElementKind.Ellipse,
            DecorationKind.Text => EditorElementKind.Text,
            DecorationKind.QrCode => EditorElementKind.QrCode,
            _ => EditorElementKind.Image,
        };

    public string? ElementId => Slot is { } slot ? slot.Id : Decoration!.Id;

    public int ZIndex => Slot?.ZIndex ?? Decoration!.ZIndex;

    public CanvasRect Bounds => Slot is { } s
        ? new CanvasRect(s.X, s.Y, s.Width, s.Height)
        : new CanvasRect(Decoration!.X, Decoration.Y, Decoration.Width, Decoration.Height);

    public EditorElement WithBounds(CanvasRect r) => Slot is { } s
        ? this with { Slot = s with { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height } }
        : this with { Decoration = Decoration! with { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height } };

    public EditorElement WithZIndex(int zIndex) => Slot is { } s
        ? this with { Slot = s with { ZIndex = zIndex } }
        : this with { Decoration = Decoration! with { ZIndex = zIndex } };

    public EditorElement WithElementId(string? id) => Slot is { } s
        ? this with { Slot = s with { Id = id } }
        : this with { Decoration = Decoration! with { Id = id } };

    /// <summary>Bản sao có khóa mới (dùng cho Duplicate).</summary>
    public EditorElement CopyWithNewKey() => Slot is { } s ? ForSlot(s) : ForDecoration(Decoration!);
}

/// <summary>
/// Trạng thái bất biến của template đang thiết kế. <see cref="Layers"/> xếp từ DƯỚI lên TRÊN, đúng thứ tự Photo Composer vẽ
/// (<see cref="TemplateLayers.GetRenderOrder"/>); nền canvas luôn nằm dưới cùng và không thuộc danh sách.
/// </summary>
public sealed record TemplateDraft
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required TemplateCanvas Canvas { get; init; }

    public ImmutableList<EditorElement> Layers { get; init; } = [];

    /// <summary>Số ảnh cần chụp (BR-T03): photoIndex lớn nhất + 1, không phải số ô.</summary>
    public int RequiredPhotoCount => ToTemplate(null).RequiredPhotoCount;

    public IEnumerable<EditorElement> Slots => Layers.Where(l => l.IsSlot);

    public EditorElement? Find(Guid key) => Layers.FirstOrDefault(l => l.Key == key);

    public int IndexOf(Guid key) => Layers.FindIndex(l => l.Key == key);

    /// <summary>So sánh theo nội dung (kể cả từng lớp), không theo tham chiếu danh sách.</summary>
    public bool IsSameAs(TemplateDraft other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Id == other.Id
            && Name == other.Name
            && Description == other.Description
            && Canvas == other.Canvas
            && Layers.SequenceEqual(other.Layers));

    /// <summary>Mở template hiện có: lớp được xếp đúng thứ tự vẽ của Photo Composer.</summary>
    public static TemplateDraft FromTemplate(PhotoTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var layers = TemplateLayers.GetRenderOrder(template)
            .Select(e => e.Slot is { } slot ? EditorElement.ForSlot(slot) : EditorElement.ForDecoration(e.Decoration!))
            .ToImmutableList();

        return new TemplateDraft
        {
            Id = template.Id,
            Name = template.Name,
            Description = template.Description,
            Canvas = template.Canvas,
            Layers = layers,
        }.NormalizeLayers();
    }

    /// <summary>
    /// Chuyển về Template Model của Engine. Ô ảnh và trang trí được ghi theo thứ tự lớp, zIndex đã được
    /// <see cref="NormalizeLayers"/> tính sao cho <see cref="TemplateLayers.GetRenderOrder"/> cho đúng thứ tự lớp của Editor.
    /// </summary>
    public PhotoTemplate ToTemplate(string? assetDirectory) => new()
    {
        Id = Id,
        Name = Name,
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description,
        Canvas = Canvas,
        Slots = Layers.Where(l => l.Slot is not null).Select(l => l.Slot!).ToArray(),
        Decorations = Layers.Where(l => l.Decoration is not null).Select(l => l.Decoration!).ToArray(),
        AssetDirectory = assetDirectory,
    };

    /// <summary>
    /// Gán zIndex khớp thứ tự lớp, giữ nguyên giá trị cũ khi có thể. Quy tắc Engine: zIndex tăng dần; cùng zIndex thì
    /// ô ảnh trước trang trí, rồi theo thứ tự khai báo (= thứ tự lớp vì <see cref="ToTemplate"/> ghi theo lớp).
    /// Vì vậy chỉ cần nâng zIndex khi nó nhỏ hơn lớp dưới, hoặc khi một ô ảnh nằm ngay trên trang trí cùng zIndex.
    /// </summary>
    public TemplateDraft NormalizeLayers()
    {
        if (Layers.Count == 0)
        {
            return this;
        }

        var assigned = new int[Layers.Count];
        var fits = true;
        for (var i = 0; i < Layers.Count; i++)
        {
            var z = Layers[i].ZIndex;
            if (i > 0)
            {
                var previous = assigned[i - 1];
                if (z < previous)
                {
                    z = previous;
                }

                if (z == previous && !Layers[i - 1].IsSlot && Layers[i].IsSlot)
                {
                    z = previous + 1;
                }
            }

            fits &= z is >= TemplateValidator.MinZIndex and <= TemplateValidator.MaxZIndex;
            assigned[i] = z;
        }

        if (!fits)
        {
            // Hiếm gặp: vượt giới hạn zIndex → đánh số lại tăng dần liên tiếp (thứ tự lớp vẫn giữ đúng).
            var start = Math.Clamp(Layers.Min(l => l.ZIndex), TemplateValidator.MinZIndex, TemplateValidator.MaxZIndex - Layers.Count + 1);
            for (var i = 0; i < assigned.Length; i++)
            {
                assigned[i] = start + i;
            }
        }

        var changed = false;
        var builder = Layers.ToBuilder();
        for (var i = 0; i < builder.Count; i++)
        {
            if (builder[i].ZIndex != assigned[i])
            {
                builder[i] = builder[i].WithZIndex(assigned[i]);
                changed = true;
            }
        }

        return changed ? this with { Layers = builder.ToImmutable() } : this;
    }
}
