using System.Collections.Immutable;
using System.Globalization;

namespace Photobooth.Application.Templates.Editing;

/// <summary>
/// Phiên làm việc của Visual Template Editor: trạng thái hiện tại, lựa chọn, Undo/Redo (chỉ trong phiên này)
/// và các thao tác chỉnh sửa. Không truy cập ổ đĩa; việc lưu do Template Library thực hiện.
/// Không thread-safe: chỉ dùng từ luồng giao diện.
/// </summary>
public sealed class TemplateEditor
{
    public const int MaxHistory = 100;
    private const int DuplicateOffset = 20;

    private readonly Stack<TemplateDraft> _undo = new();
    private readonly Stack<TemplateDraft> _redo = new();
    private Gesture? _gesture;

    public TemplateEditor(TemplateDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Current = draft.NormalizeLayers();
        Saved = Current;
    }

    public TemplateDraft Current { get; private set; }

    /// <summary>Trạng thái đã lưu gần nhất (để biết có thay đổi chưa lưu).</summary>
    public TemplateDraft Saved { get; private set; }

    public Guid? SelectedKey { get; private set; }

    public EditorElement? Selected => SelectedKey is { } key ? Current.Find(key) : null;

    public bool HasUnsavedChanges => !Current.IsSameAs(Saved);

    public bool CanUndo => _undo.Count > 0 && _gesture is null;

    public bool CanRedo => _redo.Count > 0 && _gesture is null;

    public bool IsDragging => _gesture is not null;

    /// <summary>Trạng thái thay đổi (để giao diện vẽ lại).</summary>
    public event EventHandler? Changed;

    // ------------------------------------------------------------------ Lựa chọn, lịch sử

