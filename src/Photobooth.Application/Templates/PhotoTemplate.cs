using System.Text.Json.Serialization;

namespace Photobooth.Application.Templates;

/// <summary>
/// Định nghĩa một khung ảnh (template), nạp từ <c>Templates/{thư mục}/template.json</c>.
/// Tọa độ tính bằng pixel trên canvas. Mô tả đầy đủ: docs/TEMPLATE_SCHEMA.md.
/// </summary>
public sealed record PhotoTemplate
{
    /// <summary>Mã template: chỉ gồm a-z, 0-9 và dấu gạch ngang.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required TemplateCanvas Canvas { get; init; }

    /// <summary>Các ô ảnh. Nhiều ô có thể dùng chung một ảnh (cùng PhotoIndex).</summary>
    public IReadOnlyList<TemplateSlot> Slots { get; init; } = [];

    public IReadOnlyList<TemplateDecoration> Decorations { get; init; } = [];

    /// <summary>Thư mục chứa template.json; gốc của mọi <see cref="TemplateDecoration.AssetPath"/>. Không đọc từ JSON.</summary>
    [JsonIgnore]
    public string? AssetDirectory { get; init; }

    /// <summary>Số ảnh cần chụp = PhotoIndex lớn nhất + 1.</summary>
    [JsonIgnore]
    public int RequiredPhotoCount => Slots.Count == 0 ? 0 : Slots.Max(s => s.PhotoIndex) + 1;
}

/// <summary>Kích thước canvas và kích thước vật lý (qua DPI).</summary>
public sealed record TemplateCanvas
{
    public required int WidthPixels { get; init; }

    public required int HeightPixels { get; init; }

    /// <summary>Mật độ điểm ảnh khi in; 1200×1800 px ở 300 DPI = 4×6 inch.</summary>
    public int Dpi { get; init; } = 300;

    public string BackgroundColor { get; init; } = "#FFFFFF";

    /// <summary>Chiều rộng trang PDF (1 inch = 72 point).</summary>
    [JsonIgnore]
    public double PageWidthPoints => WidthPixels * 72.0 / Dpi;

    [JsonIgnore]
    public double PageHeightPoints => HeightPixels * 72.0 / Dpi;
}

/// <summary>Cách đặt ảnh vào khung.</summary>
public enum ImageFit
{
    /// <summary>Lấp đầy khung, giữ tỉ lệ, cắt phần dư (theo focal point).</summary>
    Cover,

    /// <summary>Hiển thị toàn bộ ảnh trong khung, giữ tỉ lệ; phần trống là nền của khung.</summary>
    Contain,
}

/// <summary>Điểm ưu tiên giữ lại khi cắt ảnh, tọa độ tương đối 0..1 (0.5, 0.5 = tâm ảnh).</summary>
public sealed record FocalPoint
{
    public double X { get; init; } = 0.5;

    public double Y { get; init; } = 0.5;
}

/// <summary>Một ô ảnh.</summary>
public sealed record TemplateSlot
{
    /// <summary>Mã phần tử (tùy chọn, phải duy nhất trong template nếu khai báo).</summary>
    public string? Id { get; init; }

    /// <summary>Ảnh nguồn (0-based) theo thứ tự chụp. Nhiều ô có thể dùng chung một ảnh.</summary>
    public required int PhotoIndex { get; init; }

    public required int X { get; init; }

    public required int Y { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public int CornerRadius { get; init; }

    public ImageFit Fit { get; init; } = ImageFit.Cover;

    /// <summary>Theo hướng ảnh hiển thị (đã lật gương nếu có), dùng khi <see cref="Fit"/> = cover.</summary>
    public FocalPoint FocalPoint { get; init; } = new();

    /// <summary>Màu nền trong khung (thấy được khi fit = contain). Bỏ trống = trong suốt.</summary>
    public string? BackgroundColor { get; init; }

    public int BorderWidth { get; init; }

    public string? BorderColor { get; init; }

    /// <summary>Thứ tự vẽ: nhỏ vẽ trước. Mặc định 0.</summary>
    public int ZIndex { get; init; }
}

public enum DecorationKind
{
    Rectangle,
    Ellipse,
    Text,
    Image,
}

public enum TextAlignment
{
    Center,
    Left,
    Right,
}

/// <summary>Thành phần trang trí: hình chữ nhật, elip, chữ hoặc ảnh (PNG/JPEG trong thư mục template).</summary>
public sealed record TemplateDecoration
{
    /// <summary>Mã phần tử (tùy chọn, phải duy nhất trong template nếu khai báo).</summary>
    public string? Id { get; init; }

    public required DecorationKind Type { get; init; }

    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Thứ tự vẽ: nhỏ vẽ trước. Mặc định 0 (cùng zIndex: ô ảnh trước, trang trí sau, theo thứ tự khai báo).</summary>
    public int ZIndex { get; init; }

    /// <summary>Màu nền (hình) hoặc màu chữ. Định dạng #RRGGBB hoặc #AARRGGBB.</summary>
    public string? Color { get; init; }

    public string? StrokeColor { get; init; }

    public int StrokeWidth { get; init; }

    public int CornerRadius { get; init; }

    /// <summary>Nội dung chữ; hỗ trợ {date}, {time}, {datetime}.</summary>
    public string? Text { get; init; }

    /// <summary>Tên font cài trên Windows. Bỏ trống = Segoe UI.</summary>
    public string? FontFamily { get; init; }

    public int FontSizePixels { get; init; } = 48;

    public bool Bold { get; init; }

    public TextAlignment TextAlign { get; init; } = TextAlignment.Center;

    /// <summary>Đường dẫn ảnh tương đối với thư mục template, ví dụ "assets/overlay.png".</summary>
    public string? AssetPath { get; init; }

    /// <summary>Cách đặt ảnh trang trí vào khung. Mặc định contain (không cắt, không kéo giãn).</summary>
    public ImageFit Fit { get; init; } = ImageFit.Contain;
}