    public void Select(Guid? key)
    {
        SelectedKey = key is { } k && Current.Find(k) is not null ? k : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo() => Step(_undo, _redo);

    public bool Redo() => Step(_redo, _undo);

    /// <summary>Đánh dấu trạng thái hiện tại đã được lưu (sau khi Template Library ghi thành công).</summary>
    public void MarkSaved()
    {
        Saved = Current;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Áp dụng một thay đổi thành một bước Undo. Trả về false nếu không có gì thay đổi.</summary>
    public bool Apply(Func<TemplateDraft, TemplateDraft> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (_gesture is not null)
        {
            return false;
        }

        var next = change(Current).NormalizeLayers();
        if (next.IsSameAs(Current))
        {
            return false;
        }

        Push(_undo, Current);
        _redo.Clear();
        Current = next;
        if (SelectedKey is { } key && Current.Find(key) is null)
        {
            SelectedKey = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ------------------------------------------------------------------ Kéo / đổi kích thước

    /// <summary>Bắt đầu kéo một phần tử (chọn luôn phần tử đó). Toàn bộ lần kéo là MỘT bước Undo.</summary>
    public bool BeginDrag(Guid key, DragHandle handle)
    {
        if (Current.Find(key) is not { } element)
        {
            return false;
        }

        _gesture = new Gesture(key, handle, element.Bounds, Current);
        SelectedKey = key;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Cập nhật theo tổng độ dời từ lúc bắt đầu kéo, tính bằng PIXEL CANVAS (đã đổi từ màn hình).</summary>
    public void DragTo(double canvasDeltaX, double canvasDeltaY, bool keepAspect = false, int gridSize = 0)
    {
        if (_gesture is not { } gesture || gesture.Start.Find(gesture.Key) is not { } element)
        {
            return;
        }

        var rect = CanvasGeometry.Apply(gesture.StartBounds, gesture.Handle, canvasDeltaX, canvasDeltaY, keepAspect, gridSize);
        if (element.Kind == EditorElementKind.QrCode)
        {
            rect = CanvasGeometry.ToSquare(rect, gesture.Handle);
        }

        var index = gesture.Start.IndexOf(gesture.Key);
        Current = gesture.Start with { Layers = gesture.Start.Layers.SetItem(index, element.WithBounds(rect)) };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Kết thúc kéo: ghi một bước Undo nếu vị trí/kích thước đã đổi.</summary>
    public void EndDrag()
    {
        if (_gesture is not { } gesture)
        {
            return;
        }

        _gesture = null;
        if (!Current.IsSameAs(gesture.Start))
        {
            Push(_undo, gesture.Start);
            _redo.Clear();
        }
        else
        {
            Current = gesture.Start;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hủy lần kéo đang diễn ra (ví dụ mất kết nối giao diện), trả về vị trí ban đầu.</summary>
    public void CancelDrag()
    {
        if (_gesture is { } gesture)
        {
            _gesture = null;
            Current = gesture.Start;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ------------------------------------------------------------------ Thêm, nhân bản, xóa

    /// <summary>
    /// Thêm ô ảnh cho ảnh MỚI (photoIndex = số ảnh hiện tại), đặt giữa canvas, tỉ lệ 4:3.
    /// Ô mới nằm ngay trên ô ảnh cao nhất (dưới các khung phủ/chữ).
    /// </summary>
    public EditorElement AddSlot()
    {
        var canvas = Current.Canvas;
        var width = Math.Max(1, canvas.WidthPixels * 2 / 5);
        var height = Math.Max(1, width * 3 / 4);
        var rect = CanvasGeometry.Center(new CanvasRect(0, 0, width, height), canvas.WidthPixels, canvas.HeightPixels, true, true);
        var lastSlot = Current.Layers.FindLastIndex(l => l.IsSlot);
        var element = EditorElement.ForSlot(new TemplateSlot
        {
            Id = NextId("photo"),
            PhotoIndex = Current.RequiredPhotoCount,
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
            ZIndex = lastSlot >= 0 ? Current.Layers[lastSlot].ZIndex : 0,
        });

        Insert(element, lastSlot + 1);
        return element;
    }

    public EditorElement AddText()
    {
        var canvas = Current.Canvas;
        var fontSize = Math.Max(8, canvas.WidthPixels / 16);
        var rect = CanvasGeometry.Center(new CanvasRect(0, 0, Math.Max(1, canvas.WidthPixels * 3 / 4), Math.Max(1, fontSize * 3 / 2)), canvas.WidthPixels, canvas.HeightPixels, true, true);
        return AddDecoration(new TemplateDecoration
        {
            Id = NextId("text"),
            Type = DecorationKind.Text,
            Text = "Nhập chữ",
            FontFamily = "Segoe UI",
            FontSizePixels = fontSize,
            Color = "#111827",
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
        });
    }

    public EditorElement AddRectangle(bool ellipse = false)
    {
        var canvas = Current.Canvas;
        var size = Math.Max(1, Math.Min(canvas.WidthPixels, canvas.HeightPixels) / 3);
        var rect = CanvasGeometry.Center(new CanvasRect(0, 0, size, size), canvas.WidthPixels, canvas.HeightPixels, true, true);
        return AddDecoration(new TemplateDecoration
        {
            Id = NextId(ellipse ? "ellipse" : "rect"),
            Type = ellipse ? DecorationKind.Ellipse : DecorationKind.Rectangle,
            Color = "#F9A8D4",
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
        });
    }

    /// <summary>Thêm ảnh trang trí (asset đã được chép vào thư mục template), giữ tỉ lệ gốc, rộng tối đa 1/2 canvas.</summary>
    public EditorElement AddImage(string assetPath, int imageWidthPixels, int imageHeightPixels)
    {
        if (!TemplateAssetPath.IsSafeRelative(assetPath))
        {
            throw new ArgumentException("Đường dẫn asset phải tương đối trong thư mục template.", nameof(assetPath));
        }

        var canvas = Current.Canvas;
        var scale = Math.Min(1.0, Math.Min(canvas.WidthPixels / 2.0 / Math.Max(1, imageWidthPixels), canvas.HeightPixels / 2.0 / Math.Max(1, imageHeightPixels)));
        var width = Math.Max(1, (int)Math.Round(imageWidthPixels * scale));
        var height = Math.Max(1, (int)Math.Round(imageHeightPixels * scale));
        var rect = CanvasGeometry.Center(new CanvasRect(0, 0, width, height), canvas.WidthPixels, canvas.HeightPixels, true, true);
        return AddDecoration(new TemplateDecoration
        {
            Id = NextId("image"),
            Type = DecorationKind.Image,
            AssetPath = assetPath.Replace('\\', '/'),
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
        });
    }

    /// <summary>
    /// Thêm vùng mã QR (vuông, góc dưới bên phải, đủ lớn để quét khi in). Mỗi template chỉ có một vùng QR:
    /// nếu đã có thì chọn vùng đó thay vì thêm mới.
    /// </summary>
    public EditorElement AddQrCode()
    {
        if (Current.Layers.FirstOrDefault(l => l.Kind == EditorElementKind.QrCode) is { } existing)
        {
            Select(existing.Key);
            return existing;
        }

        var canvas = Current.Canvas;
        var shortSide = Math.Max(1, Math.Min(canvas.WidthPixels, canvas.HeightPixels));
        var size = Math.Min(shortSide, Math.Max(TemplateValidator.MinQrSizePixels(canvas.Dpi), shortSide / 5));
        var margin = Math.Max(0, Math.Min(shortSide / 30, shortSide - size));
        return AddDecoration(new TemplateDecoration
        {
            Id = NextId("qr"),
            Type = DecorationKind.QrCode,
            X = Math.Max(0, canvas.WidthPixels - size - margin),
            Y = Math.Max(0, canvas.HeightPixels - size - margin),
            Width = size,
            Height = size,
        });
    }

    /// <summary>Nhân bản phần tử đang chọn, lệch một chút và nằm ngay trên bản gốc. Ô ảnh bản sao dùng CHUNG photoIndex.</summary>
    /// <returns>null nếu chưa chọn gì hoặc phần tử là vùng QR (mỗi template chỉ có một vùng QR).</returns>
    public EditorElement? DuplicateSelected()
    {
        if (Selected is not { } source || source.Kind == EditorElementKind.QrCode)
        {
            return null;
        }

        var prefix = source.Kind switch
        {
            EditorElementKind.Slot => "photo",
            EditorElementKind.Text => "text",
            EditorElementKind.Image => "image",
            EditorElementKind.Ellipse => "ellipse",
            _ => "rect",
        };
        var bounds = source.Bounds;
        var copy = source.CopyWithNewKey()
            .WithElementId(NextId(prefix))
            .WithBounds(bounds with { X = bounds.X + DuplicateOffset, Y = bounds.Y + DuplicateOffset });
        Insert(copy, Current.IndexOf(source.Key) + 1);
        return copy;
    }

    public bool DeleteSelected()
    {
        if (SelectedKey is not { } key)
        {
            return false;
        }

        return Apply(d => d with { Layers = d.Layers.RemoveAll(l => l.Key == key) });
    }

    // ------------------------------------------------------------------ Lớp (zIndex)

    /// <summary>Đưa lớp lên (+1) hoặc xuống (−1) một bậc. Lớp đổi chỗ nhận zIndex của lớp nó vượt qua.</summary>
    public bool MoveLayer(Guid key, int direction)
    {
        var index = Current.IndexOf(key);
        var target = index + Math.Sign(direction);
        if (index < 0 || direction == 0 || target < 0 || target >= Current.Layers.Count)
        {
            return false;
        }

        return Apply(d =>
        {
            var moving = d.Layers[index];
            var passed = d.Layers[target];
            return d with { Layers = d.Layers.RemoveAt(index).Insert(target, moving.WithZIndex(passed.ZIndex)) };
        });
    }

    /// <summary>Đặt zIndex trực tiếp: lớp được xếp lại theo đúng quy tắc vẽ của Photo Composer.</summary>
    public bool SetZIndex(Guid key, int zIndex)
    {
        if (Current.Find(key) is null)
        {
            return false;
        }

        zIndex = Math.Clamp(zIndex, TemplateValidator.MinZIndex, TemplateValidator.MaxZIndex);
        return Apply(d =>
        {
            var layers = d.Layers.Select(l => l.Key == key ? l.WithZIndex(zIndex) : l).ToList();
            var template = (d with { Layers = [.. layers] }).ToTemplate(null);
            var order = TemplateLayers.GetRenderOrder(template);

            // GetRenderOrder đánh số khai báo: ô ảnh trước, rồi trang trí — ánh xạ ngược về phần tử Editor.
            var slots = layers.Where(l => l.IsSlot).ToArray();
            var decorations = layers.Where(l => !l.IsSlot).ToArray();
            var sorted = order.Select(e => e.Slot is not null ? slots[e.DeclarationOrder] : decorations[e.DeclarationOrder - slots.Length]);
            return d with { Layers = [.. sorted] };
        });
    }

    // ------------------------------------------------------------------ Thuộc tính

    public bool UpdateSlot(Guid key, Func<TemplateSlot, TemplateSlot> change) =>
        Replace(key, e => e.Slot is { } s ? EditorElement.ForSlot(change(s), e.Key) : e);

    public bool UpdateDecoration(Guid key, Func<TemplateDecoration, TemplateDecoration> change) =>
        Replace(key, e => e.Decoration is { } d ? EditorElement.ForDecoration(change(d), e.Key) : e);

    /// <summary>Đặt vị trí/kích thước. Vùng QR luôn vuông: cạnh vừa được đổi quyết định cạnh còn lại.</summary>
    public bool SetBounds(Guid key, CanvasRect bounds) =>
        Replace(key, e =>
        {
            var width = Math.Max(1, bounds.Width);
            var height = Math.Max(1, bounds.Height);
            if (e.Kind == EditorElementKind.QrCode)
            {
                var size = height != e.Bounds.Height && width == e.Bounds.Width ? height : width;
                width = height = size;
            }

            return e.WithBounds(bounds with { Width = width, Height = height });
        });

    public bool SetInfo(string name, string? description) =>
        Apply(d => d with { Name = name.Trim(), Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim() });

    public bool SetCanvas(TemplateCanvas canvas) => Apply(d => d with { Canvas = canvas });

    /// <summary>
    /// Đánh lại photoIndex liên tục từ 0 theo thứ tự giá trị đang dùng, GIỮ các ô đang dùng chung ảnh
    /// (ví dụ [0, 0, 2, 2] → [0, 0, 1, 1]). Dùng khi template bị thiếu chỉ số trung gian.
    /// </summary>
    public bool RenumberPhotoIndexes() => Apply(d =>
    {
        var map = d.Slots.Select(s => s.Slot!.PhotoIndex).Where(i => i >= 0).Distinct().Order()
            .Select((value, i) => (value, i)).ToDictionary(x => x.value, x => x.i);
        return d with
        {
            Layers = d.Layers.Select(l => l.Slot is { } s && map.TryGetValue(s.PhotoIndex, out var n) ? EditorElement.ForSlot(s with { PhotoIndex = n }, l.Key) : l).ToImmutableList(),
        };
    });

    /// <summary>Kiểm tra theo đúng TemplateValidator của Engine (chưa gồm kiểm tra file asset).</summary>
    public TemplateValidationResult Validate() => TemplateValidator.Validate(Current.ToTemplate(null));

    // ------------------------------------------------------------------ Nội bộ

    private EditorElement AddDecoration(TemplateDecoration decoration)
    {
        var element = EditorElement.ForDecoration(decoration with { ZIndex = Current.Layers.Count > 0 ? Current.Layers[^1].ZIndex : 0 });
        Insert(element, Current.Layers.Count);
        return element;
    }

    private void Insert(EditorElement element, int index)
    {
        Apply(d => d with { Layers = d.Layers.Insert(Math.Clamp(index, 0, d.Layers.Count), element) });
        SelectedKey = element.Key;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool Replace(Guid key, Func<EditorElement, EditorElement> change)
    {
        var index = Current.IndexOf(key);
        if (index < 0)
        {
            return false;
        }

        return Apply(d => d with { Layers = d.Layers.SetItem(index, change(d.Layers[index])) });
    }

    private string NextId(string prefix)
    {
        var used = Current.Layers.Select(l => l.ElementId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"{prefix}-{n}");
            if (!used.Contains(id))
            {
                return id;
            }
        }
    }

    private bool Step(Stack<TemplateDraft> from, Stack<TemplateDraft> to)
    {
        if (_gesture is not null || from.Count == 0)
        {
            return false;
        }

        Push(to, Current);
        Current = from.Pop();
        if (SelectedKey is { } key && Current.Find(key) is null)
        {
            SelectedKey = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static void Push(Stack<TemplateDraft> stack, TemplateDraft draft)
    {
        stack.Push(draft);
        if (stack.Count > MaxHistory)
        {
            // Bỏ bước cũ nhất.
            var kept = stack.Take(MaxHistory).Reverse().ToArray();
            stack.Clear();
            foreach (var item in kept)
            {
                stack.Push(item);
            }
        }
    }

    private sealed record Gesture(Guid Key, DragHandle Handle, CanvasRect StartBounds, TemplateDraft Start);
}
